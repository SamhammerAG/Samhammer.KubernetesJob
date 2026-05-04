using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RedLockNet;
using Samhammer.KubernetesJob.Job;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.MongoDb;
using Samhammer.KubernetesJob.Redis;
using Xunit;

namespace Samhammer.KubernetesJob.Test.Job.Deploy
{
    public class AbstractJobDeployTest
    {
        private class TestDeployOptions : IBaseJobOptionsDeploy
        {
            public bool DisableJob { get; set; }

            public int MaxRunningJobs { get; set; }

            public TimeSpan JobDeployInterval { get; set; }
        }

        private class TestJobDeployService : AbstractJobDeployService<TestDeployJobModel, IBaseJobRepository<TestDeployJobModel>, TestDeployOptions, IBaseJobKubernetesClient<TestDeployJobModel>, IBaseRedisQueueClient>
        {
            public List<string> CouldNotAcquireLockJobIds { get; } = new();

            public List<string> StartedJobIds { get; } = new();

            public List<string> QueuedJobIds { get; } = new();

            public TestJobDeployService(
                ILogger<TestJobDeployService> logger,
                IOptions<TestDeployOptions> options,
                IBaseJobRepository<TestDeployJobModel> jobRepository,
                IBaseJobKubernetesClient<TestDeployJobModel> jobKubernetesClient,
                IBaseRedisQueueClient jobRedisQueueClient)
                : base(logger, options, jobRepository, jobKubernetesClient, jobRedisQueueClient)
            {
            }

            public override Task OnCouldNotAcquireLock(TestDeployJobModel jobModel)
            {
                CouldNotAcquireLockJobIds.Add(jobModel.Id);

                return Task.CompletedTask;
            }

            public override Task OnJobStarted(TestDeployJobModel jobModel)
            {
                StartedJobIds.Add(jobModel.Id);

                return Task.CompletedTask;
            }

            public override Task OnJobQueued(TestDeployJobModel jobModel)
            {
                QueuedJobIds.Add(jobModel.Id);

                return Task.CompletedTask;
            }
        }

        private readonly TestJobDeployService service;
        private readonly IBaseJobKubernetesClient<TestDeployJobModel> kubernetesClient;
        private readonly IBaseRedisQueueClient redisQueueClient;
        private readonly IRedLock redLock;

        public AbstractJobDeployTest()
        {
            var logger = new NullLogger<TestJobDeployService>();
            var jobRepository = Substitute.For<IBaseJobRepository<TestDeployJobModel>>();
            kubernetesClient = Substitute.For<IBaseJobKubernetesClient<TestDeployJobModel>>();
            redisQueueClient = Substitute.For<IBaseRedisQueueClient>();
            redLock = Substitute.For<IRedLock>();

            var options = Options.Create(new TestDeployOptions
            {
                DisableJob = false,
                MaxRunningJobs = 1
            });

            service = new TestJobDeployService(logger, options, jobRepository, kubernetesClient, redisQueueClient);
        }

        [Fact]
        public async Task DeployQueuedJobs_DeployAllJobs_UntilMaxJobsAllowed_Successfully()
        {
            // Arrange
            var queueJobs = new List<QueueJobRedisMessage>
            {
                new()
                {
                    JobName = "1",
                    JobTemplate = "template1"
                },
                new()
                {
                    JobName = "2",
                    JobTemplate = "template2"
                },
                new()
                {
                    JobName = "3",
                    JobTemplate = "template3"
                }
            };

            redisQueueClient.GetAsync().Returns(new QueueRedisMessage
            {
                QueuedJobs = queueJobs,
                RunningJobs = new List<QueueJobRedisMessage>()
            });

            redLock.IsAcquired.Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            var jobContract1 = queueJobs[0];

            await kubernetesClient.Received(1).DeployJobAsync(Arg.Is<string>(c => c == jobContract1.JobTemplate));
        }

        [Fact]
        public async Task DeployQueuedJobs_MaxJobsReach_DeployNoJob()
        {
            // Arrange
            var runningJobs = new List<QueueJobRedisMessage>
            {
                new()
                {
                    JobName = "1",
                    JobTemplate = "template1"
                }
            };

            redisQueueClient.GetAsync().Returns(new QueueRedisMessage
            {
                QueuedJobs = new List<QueueJobRedisMessage> { new() },
                RunningJobs = runningJobs
            });

            redLock.IsAcquired.Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await redisQueueClient.Received(1).GetAsync();
            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());
            await redisQueueClient.DidNotReceive().MoveJobToRunningAsync(Arg.Any<QueueJobRedisMessage>());
        }

