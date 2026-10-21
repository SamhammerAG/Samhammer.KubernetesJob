# Plan: Deploy error handling in Samhammer.KubernetesJob (MET-4944)

Sources:
- Concept: https://samhammer.atlassian.net/wiki/spaces/~rschmitt/pages/2647719941/MET-4944+Automatic+data+item+import+retry+Concept
- Story: https://samhammer.atlassian.net/browse/MET-4944
- Tasks: [MET-4966](https://samhammer.atlassian.net/browse/MET-4966), [MET-4967](https://samhammer.atlassian.net/browse/MET-4967), [MET-4968](https://samhammer.atlassian.net/browse/MET-4968)

This covers only the library part of the concept. The Metis side (`RetryPending`, the retry decision, the cleanup checks) builds on these changes. There are three steps, one per task. Each step is its own PR with its own tests.

---

## Step 1: MET-4966, `CreateJobAsync` reports failures

**Status:** done and reviewed. `CreateJobAsync` throws `KubernetesJobDeployException`; test seam via `protected virtual IKubernetes GetKubernetes()`. The `Running` check after create stays unchanged for now.

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

**Status:** done and reviewed, including the direct deploy tests from 2c. `CreateJobName` / `CreateJobTemplate` stay outside the try/catch (config errors still throw as before).
File: `src/Samhammer.KubernetesJob/Job/AbstractJobDeployService.cs:38`

- New hook: `public virtual Task OnJobDeployFailed(TJobModel jobModel, Exception exception)`, doing nothing by default like the other hooks.
- Wrap the Redis and Kubernetes operations after the lock (`GetAsync`, `AddJobToQueueAsync` / `AddJobToRunningListAsync`, `DeployJobAsync`) in a try/catch. On failure:
  1. `LogError`.
  2. Best effort, with its own try/catch: `RemoveQueuedJobAsync(jobName)` and `RemoveRunningJobAsync(jobName)`. These can fail too if Redis is down.
  3. `await OnJobDeployFailed(jobModel, e)`.
  4. The library doesn't rethrow. The consumer decides in the hook: the data item import sets `RetryPending`, other jobs set a failure status, or rethrow (`throw` / `ExceptionDispatchInfo.Throw(exception)`) to keep the old behaviour.
- The success hooks (`OnJobStarted`, `OnJobQueued`) stay **outside** the try/catch. If Mongo fails after a successful deploy, that must not trigger the failure hook, or a running job would get retried.

### 2b. Hooks in `DeployQueuedJobs`

**Status:** done and reviewed, including the queue tests from 2c. Implementation notes:
- Entries without `JobId` and entries whose model isn't found take the same path: new order and Redis cleanup, only the hooks are skipped (with a warning). Nothing is removed from the queue just because the model is missing. The hooks never receive `null`, so consumers don't need null checks.
- The model is only loaded after the try/catch, right before the hooks. So a Mongo outage doesn't block the deploy itself.
- `DeployOrQueueJob` and `DeployQueuedJobs` use the same pattern: the catch only logs, cleans up Redis and stores the exception. After the try/catch, the stored exception decides which hook is called.
- Log messages contain `{JobId} ({JobName})` wherever both are available, so they can be matched with the Kubernetes client's logs (which only know the job name).

File: `src/Samhammer.KubernetesJob/Job/AbstractJobDeployService.cs:145`

- **The hooks (`OnJobStarted`, `OnJobDeployFailed`) receive the full job model, loaded from Mongo**, the same as in the direct deploy.
- To load it, the Redis entry needs the model's key: **add `JobId` to `QueueJobRedisMessage`** and set it in `DeployOrQueueJob`. This is only the lookup key, never passed to the hooks. Avoid parsing the job name, because `CreateJobName` is defined by the consumer and can't be reversed reliably. Matching against `GetRunningAndCreatedJobs` doesn't work either, because that list isn't guaranteed to contain queued jobs.
- Add `Task<T> GetById(string id)` to `IBaseJobRepository<T>`. This assumes `BaseRepositoryMongo` in Metis already provides it with a matching signature. If not, Metis needs a one-line implementation.
- For each queued job:
  - Load the model with `JobRepository.GetById(job.JobId)`. If there's no `JobId` (old entry from before the release, only during the transition) or the model isn't found (should never happen): log a warning and deploy without hooks.
  - Swap the order to `MoveJobToRunningAsync`, then `DeployJobAsync` (like `AddJobToRunningListAsync` before `DeployJobAsync` in 2a). With the old order, a failing move after a successful deploy would call `OnJobDeployFailed` for a job that is running, and the retry would start a duplicate. With the new order, a failing deploy is undone by the cleanup below.
  - Wrap `MoveJobToRunningAsync` and `DeployJobAsync` in a try/catch:
    - Success: `runningJobCount++`, then `OnJobStarted(model)`.
    - Failure: `RemoveQueuedJobAsync` and `RemoveRunningJobAsync` (best effort), then `OnJobDeployFailed(model, e)`, then **continue with the next job**. The slot isn't counted.
- The entry is removed from the queue on failure, so it no longer gets stuck in `Queued` forever.
- If the hook rethrows, the tick ends early, which is the old behaviour. The failed entry is already removed from the queue, so it doesn't block later ticks.

### 2c. Tests in `AbstractJobDeployTest`

**Status:** done. All cases below are covered. The test class uses a partial substitute (`Substitute.ForPartsOf`) instead of a hand-written test subclass, so hook calls are checked with `Received`.

- Direct deploy where `DeployJobAsync` throws: `OnJobDeployFailed` is called, the job is removed from the running list, and `OnJobStarted` isn't called.
- Direct deploy where `AddJobToQueueAsync` or `AddJobToRunningListAsync` throws: `OnJobDeployFailed` is called.
- Queue deploy succeeds: `OnJobStarted` is called with the loaded model.
- Queue deploy fails for job 1: it's removed from the queue, the hook is called, and job 2 still gets deployed.
- The hook rethrows: the exception reaches the caller of `DeployOrQueueJob` / `DeployQueuedJobs`.
- Old entry without `JobId`: deployed with no hooks.
- Model not found: deployed with no hooks.

### 2d. README

**Status:** done and reviewed.

Document `OnJobDeployFailed` (required, with examples: set a failure status, or rethrow to keep the old behaviour), `GetById` and the "hooks now also fire for queued jobs" behaviour.

---

## Step 3: MET-4968, status mapping and Redis cleanup

**Status:** done and reviewed.
- 3a: different from the bullets below, `ReadJobAsync` still doesn't throw. It returns a tuple `(V1Job Job, HttpStatusCode? StatusCode)`, and `MapJobStatus` uses the status code when the job is `null`: only a 404 means `Deleted`, anything else is `Unknown`. No consumer (Asimov, Metis) calls `ReadJobAsync`, so changing its return type is safe.
- 3b: `GetJobsAsync` rethrows, `CleanupRedisQueue` catches it, logs a warning and skips the run.
- 3c: `{StatusCode}` logged as `(int?)statusCode`, so the field has the same numeric type as in our other projects.
- 3d: all tests below are covered. New test seam `protected virtual string ReadTemplate()`, so the client tests don't need a template file.

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

### 3c. Log improvements in `AbstractJobKubernetesClient`
- Rename the placeholder `{Status}` to `{StatusCode}`. `Status` is too generic and can clash with other status logs (e.g. the job status).
- Log the status code as a number: pass `(int?)statusCode` instead of the `HttpStatusCode` enum, so `404` is logged instead of `NotFound`. That's how the status code is logged in our other projects.
- Affected: the create, read and delete logs (`LogError` and the `LogDebug` for 404). Also remove the extra `'` at the end of the read `LogDebug` (`'{Status}''`).

### 3d. Tests
- `GetJobStatusAsync` maps a 404 to `Deleted` and a 500 to `Unknown`. This needs the `GetKubernetes()` seam from step 1.
- `CleanupJobs` with `Unknown`: neither hook is called.
- `CleanupRedisQueue` when `GetJobsAsync` fails: `RemoveRunningJobsExceptAsync` isn't called.

Step 3 doesn't depend on steps 1 and 2, so it can be done in parallel or first.

---

## Release and dependencies

| Order | PR | Breaking for consumers? |
|---|---|---|
| 1 | MET-4966 | Behaviour only: `DeployJobAsync` throws |
| 2 | MET-4967 | Yes: `IBaseJobRepository.GetById`, deploy errors no longer thrown unless `OnJobDeployFailed` rethrows (release notes), new `JobId` in the Redis entry (compatible both ways) |
| 3 | MET-4968 | Small: new enum value `Unknown` |

Release one tag after steps 1 and 2, e.g. **10.1.0**. Breaking interface changes like this are usually a minor bump here. Step 3 can go into the same release or a later one. Metis then updates the package and builds `RetryPending` on top of it.

**Deferred / out of scope (from the step 1 review):**
- `GetKubernetes()` creates a new, never-disposed `k8s.Kubernetes` client per call. Out of scope for MET-4944, as it could be a bigger change.

**Not part of this library:** `RetryPending`, the retry counter and delays, the "never started" check (Metis uses the existing `DeleteJobAsync`), prioritising retries, and recovering when Redis loses the queue.

---

## Decisions
- **`OnJobDeployFailed` is virtual**, like all other hooks, and does nothing by default. The library catches the deploy error, logs it, cleans up Redis and calls the hook, but doesn't rethrow.
  - Data item import: set `RetryPending`.
  - Other jobs: set a failure status, or rethrow to keep the old behaviour (Redis errors used to throw, Kubernetes create errors were only logged).
  - **Release notes:** mention that Redis errors in `DeployOrQueueJob` are no longer thrown unless `OnJobDeployFailed` rethrows them.

- **Duplicate error logging is intended.** A Kubernetes create error is logged by `CreateJobAsync` (status code and response) and again by the deploy service (`JobId`). One log comes from the client, the other from the deploy logic.

- **Hooks for queued jobs receive the full job model**, loaded from Mongo via `IBaseJobRepository.GetById`. The Redis entry stores only `JobId` as the lookup key.
