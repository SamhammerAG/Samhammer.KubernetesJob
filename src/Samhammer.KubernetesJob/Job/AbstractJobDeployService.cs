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
            if (Options.Value.DisableJob)
            {
                Logger.LogInformation("Skipping deployment of job {JobId}, because it is disabled", jobModel.Id);
                return;
            }

            jobModel.DateCreated = DateTime.UtcNow;
            await JobRepository.Save(jobModel);

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
                JobName = jobName,
                JobTemplate = jobTemplate
            };

            var jobManagerData = await JobRedisQueueClient.GetAsync();
            if (jobManagerData.RunningJobs.Count >= Options.Value.MaxRunningJobs || jobManagerData.QueuedJobs.Any())
            {
                Logger.LogInformation("Start queueing job {JobId}", jobModel.Id);
                await JobRedisQueueClient.AddJobToQueueAsync(jobContract);
                await OnJobQueued(jobModel);
                Logger.LogInformation("Completed queueing job {JobId}", jobModel.Id);
            }
            else
            {
                Logger.LogInformation("Start deploying {JobId}", jobModel.Id);
                await JobRedisQueueClient.AddJobToRunningListAsync(jobContract);
                await JobKubernetesClient.DeployJobAsync(jobContract.JobTemplate);
                await OnJobStarted(jobModel);
                Logger.LogInformation("Completed deploying job {JobId}", jobModel.Id);
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

                Logger.LogInformation("Start deploying job {JobName} from redis queue", job.JobName);

                await JobKubernetesClient.DeployJobAsync(job.JobTemplate);
                await JobRedisQueueClient.MoveJobToRunningAsync(job);

                Logger.LogInformation("Completed deploying job {JobName} from redis queue", job.JobName);

                runningJobCount++;
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

            Logger.LogInformation("Aborting job {JobId} (requested by {UserName})", jobModel.Id, userName);
            await OnJobAborting(jobModel);

            var jobName = JobKubernetesClient.CreateJobName(jobModel);

            await JobRedisQueueClient.RemoveQueuedJobAsync(jobName);
            await JobKubernetesClient.DeleteJobAsync(jobModel);
            await JobRedisQueueClient.RemoveRunningJobAsync(jobName);

            Logger.LogInformation("Completed aborting job {JobId}", jobModel.Id);
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
