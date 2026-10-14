using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.MongoDb;
using Samhammer.KubernetesJob.Redis;

namespace Samhammer.KubernetesJob.Job
{
    public abstract class AbstractJobDeployService<TJobModel, TJobRepository, TOptions, TJobKubernetesClient, TJobRedisQueueClient> : IBaseJobDeployService<TJobModel>
        where TJobModel : BaseJobModel, new()
        where TOptions : class, IBaseJobOptionsDeploy
        where TJobKubernetesClient : IBaseJobKubernetesClient<TJobModel>
        where TJobRedisQueueClient : IBaseRedisQueueClient
        where TJobRepository : IBaseJobRepository<TJobModel>
    {
        public ILogger<AbstractJobDeployService<TJobModel, TJobRepository, TOptions, TJobKubernetesClient, TJobRedisQueueClient>> Logger { get; }

        protected IOptions<TOptions> Options { get; }

        private TJobKubernetesClient JobKubernetesClient { get; }

        private TJobRedisQueueClient JobRedisQueueClient { get; }

        protected TJobRepository JobRepository { get; }

        protected AbstractJobDeployService(ILogger<AbstractJobDeployService<TJobModel, TJobRepository, TOptions, TJobKubernetesClient, TJobRedisQueueClient>> logger, IOptions<TOptions> options, TJobRepository jobRepository, TJobKubernetesClient jobKubernetesClient, TJobRedisQueueClient jobRedisQueueClient)
        {
            Logger = logger;
            Options = options;
            JobRepository = jobRepository;
            JobKubernetesClient = jobKubernetesClient;
            JobRedisQueueClient = jobRedisQueueClient;
        }

        public async Task DeployOrQueueJob(TJobModel jobModel)
        {
            jobModel.DateCreated = DateTime.UtcNow;
            await JobRepository.Save(jobModel);

            if (Options.Value.DisableJob)
            {
                Logger.LogInformation("Skipping deployment of job {JobId}, because it is disabled", jobModel.Id);
                return;
            }

            await using var redLock = await JobRedisQueueClient.CreateLockAsync();

            if (!redLock.IsAcquired)
            {
                Logger.LogWarning("Could not acquire lock to send job {JobId} to queue", jobModel.Id);
                await OnCouldNotAcquireLock(jobModel);
                return;
            }

            var jobName = JobKubernetesClient.CreateJobName(jobModel);
            var jobTemplate = JobKubernetesClient.CreateJobTemplate(jobModel);

            var jobContract = new QueueJobRedisMessage
            {
                JobId = jobModel.Id,
                JobName = jobName,
                JobTemplate = jobTemplate
            };

            var isQueued = false;
            Exception deployException = null;

            try
            {
                var jobManagerData = await JobRedisQueueClient.GetAsync();
                isQueued = jobManagerData.RunningJobs.Count >= Options.Value.MaxRunningJobs || jobManagerData.QueuedJobs.Any();

                if (isQueued)
                {
                    Logger.LogInformation("Start queueing job {JobId} ({JobName})", jobModel.Id, jobName);
                    await JobRedisQueueClient.AddJobToQueueAsync(jobContract);
                }
                else
                {
                    Logger.LogInformation("Start deploying job {JobId} ({JobName})", jobModel.Id, jobName);
                    await JobRedisQueueClient.AddJobToRunningListAsync(jobContract);
                    await JobKubernetesClient.DeployJobAsync(jobContract.JobTemplate);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Deploying job {JobId} ({JobName}) failed", jobModel.Id, jobName);
                await TryRemoveJobFromRedis(jobName);
                deployException = e;
            }

            // Hooks are called outside the try block, so a failing hook doesn't mark a deployed job as failed
            if (deployException != null)
            {
                await OnJobDeployFailed(jobModel, deployException);
            }
            else if (isQueued)
            {
                await OnJobQueued(jobModel);
                Logger.LogInformation("Completed queueing job {JobId} ({JobName})", jobModel.Id, jobName);
            }
            else
            {
                await OnJobStarted(jobModel);
                Logger.LogInformation("Completed deploying job {JobId} ({JobName})", jobModel.Id, jobName);
            }
        }

        private async Task TryRemoveJobFromRedis(string jobName)
        {
            try
            {
                await JobRedisQueueClient.RemoveQueuedJobAsync(jobName);
                await JobRedisQueueClient.RemoveRunningJobAsync(jobName);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Removing job {JobName} from redis after failed deployment failed", jobName);
            }
        }

        public virtual Task OnCouldNotAcquireLock(TJobModel jobModel)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnJobStarted(TJobModel jobModel)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnJobQueued(TJobModel jobModel)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnJobDeployFailed(TJobModel jobModel, Exception exception)
        {
            return Task.CompletedTask;
        }

        public async Task DeployQueuedJobs()
        {
            await using var redLock = await JobRedisQueueClient.CreateLockAsync();

            if (!redLock.IsAcquired)
            {
                Logger.LogWarning("Could not acquire lock for deploying jobs. Waiting for next schedule");
                return;
            }

            var jobManagerData = await JobRedisQueueClient.GetAsync();
            var runningJobCount = jobManagerData.RunningJobs.Count;

            foreach (var job in jobManagerData.QueuedJobs)
            {
                if (!redLock.IsAcquired || runningJobCount >= Options.Value.MaxRunningJobs)
                {
                    break;
                }

                Logger.LogInformation("Start deploying job {JobId} ({JobName}) from redis queue", job.JobId, job.JobName);

                Exception deployException = null;

                try
                {
                    // Moved to running before deploying, so a failed move can't leave a deployed job marked as failed
                    await JobRedisQueueClient.MoveJobToRunningAsync(job);
                    await JobKubernetesClient.DeployJobAsync(job.JobTemplate);

                    // Only a successful deployment takes a slot. A failed one throws before this
                    runningJobCount++;
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Deploying job {JobId} ({JobName}) from redis queue failed", job.JobId, job.JobName);
                    await TryRemoveJobFromRedis(job.JobName);
                    deployException = e;
                }

                // Hooks are called outside the try block, so a failing hook doesn't mark a deployed job as failed.
                // JobId is null for entries queued by an older version. The null check can be removed in a future version, once no such entries are left.
                var jobModel = job.JobId != null ? await JobRepository.GetById(job.JobId) : null;

                if (jobModel == null)
                {
                    Logger.LogWarning("No job model found for queued job {JobId} ({JobName}). Skipping hooks", job.JobId, job.JobName);
                }
                else if (deployException != null)
                {
                    await OnJobDeployFailed(jobModel, deployException);
                }
                else
                {
                    await OnJobStarted(jobModel);
                    Logger.LogInformation("Completed deploying job {JobId} ({JobName}) from redis queue", job.JobId, job.JobName);
                }
            }
        }

        public async Task AbortJob(TJobModel jobModel, string userName)
        {
            if (Options.Value.DisableJob)
            {
                Logger.LogInformation("Skip aborting job {JobId}, because it is disabled", jobModel.Id);
                await OnJobAborting(jobModel);
                return;
            }

            await using var redLock = await JobRedisQueueClient.CreateLockAsync();

            if (!redLock.IsAcquired)
            {
                Logger.LogWarning("Could not acquire lock to abort job {JobId}", jobModel.Id);
                throw new TimeoutException($"Could not acquire lock to abort job {jobModel.Id}");
            }

            var jobName = JobKubernetesClient.CreateJobName(jobModel);

            Logger.LogInformation("Aborting job {JobId} ({JobName}) (requested by {UserName})", jobModel.Id, jobName, userName);
            await OnJobAborting(jobModel);

            await JobRedisQueueClient.RemoveQueuedJobAsync(jobName);
            await JobKubernetesClient.DeleteJobAsync(jobModel);
            await JobRedisQueueClient.RemoveRunningJobAsync(jobName);

            Logger.LogInformation("Completed aborting job {JobId} ({JobName})", jobModel.Id, jobName);
        }

        public virtual Task OnJobAborting(TJobModel jobModel)
        {
            return Task.CompletedTask;
        }
    }

    public interface IBaseJobDeployService<in TJobModel>
    {
        Task DeployOrQueueJob(TJobModel jobModel);

        Task DeployQueuedJobs();

        Task AbortJob(TJobModel jobModel, string userName);
    }

    public interface IBaseJobOptionsDeploy
    {
        public bool DisableJob { get; set; }

        public int MaxRunningJobs { get; set; }

        public TimeSpan JobDeployInterval { get; set; }
    }
}
