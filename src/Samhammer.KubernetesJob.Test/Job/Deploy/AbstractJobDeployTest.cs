using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
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
        private const string JobId = "AnyId";

        private const string JobName = "any";

        private const string JobTemplate = "anyTemplate";

        private readonly AbstractJobDeployService<TestDeployJobModel, IBaseJobRepository<TestDeployJobModel>, TestDeployOptions, IBaseJobKubernetesClient<TestDeployJobModel>, IBaseRedisQueueClient> service;
        private readonly IBaseJobRepository<TestDeployJobModel> jobRepository;
        private readonly IBaseJobKubernetesClient<TestDeployJobModel> kubernetesClient;
        private readonly IBaseRedisQueueClient redisQueueClient;
        private readonly IRedLock redLock;
        private readonly QueueRedisMessage queue = new();

        private readonly TestDeployJobModel job = new() { Status = TestDeployJobStatus.Created, Id = JobId };
        private readonly QueueJobRedisMessage queuedJob = new() { JobId = JobId, JobName = JobName, JobTemplate = JobTemplate };
        private readonly TestDeployOptions options = new() { DisableJob = false, MaxRunningJobs = 1 };

        private bool lockAcquired = true;

        public AbstractJobDeployTest()
        {
            var logger = NullLogger<AbstractJobDeployService<TestDeployJobModel, IBaseJobRepository<TestDeployJobModel>, TestDeployOptions, IBaseJobKubernetesClient<TestDeployJobModel>, IBaseRedisQueueClient>>.Instance;
            var iOptions = Options.Create(options);

            jobRepository = Substitute.For<IBaseJobRepository<TestDeployJobModel>>();
            kubernetesClient = Substitute.For<IBaseJobKubernetesClient<TestDeployJobModel>>();
            redisQueueClient = Substitute.For<IBaseRedisQueueClient>();
            redLock = Substitute.For<IRedLock>();

            redLock.IsAcquired.Returns(_ => lockAcquired);
            redisQueueClient.CreateLockAsync().Returns(redLock);
            redisQueueClient.GetAsync().Returns(queue);
            kubernetesClient.CreateJobName(job).Returns(JobName);
            kubernetesClient.CreateJobTemplate(job).Returns(JobTemplate);
            jobRepository.GetById(JobId).Returns(job);

            // Partial substitute: runs the real deploy logic, records calls to the virtual On* hooks
            service = Substitute.ForPartsOf<AbstractJobDeployService<TestDeployJobModel, IBaseJobRepository<TestDeployJobModel>, TestDeployOptions, IBaseJobKubernetesClient<TestDeployJobModel>, IBaseRedisQueueClient>>(
                logger, iOptions, jobRepository, kubernetesClient, redisQueueClient);
        }

        [Fact]
        public async Task DeployQueuedJobs_DeployAllJobs_UntilMaxJobsAllowed_Successfully()
        {
            // Arrange
            queue.QueuedJobs.Add(new QueueJobRedisMessage { JobName = "1", JobTemplate = "template1" });
            queue.QueuedJobs.Add(new QueueJobRedisMessage { JobName = "2", JobTemplate = "template2" });
            queue.QueuedJobs.Add(new QueueJobRedisMessage { JobName = "3", JobTemplate = "template3" });

            // Act
            await service.DeployQueuedJobs();

            // Assert
            var jobContract1 = queue.QueuedJobs[0];

            await kubernetesClient.Received(1).DeployJobAsync(Arg.Is<string>(c => c == jobContract1.JobTemplate));
        }

        [Fact]
        public async Task DeployQueuedJobs_MaxJobsReach_DeployNoJob()
        {
            // Arrange
            queue.QueuedJobs.Add(new QueueJobRedisMessage());
            queue.RunningJobs.Add(new QueueJobRedisMessage { JobName = "1", JobTemplate = "template1" });

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
            lockAcquired = false;

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await redisQueueClient.Received(1).CreateLockAsync();
            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());
            await redisQueueClient.DidNotReceive().MoveJobToRunningAsync(Arg.Any<QueueJobRedisMessage>());
        }

        [Fact]
        public async Task DeployQueuedJobs_QueueJobsEmpty_DoNothing()
        {
            // Arrange
            // The queue is empty by default

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await redisQueueClient.Received(1).GetAsync();
            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());
            await redisQueueClient.DidNotReceive().MoveJobToRunningAsync(Arg.Any<QueueJobRedisMessage>());
        }

        [Fact]
        public async Task DeployQueuedJobs_DeploysJob_CallsOnJobStartedWithLoadedModel()
        {
            // Arrange
            queue.QueuedJobs.Add(queuedJob);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.MoveJobToRunningAsync(queuedJob);
                await kubernetesClient.DeployJobAsync(JobTemplate);
            });

            await service.Received(1).OnJobStarted(job);
            await service.DidNotReceiveWithAnyArgs().OnJobDeployFailed(null, null);
        }

        [Fact]
        public async Task DeployQueuedJobs_DeployJobFails_CallsOnJobDeployFailedAndDeploysNextJob()
        {
            // Arrange
            var exception = new KubernetesJobDeployException(JobName, null, null, new Exception("kubernetes api down"));
            var job2 = new TestDeployJobModel { Status = TestDeployJobStatus.Created, Id = "AnyId2" };
            var queuedJob2 = new QueueJobRedisMessage { JobId = job2.Id, JobName = "any2", JobTemplate = "anyTemplate2" };

            queue.QueuedJobs.Add(queuedJob);
            queue.QueuedJobs.Add(queuedJob2);
            jobRepository.GetById(job2.Id).Returns(job2);
            kubernetesClient.DeployJobAsync(JobTemplate).ThrowsAsync(exception);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await redisQueueClient.Received(1).RemoveQueuedJobAsync(JobName);
            await redisQueueClient.Received(1).RemoveRunningJobAsync(JobName);
            await service.Received(1).OnJobDeployFailed(job, exception);
            await service.DidNotReceive().OnJobStarted(job);

            // The failed job doesn't take the only slot, so the next job is deployed
            await kubernetesClient.Received(1).DeployJobAsync(queuedJob2.JobTemplate);
            await service.Received(1).OnJobStarted(job2);
        }

        [Fact]
        public async Task DeployQueuedJobs_OnJobDeployFailedThrows_ExceptionReachesCaller()
        {
            // Arrange
            var exception = new KubernetesJobDeployException(JobName, null, null, new Exception("kubernetes api down"));
            var hookException = new Exception("error in deploy failed");

            queue.QueuedJobs.Add(queuedJob);
            kubernetesClient.DeployJobAsync(JobTemplate).ThrowsAsync(exception);
            service.Configure().OnJobDeployFailed(job, exception).ThrowsAsync(hookException);

            // Act
            var act = () => service.DeployQueuedJobs();

            // Assert
            (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(hookException);
        }

        [Fact]
        public async Task DeployQueuedJobs_EntryWithoutJobId_DeploysWithoutHooks()
        {
            // Arrange
            // Entries queued by an older version have no JobId
            queue.QueuedJobs.Add(new QueueJobRedisMessage { JobName = JobName, JobTemplate = JobTemplate });

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await kubernetesClient.Received(1).DeployJobAsync(JobTemplate);
            await jobRepository.DidNotReceiveWithAnyArgs().GetById(null);
            await service.DidNotReceiveWithAnyArgs().OnJobStarted(null);
        }

        [Fact]
        public async Task DeployQueuedJobs_JobModelNotFound_DeploysWithoutHooks()
        {
            // Arrange
            queue.QueuedJobs.Add(queuedJob);
            jobRepository.GetById(JobId).Returns((TestDeployJobModel)null);

            // Act
            await service.DeployQueuedJobs();

            // Assert
            await kubernetesClient.Received(1).DeployJobAsync(JobTemplate);
            await service.DidNotReceiveWithAnyArgs().OnJobStarted(null);
        }

        [Fact]
        public async Task DeployOrQueueJob_DeployJobImmediately()
        {
            // Arrange
            // The queue is empty by default, so the job is deployed immediately

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.GetAsync();
                await redisQueueClient.AddJobToRunningListAsync(Arg.Is<QueueJobRedisMessage>(c => c.JobId == JobId && c.JobName == JobName));
                await kubernetesClient.DeployJobAsync(Arg.Is<string>(c => c == JobTemplate));
            });

            await service.Received(1).OnJobStarted(job);
            await service.DidNotReceiveWithAnyArgs().OnJobDeployFailed(null, null);
        }

        [Fact]
        public async Task DeployOrQueueJob_QueueNotEmpty_AddJobToQueue_Successfully()
        {
            // Arrange
            // Set up to have a job in queue so the job cannot be processed immediately. Job will be added to queue.
            queue.QueuedJobs.Add(new QueueJobRedisMessage());

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.GetAsync();
                await redisQueueClient.AddJobToQueueAsync(Arg.Is<QueueJobRedisMessage>(c => c.JobName == JobName));
            });

            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());

            await service.Received(1).OnJobQueued(job);
        }

        [Fact]
        public async Task DeployOrQueueJob_MaxRunningJobsAllowed_AddJobToQueue()
        {
            // Arrange
            // Set up to have max jobs are processing then new job should be added to queue
            queue.RunningJobs.Add(new QueueJobRedisMessage());

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            Received.InOrder(async () =>
            {
                await redisQueueClient.GetAsync();
                await redisQueueClient.AddJobToQueueAsync(Arg.Is<QueueJobRedisMessage>(c => c.JobName == JobName));
            });

            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());

            await service.Received(1).OnJobQueued(job);
        }

        [Fact]
        public async Task DeployOrQueueJob_CannotAcquireLock_MarkRecommendationJobAsFailed()
        {
            // Arrange
            lockAcquired = false;

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            await service.Received(1).OnCouldNotAcquireLock(job);
        }

        [Fact]
        public async Task DeployOrQueueJob_DeployJobFails_CallsOnJobDeployFailed_AndRemovesJobFromRedis()
        {
            // Arrange
            var exception = new KubernetesJobDeployException(JobName, null, null, new Exception("kubernetes api down"));

            kubernetesClient.DeployJobAsync(JobTemplate).ThrowsAsync(exception);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            await redisQueueClient.Received(1).RemoveRunningJobAsync(JobName);
            await redisQueueClient.Received(1).RemoveQueuedJobAsync(JobName);

            await service.Received(1).OnJobDeployFailed(job, exception);
            await service.DidNotReceiveWithAnyArgs().OnJobStarted(null);
        }

        [Fact]
        public async Task DeployOrQueueJob_AddJobToRunningListFails_CallsOnJobDeployFailed_WithoutDeploying()
        {
            // Arrange
            var exception = new InvalidOperationException("redis down");

            redisQueueClient.AddJobToRunningListAsync(Arg.Any<QueueJobRedisMessage>()).ThrowsAsync(exception);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            await kubernetesClient.DidNotReceive().DeployJobAsync(Arg.Any<string>());

            await service.Received(1).OnJobDeployFailed(job, exception);
            await service.DidNotReceiveWithAnyArgs().OnJobStarted(null);
        }

        [Fact]
        public async Task DeployOrQueueJob_AddJobToQueueFails_CallsOnJobDeployFailed()
        {
            // Arrange
            var exception = new InvalidOperationException("redis down");

            // Max running jobs reached, so the job is added to the queue
            queue.RunningJobs.Add(new QueueJobRedisMessage());
            redisQueueClient.AddJobToQueueAsync(Arg.Any<QueueJobRedisMessage>()).ThrowsAsync(exception);

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            await redisQueueClient.Received(1).RemoveQueuedJobAsync(JobName);

            await service.Received(1).OnJobDeployFailed(job, exception);
            await service.DidNotReceiveWithAnyArgs().OnJobQueued(null);
        }

        [Fact]
        public async Task DeployOrQueueJob_RemovingJobFromRedisFails_StillCallsOnJobDeployFailed()
        {
            // Arrange
            var exception = new KubernetesJobDeployException(JobName, null, null, new Exception("kubernetes api down"));

            kubernetesClient.DeployJobAsync(Arg.Any<string>()).ThrowsAsync(exception);
            redisQueueClient.RemoveQueuedJobAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("redis down"));

            // Act
            await service.DeployOrQueueJob(job);

            // Assert
            await service.Received(1).OnJobDeployFailed(job, exception);
        }

        [Fact]
        public async Task DeployOrQueueJob_OnJobDeployFailedThrows_ExceptionReachesCaller()
        {
            // Arrange
            var exception = new KubernetesJobDeployException(JobName, null, null, new Exception("kubernetes api down"));
            var hookException = new Exception("error in deploy failed");

            kubernetesClient.DeployJobAsync(Arg.Any<string>()).ThrowsAsync(exception);
            service.Configure().OnJobDeployFailed(job, exception).ThrowsAsync(hookException);

            // Act
            var act = () => service.DeployOrQueueJob(job);

            // Assert
            (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(hookException);
        }

        [Fact]
        public async Task DeployOrQueueJob_OnJobStartedFails_ExceptionReachesCaller_WithoutCallingOnJobDeployFailed()
        {
            // Arrange
            var exception = new Exception("mongo down");

            service.Configure().OnJobStarted(job).ThrowsAsync(exception);

            // Act
            var act = () => service.DeployOrQueueJob(job);

            // Assert
            (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(exception);
            await service.DidNotReceiveWithAnyArgs().OnJobDeployFailed(null, null);
        }
    }
}
