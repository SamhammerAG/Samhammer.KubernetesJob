# Plan: Deploy error handling in Samhammer.KubernetesJob (MET-4944)

Sources:
- Concept: https://samhammer.atlassian.net/wiki/spaces/~rschmitt/pages/2647719941/MET-4944+Automatic+data+item+import+retry+Concept
- Story: https://samhammer.atlassian.net/browse/MET-4944
- Tasks: [MET-4966](https://samhammer.atlassian.net/browse/MET-4966), [MET-4967](https://samhammer.atlassian.net/browse/MET-4967), [MET-4968](https://samhammer.atlassian.net/browse/MET-4968)

This covers only the library part of the concept. The Metis side (`RetryPending`, the retry decision, the cleanup checks) builds on these changes. There are three steps, one per task. Each step is its own PR with its own tests.

---

## Step 1: MET-4966, `CreateJobAsync` reports failures

**Problem:** `AbstractJobKubernetesClient.CreateJobAsync` (`src/Samhammer.KubernetesJob/Kubernetes/AbstractJobKubernetesClient.cs:74`) catches every exception and only logs it. The caller can't tell that the deploy failed.

**Changes:**
- Add an exception type `KubernetesJobDeployException(jobName, statusCode, response, innerException)` in `Kubernetes/`.
- `CreateJobAsync`: keep logging with status code and response, then throw `KubernetesJobDeployException`. Do this for both `HttpOperationException` and unexpected exceptions.
- When the created job's status is not `Running`, keep it as a log only. The job exists in that case, so throwing would leave an orphaned job that then gets retried.
- `TryDeleteJobImmediately` stays best-effort. If the delete fails, the create usually fails too (409 Conflict), and that failure is now reported.
- Document on `IBaseJobKubernetesClient.DeployJobAsync` (XML doc) that it throws.

**Tests:** a new `AbstractJobKubernetesClientTest`. `GetKubernetes()` currently does `new k8s.Kubernetes(...)` directly, so this likely needs a `protected virtual IKubernetes GetKubernetes()` seam. If that's too much for this step, cover it through the deploy service tests in step 2.

> ⚠️ **Release:** after this step `DeployJobAsync` throws, and step 2 doesn't catch it yet. A queued job that fails would then break every `DeployQueuedJobs` tick. Merge step 1 freely, but **don't tag or release until step 2 is merged.**

---

## Step 2: MET-4967, deploy error hooks and `OnJobStarted` for queued jobs

### 2a. New hook and handling in `DeployOrQueueJob`
File: `src/Samhammer.KubernetesJob/Job/AbstractJobDeployService.cs:38`

- New hook: `public abstract Task OnJobDeployFailed(TJobModel jobModel, Exception exception)`. It's abstract so every consumer has to decide how to handle deploy failures. Nothing gets swallowed silently.
- Wrap the Redis and Kubernetes operations after the lock (`GetAsync`, `AddJobToQueueAsync` / `AddJobToRunningListAsync`, `DeployJobAsync`) in a try/catch. On failure:
  1. Best effort, with its own try/catch: `RemoveRunningJobAsync(jobName)` and `RemoveQueuedJobAsync(jobName)`. These can fail too if Redis is down.
  2. `LogError`, then `await OnJobDeployFailed(jobModel, e)`.
  3. The library doesn't rethrow. The consumer decides in the hook: the data item import sets `RetryPending`, other jobs set a failure status, or rethrow (`throw` / `ExceptionDispatchInfo.Throw(exception)`) to keep the old behaviour.
- The success hooks (`OnJobStarted`, `OnJobQueued`) stay **outside** the try/catch. If Mongo fails after a successful deploy, that must not trigger the failure hook, or a running job would get retried.

### 2b. Hooks in `DeployQueuedJobs`
File: `src/Samhammer.KubernetesJob/Job/AbstractJobDeployService.cs:100`

- **The hooks (`OnJobStarted`, `OnJobDeployFailed`) receive the full job model, loaded from Mongo**, the same as in the direct deploy.
- To load it, the Redis entry needs the model's key: **add `JobId` to `QueueJobRedisMessage`** and set it in `DeployOrQueueJob`. This is only the lookup key, never passed to the hooks. Avoid parsing the job name, because `CreateJobName` is defined by the consumer and can't be reversed reliably. Matching against `GetRunningAndCreatedJobs` doesn't work either, because that list isn't guaranteed to contain queued jobs.
- Add `Task<T> GetById(string id)` to `IBaseJobRepository<T>`. This assumes `BaseRepositoryMongo` in Metis already provides it with a matching signature. If not, Metis needs a one-line implementation.
- For each queued job:
  - Old entry without `JobId` (from before the release): deploy as today with no hooks, and log a warning. This only matters during the transition.
  - Load the model with `JobRepository.GetById(job.JobId)`. If it's `null`, remove the entry from the queue, log a warning and skip it.
  - Wrap `DeployJobAsync` and `MoveJobToRunningAsync` in a try/catch:
    - Success: `OnJobStarted(model)`, then `runningJobCount++`.
    - Failure: `RemoveQueuedJobAsync` and `RemoveRunningJobAsync` (best effort), then `OnJobDeployFailed(model, e)`, then **continue with the next job**. The slot isn't counted.
- The entry is removed from the queue on failure, so it no longer gets stuck in `Queued` forever.
- If the hook rethrows, the tick ends early, which is the old behaviour. The failed entry is already removed from the queue, so it doesn't block later ticks.

### 2c. Tests in `AbstractJobDeployTest`
- Direct deploy where `DeployJobAsync` throws: `OnJobDeployFailed` is called, the job is removed from the running list, and `OnJobStarted` isn't called.
- Direct deploy where `AddJobToQueueAsync` or `AddJobToRunningListAsync` throws: `OnJobDeployFailed` is called.
- Queue deploy succeeds: `OnJobStarted` is called with the loaded model.
- Queue deploy fails for job 1: it's removed from the queue, the hook is called, and job 2 still gets deployed.
- The hook rethrows: the exception reaches the caller of `DeployOrQueueJob` / `DeployQueuedJobs`.
- Old entry without `JobId`: deployed with no hooks.
- Model not found: the entry is removed and nothing is deployed.

### 2d. README
Document `OnJobDeployFailed` (required, with examples: set a failure status, or rethrow to keep the old behaviour), `GetById` and the "hooks now also fire for queued jobs" behaviour.

---

## Step 3: MET-4968, status mapping and Redis cleanup

### 3a. Only a 404 means "deleted"
File: `src/Samhammer.KubernetesJob/Kubernetes/AbstractJobKubernetesClient.cs:133-172`

- Today `ReadJobAsync` returns `null` on **every** error, so `MapJobStatus` reports `Deleted`. A short API outage then marks every running job as deleted.
- Add `KubernetesJobStatus.Unknown`, appended at the end of the enum so existing values keep their positions.
- `ReadJobAsync` returns `null` only for a 404. Any other error throws, or `GetJobStatusAsync` catches it and returns `Unknown`. Recommended: return `Unknown`, so the cleanup loop keeps going through the other jobs.
- `CleanupJobs` ignores `Unknown`: it logs a warning and calls no hook.

### 3b. A failed job list must not clear the running list
File: `src/Samhammer.KubernetesJob/Job/AbstractJobCleanupService.cs:87`

- `GetJobsAsync` returns an empty list on error. `CleanupRedisQueue` then removes **all** running jobs from Redis, so `MaxRunningJobs` stops being enforced.
- Fix: `GetJobsAsync` rethrows, or returns `null`. `CleanupRedisQueue` catches it, logs, and skips this run.

### 3c. Tests
- `GetJobStatusAsync` maps a 404 to `Deleted` and a 500 to `Unknown`. This needs the `GetKubernetes()` seam from step 1.
- `CleanupJobs` with `Unknown`: neither hook is called.
- `CleanupRedisQueue` when `GetJobsAsync` fails: `RemoveRunningJobsExceptAsync` isn't called.

Step 3 doesn't depend on steps 1 and 2, so it can be done in parallel or first.

---

## Release and dependencies

| Order | PR | Breaking for consumers? |
|---|---|---|
| 1 | MET-4966 | Behaviour only: `DeployJobAsync` throws |
| 2 | MET-4967 | Yes: `IBaseJobRepository.GetById`, new abstract `OnJobDeployFailed` (compile error until implemented), new `JobId` in the Redis entry (compatible both ways) |
| 3 | MET-4968 | Small: new enum value `Unknown` |

Release one tag after steps 1 and 2, e.g. **10.1.0**. Breaking interface changes like this are usually a minor bump here. Step 3 can go into the same release or a later one. Metis then updates the package and builds `RetryPending` on top of it.

**Not part of this library:** `RetryPending`, the retry counter and delays, the "never started" check (Metis uses the existing `DeleteJobAsync`), prioritising retries, and recovering when Redis loses the queue.

---

## Decisions
- **`OnJobDeployFailed` is abstract.** The library catches the deploy error, cleans up Redis and calls the hook, but doesn't rethrow. Because the hook is abstract, no consumer loses an error silently. Each one has to implement it explicitly:
  - Data item import: set `RetryPending`.
  - Other jobs: set a failure status, or rethrow to keep the old behaviour (Redis errors used to throw, Kubernetes create errors were only logged).
  - The other hooks (`OnJobStarted`, `OnJobQueued`, `OnCouldNotAcquireLock`) stay virtual. Only the failure hook is mandatory.

- **Hooks for queued jobs receive the full job model**, loaded from Mongo via `IBaseJobRepository.GetById`. The Redis entry stores only `JobId` as the lookup key.
