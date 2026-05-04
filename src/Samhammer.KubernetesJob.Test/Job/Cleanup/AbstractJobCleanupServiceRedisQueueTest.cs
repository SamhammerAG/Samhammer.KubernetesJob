using System.Collections.Generic;
using System.Threading.Tasks;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RedLockNet;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.MongoDb;
using Samhammer.KubernetesJob.Redis;
using Xunit;

namespace Samhammer.KubernetesJob.Test.Job.Cleanup
{
    public class AbstractJobCleanupServiceRedisQueueTest
    {
        private readonly TestJobCleanupService service;

        private readonly IBaseJobKubernetesClient<TestCleanupJobModel> kubernetesClient;
        private readonly IBaseRedisQueueClient redisQueueClient;

        public AbstractJobCleanupServiceRedisQueueTest()
        {
            var logger = new NullLogger<TestJobCleanupService>();
            var jobRepository = Substitute.For<IBaseJobRepository<TestCleanupJobModel>>();
            kubernetesClient = Substitute.For<IBaseJobKubernetesClient<TestCleanupJobModel>>();
            redisQueueClient = Substitute.For<IBaseRedisQueueClient>();

            service = new TestJobCleanupService(logger, jobRepository, kubernetesClient, redisQueueClient);
        }

        [Fact]
        public async Task CleanupRedisQueue_EmptyQueue()
        {
            // arrange
            redisQueueClient.AnyRunningJobs().Returns(false);

            // act
            await service.CleanupRedisQueue();

            // assert
            await redisQueueClient.Received(1).AnyRunningJobs();
            await redisQueueClient.DidNotReceive().CreateLockAsync();
            await kubernetesClient.DidNotReceive().GetJobsAsync(Arg.Any<KubernetesJobStatus[]>());
            await redisQueueClient.DidNotReceive().RemoveRunningJobsExceptAsync(Arg.Any<List<string>>());
        }

        [Fact]
        public async Task CleanupRedisQueue_FilledQueueAndLock()
        {
            // arrange
            var redLock = Substitute.For<IRedLock>();
            redLock.IsAcquired.Returns(true);
            redisQueueClient.AnyRunningJobs().Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);
            kubernetesClient.GetJobsAsync(Arg.Any<KubernetesJobStatus[]>()).Returns(new List<V1Job>());
            redisQueueClient.RemoveRunningJobsExceptAsync(Arg.Any<List<string>>()).Returns(new List<string>());

            // act
            await service.CleanupRedisQueue();

            // assert
            await redisQueueClient.Received(1).AnyRunningJobs();
            await redisQueueClient.Received(1).CreateLockAsync();
            await kubernetesClient.Received(1).GetJobsAsync(Arg.Any<KubernetesJobStatus[]>());
            await redisQueueClient.Received(1).RemoveRunningJobsExceptAsync(Arg.Any<List<string>>());
        }

        [Fact]
        public async Task CleanupRedisQueue_FilledQueueButNoLock()
        {
            // arrange
            var redLock = Substitute.For<IRedLock>();
            redLock.IsAcquired.Returns(false);
            redisQueueClient.AnyRunningJobs().Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);
            kubernetesClient.GetJobsAsync(Arg.Any<KubernetesJobStatus[]>()).Returns(new List<V1Job>());
            redisQueueClient.RemoveRunningJobsExceptAsync(Arg.Any<List<string>>()).Returns(new List<string>());

            // act
            await service.CleanupRedisQueue();

            // assert
            await redisQueueClient.Received(1).AnyRunningJobs();
            await redisQueueClient.Received(1).CreateLockAsync();
            await kubernetesClient.DidNotReceive().GetJobsAsync(Arg.Any<KubernetesJobStatus[]>());
            await redisQueueClient.DidNotReceive().RemoveRunningJobsExceptAsync(Arg.Any<List<string>>());
        }
    }
}
