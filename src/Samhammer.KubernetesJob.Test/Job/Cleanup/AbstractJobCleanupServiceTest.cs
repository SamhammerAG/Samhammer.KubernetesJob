using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.MongoDb;
using Samhammer.KubernetesJob.Redis;
using Xunit;

namespace Samhammer.KubernetesJob.Test.Job.Cleanup
{
    public class AbstractJobCleanupServiceTest
    {
        private readonly TestJobCleanupService service;

        private readonly IBaseJobRepository<TestCleanupJobModel> jobRepository;
        private readonly IBaseJobKubernetesClient<TestCleanupJobModel> kubernetesClient;

        public AbstractJobCleanupServiceTest()
        {
            var logger = new NullLogger<TestJobCleanupService>();
            jobRepository = Substitute.For<IBaseJobRepository<TestCleanupJobModel>>();
            kubernetesClient = Substitute.For<IBaseJobKubernetesClient<TestCleanupJobModel>>();
            var redisQueueClient = Substitute.For<IBaseRedisQueueClient>();

            service = new TestJobCleanupService(logger, jobRepository, kubernetesClient, redisQueueClient);
        }

        [Fact]
        public async Task CleanupJobs_Test_Failed_Or_Deleted()
        {
            // Arrange
            var jobs = new List<TestCleanupJobModel>
            {
                new()
                {
                    Id = "JobJobModelId1",
                    Status = TestCleanupJobStatus.Failed
                },
                new()
                {
                    Id = "JobJobModelId2",
                    Status = TestCleanupJobStatus.Running
                },
                new()
                {
                    Id = "JobJobModelId3",
                    Status = TestCleanupJobStatus.Completed
                }
            };

            jobRepository.GetRunningAndCreatedJobs().Returns(jobs);

            kubernetesClient.GetJobStatusAsync(jobs[0]).Returns(KubernetesJobStatus.Failed);
            kubernetesClient.GetJobStatusAsync(jobs[1]).Returns(KubernetesJobStatus.Running);
            kubernetesClient.GetJobStatusAsync(jobs[2]).Returns(KubernetesJobStatus.Deleted);

            // Act
            await service.CleanupJobs();

            // Assert
            Assert.Equivalent(service.DeletedJobIds, new List<string> { "JobJobModelId3" });
            Assert.Equivalent(service.FailedJobIds, new List<string> { "JobJobModelId1" });
        }

        [Fact]
        public async Task CleanupJobs_Test_Ignore_New_Jobs()
        {
            // Arrange
            var jobs = new List<TestCleanupJobModel>
            {
                new()
                {
                    Id = "JobJobModelId1",
                    Status = TestCleanupJobStatus.Running,
                    DateCreated = DateTime.Now
                },
                new()
                {
                    Id = "JobJobModelId2",
                    Status = TestCleanupJobStatus.Running,
                    DateCreated = DateTime.Now
                }
            };

            jobRepository.GetRunningAndCreatedJobs().Returns(jobs);

            // Intentionally set the status of Kubernetes to Failed to test that it will not be cleared for one minute. 
            kubernetesClient.GetJobStatusAsync(jobs[0]).Returns(KubernetesJobStatus.Failed);
            kubernetesClient.GetJobStatusAsync(jobs[1]).Returns(KubernetesJobStatus.Failed);

            // Act
            await service.CleanupJobs();

            // Assert
            Assert.Equivalent(service.DeletedJobIds, new List<string>());
            Assert.Equivalent(service.FailedJobIds, new List<string>());
        }
    }
}