        [Fact]
        public async Task DeployQueuedJobs_CannotAcquireLock_DoNothing()
        {
            // Arrange
            redLock.IsAcquired.Returns(false);
            redisQueueClient.CreateLockAsync().Returns(redLock);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await redisQueueClient.Received(1).CreateLockAsync();
            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());
            await redisQueueClient.DidNotReceive().MoveJobToRunningAsync(Arg.Any<QueueJobRedisMessage>());
        }

        [Fact]
        public async Task DeployQueuedJobs_QueueJobsEmpty_DeployNoJob()
        {
            // Arrange
            redisQueueClient.GetAsync().Returns(new QueueRedisMessage
            {
                QueuedJobs = new List<QueueJobRedisMessage>(),
                RunningJobs = new List<QueueJobRedisMessage>()
            });
            redLock.IsAcquired.Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await redisQueueClient.Received(1).GetAsync();
            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());
            await redisQueueClient.DidNotReceive().MoveJobToRunningAsync(Arg.Any<QueueJobRedisMessage>());
        }

        [Fact]
        public async Task DeployOrQueueJob_DeployJobImmediately()
        {
            // Arrange
            var jobId = "AnyId";
            var job = new TestDeployJobModel
            {
                Status = TestDeployJobStatus.Running,
                Id = jobId
            };
            var jobName = "any";
            var jobTemplate = "anyTemplate";

            // Set up to have there is no job in queue and total running job < MaxRunningJobs from recommendationJobOption
            var queueContract = new QueueRedisMessage
            {
                QueuedJobs = new List<QueueJobRedisMessage>(),
                RunningJobs = new List<QueueJobRedisMessage>()
            };

            redLock.IsAcquired.Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);
            redisQueueClient.GetAsync().Returns(queueContract);
            kubernetesClient.CreateJobName(job).Returns(jobName);
            kubernetesClient.CreateJobTemplate(job).Returns(jobTemplate);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.GetAsync();
                await redisQueueClient.AddJobToRunningListAsync(Arg.Is<QueueJobRedisMessage>(c => c.JobName == jobName));
                await kubernetesClient.DeployJobAsync(Arg.Is<string>(c => c == jobTemplate));
            });

            Assert.Equivalent(service.StartedJobIds, new List<string> { jobId });
        }

        [Fact]
        public async Task DeployOrQueueJob_QueueNotEmpty_AddJobToQueue_Successfully()
        {
            // Arrange
            var jobId = "AnyId";
            var job = new TestDeployJobModel
            {
                Status = TestDeployJobStatus.Created,
                Id = jobId
            };

            var jobName = "any";
            var jobTemplate = "anyTemplate";

            // Set up to have a job in queue so the job cannot be processed immediately. Job will be added to queue.
            var queueContract = new QueueRedisMessage
            {
                QueuedJobs = new List<QueueJobRedisMessage>
                {
                    new()
                },
                RunningJobs = new List<QueueJobRedisMessage>()
            };

            redLock.IsAcquired.Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);
            redisQueueClient.GetAsync().Returns(queueContract);
            kubernetesClient.CreateJobName(job).ReturnsForAnyArgs(jobName);
            kubernetesClient.CreateJobTemplate(job).ReturnsForAnyArgs(jobTemplate);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.GetAsync();
                await redisQueueClient.AddJobToQueueAsync(Arg.Is<QueueJobRedisMessage>(c => c.JobName == jobName));
            });

            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());

            Assert.Equivalent(service.QueuedJobIds, new List<string> { jobId });
        }

        [Fact]
        public async Task DeployOrQueueJob_MaxRunningJobsAllowed_AddJobToQueue()
        {
            // Arrange
            var jobId = "AnyId";
            var job = new TestDeployJobModel
            {
                Status = TestDeployJobStatus.Created,
                Id = jobId
            };
            var jobName = "any";
            var jobTemplate = "anyTemplate";

            // Set up to have max jobs are processing then new job should be added to queue
            var queueContract = new QueueRedisMessage
            {
                QueuedJobs = new List<QueueJobRedisMessage>(),
                RunningJobs = new List<QueueJobRedisMessage>
                {
                    new()
                }
            };

            redLock.IsAcquired.Returns(true);
            redisQueueClient.CreateLockAsync().Returns(redLock);
            redisQueueClient.GetAsync().Returns(queueContract);
            kubernetesClient.CreateJobName(job).Returns(jobName);
            kubernetesClient.CreateJobTemplate(job).Returns(jobTemplate);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.GetAsync();
                await redisQueueClient.AddJobToQueueAsync(Arg.Is<QueueJobRedisMessage>(c => c.JobName == jobName));
            });

            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());

            Assert.Equivalent(service.QueuedJobIds, new List<string> { jobId });
        }

        [Fact]
        public async Task DeployOrQueueJob_CannotAcquireLock_MarkRecommendationJobAsFailed()
        {
            // Arrange
            var jobId = "AnyId";
            var job = new TestDeployJobModel
            {
                Status = TestDeployJobStatus.Created,
                Id = jobId
            };

            redLock.IsAcquired.Returns(false);
            redisQueueClient.CreateLockAsync().Returns(redLock);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Assert.Equivalent(service.CouldNotAcquireLockJobIds, new List<string> { jobId });
        }
    }
}
