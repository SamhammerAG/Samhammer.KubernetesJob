using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.MongoDb;
using Samhammer.KubernetesJob.Redis;

namespace Samhammer.KubernetesJob.Job
{
    public abstract class AbstractJobCleanupService<TJobModel, TJobRepository, TJobKubernetesClient, TJobRedisQueueClient>
        where TJobModel : BaseJobModel, new()
        where TJobRepository : IBaseJobRepository<TJobModel>
        where TJobKubernetesClient : IBaseJobKubernetesClient<TJobModel>
        where TJobRedisQueueClient : IBaseRedisQueueClient
    {
        public ILogger<AbstractJobCleanupService<TJobModel, TJobRepository, TJobKubernetesClient, TJobRedisQueueClient>> Logger { get; }

        protected TJobRepository JobRepository { get; }

        private TJobKubernetesClient JobKubernetesClient { get; }

        private TJobRedisQueueClient JobRedisQueueClient { get; }

        protected AbstractJobCleanupService(ILogger<AbstractJobCleanupService<TJobModel, TJobRepository, TJobKubernetesClient, TJobRedisQueueClient>> logger, TJobRepository jobRepository, TJobKubernetesClient jobKubernetesClient, TJobRedisQueueClient jobRedisQueueClient)
        {
            Logger = logger;
            JobRepository = jobRepository;
            JobKubernetesClient = jobKubernetesClient;
            JobRedisQueueClient = jobRedisQueueClient;
        }

        public async Task CleanupJobs()
        {
            var jobModels = await JobRepository.GetRunningAndCreatedJobs();

            foreach (var jobModel in jobModels)
            {
                // MET-3906 We wait one minute to give kubernetes time to start the job
                var secondsCreated = (DateTime.UtcNow - jobModel.DateCreated).TotalSeconds;
                if (secondsCreated < 60)
                {
                    continue;
                }

                var kubeStatus = await JobKubernetesClient.GetJobStatusAsync(jobModel);

                switch (kubeStatus)
                {
                    case KubernetesJobStatus.Deleted:
                        Logger.LogWarning("Job {JobId} is deleted", jobModel.Id);
                        await OnKubernetesJobDeleted(jobModel);
                        break;
                    case KubernetesJobStatus.Failed:
                        Logger.LogWarning("Job {JobId} is failed", jobModel.Id);
                        await OnKubernetesJobFailed(jobModel);
                        break;
                }
            }
        }

        public virtual Task OnKubernetesJobDeleted(TJobModel jobModel)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnKubernetesJobFailed(TJobModel jobModel)
        {
            return Task.CompletedTask;
        }

        public async Task CleanupRedisQueue()
        {
            await using var redLock = await JobRedisQueueClient.CreateLockAsync();

            if (!redLock.IsAcquired)
            {
                Logger.LogWarning("Could not acquire lock to cleanup job redis queue");
                return;
            }

            var runningJobs = await JobKubernetesClient.GetJobsAsync(KubernetesJobStatus.Running);
            var runningJobsNames = runningJobs.Select(c => c.Metadata.Name).ToList();

            var removedJobNames = await JobRedisQueueClient.RemoveRunningJobsExceptAsync(runningJobsNames);
            if (removedJobNames.Any())
            {
                Logger.LogInformation("Cleaned up job redis queue. Removed jobNames {RemovedJobNames}", string.Join(",", removedJobNames));
            }
        }
    }

    public interface IBaseJobCleanupService
    {
        Task CleanupJobs();

        Task CleanupRedisQueue();
    }
}
