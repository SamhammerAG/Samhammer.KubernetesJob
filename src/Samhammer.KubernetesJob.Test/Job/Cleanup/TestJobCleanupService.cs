using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Samhammer.KubernetesJob.Job;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.MongoDb;
using Samhammer.KubernetesJob.Redis;

namespace Samhammer.KubernetesJob.Test.Job.Cleanup
{
    public class TestJobCleanupService(
        ILogger<TestJobCleanupService> logger,
        IBaseJobRepository<TestCleanupJobModel> jobRepository,
        IBaseJobKubernetesClient<TestCleanupJobModel> kubernetesClient,
        IBaseRedisQueueClient redisQueueClient) : AbstractJobCleanupService<TestCleanupJobModel, IBaseJobRepository<TestCleanupJobModel>, IBaseJobKubernetesClient<TestCleanupJobModel>, IBaseRedisQueueClient>
            (logger, jobRepository, kubernetesClient, redisQueueClient)
    {
        public List<string> DeletedJobIds { get; } = new();

        public List<string> FailedJobIds { get; } = new();

        public override Task OnKubernetesJobDeleted(TestCleanupJobModel cleanupJobModel)
        {
            DeletedJobIds.Add(cleanupJobModel.Id);
            return Task.CompletedTask;
        }

        public override Task OnKubernetesJobFailed(TestCleanupJobModel cleanupJobModel)
        {
            FailedJobIds.Add(cleanupJobModel.Id);
            return Task.CompletedTask;
        }
    }
}
