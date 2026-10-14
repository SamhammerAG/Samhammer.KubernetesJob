using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Samhammer.KubernetesJob.Kubernetes;
using Samhammer.KubernetesJob.Test.Job.Deploy;
using Xunit;

namespace Samhammer.KubernetesJob.Test.Kubernetes
{
    public class AbstractJobKubernetesClientTest
    {
        private const string JobName = "test-job";

        private const string Namespace = "test-namespace";

        private const string JobTemplate = $"""
            apiVersion: batch/v1
            kind: Job
            metadata:
              name: {JobName}
              namespace: {Namespace}
            """;

        private readonly TestJobKubernetesClient client;
        private readonly IBatchV1Operations batchV1;
        private readonly TestDeployJobModel job = new() { Id = "AnyId" };

        public AbstractJobKubernetesClientTest()
        {
            batchV1 = Substitute.For<IBatchV1Operations>();
            var kubernetes = Substitute.For<IKubernetes>();
            kubernetes.BatchV1.Returns(batchV1);

            batchV1.DeleteNamespacedJobWithHttpMessagesAsync(null, null)
                .ThrowsAsyncForAnyArgs(CreateHttpOperationException(HttpStatusCode.NotFound, "not found"));

            client = new TestJobKubernetesClient(new NullLogger<TestJobKubernetesClient>(), Substitute.For<IHostEnvironment>(), kubernetes);
        }

        [Fact]
        public async Task DeployJobAsync_CreateSucceeds_CreatesJobWithoutException()
        {
            // Arrange
            batchV1.CreateNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(new HttpOperationResponse<V1Job> { Body = new V1Job() });

            // Act
            var act = () => client.DeployJobAsync(JobTemplate);

            // Assert
            await act.Should().NotThrowAsync();

            await batchV1.Received(1).CreateNamespacedJobWithHttpMessagesAsync(
                Arg.Is<V1Job>(job => job.Metadata.Name == JobName),
                Namespace,
                cancellationToken: Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DeployJobAsync_CreateFailsWithHttpError_ThrowsDeployException()
        {
            // Arrange
            var httpException = CreateHttpOperationException(HttpStatusCode.Forbidden, "denied by kyverno");
            batchV1.CreateNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ThrowsAsyncForAnyArgs(httpException);

            // Act
            var act = () => client.DeployJobAsync(JobTemplate);

            // Assert
            var exception = (await act.Should().ThrowAsync<KubernetesJobDeployException>()).Which;
            exception.JobName.Should().Be(JobName);
            exception.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            exception.Response.Should().Be("denied by kyverno");
            exception.InnerException.Should().BeSameAs(httpException);
        }

        [Fact]
        public async Task DeployJobAsync_CreateFailsUnexpectedly_ThrowsDeployException()
        {
            // Arrange
            var unexpectedException = new HttpRequestException("connection refused");
            batchV1.CreateNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ThrowsAsyncForAnyArgs(unexpectedException);

            // Act
            var act = () => client.DeployJobAsync(JobTemplate);

            // Assert
            var exception = (await act.Should().ThrowAsync<KubernetesJobDeployException>()).Which;
            exception.JobName.Should().Be(JobName);
            exception.StatusCode.Should().BeNull();
            exception.InnerException.Should().BeSameAs(unexpectedException);
        }

        [Fact]
        public async Task GetJobStatusAsync_JobRunning_ReturnsRunning()
        {
            // Arrange
            batchV1.ReadNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(new HttpOperationResponse<V1Job> { Body = new V1Job() });

            // Act
            var status = await client.GetJobStatusAsync(job);

            // Assert
            status.Should().Be(KubernetesJobStatus.Running);
            await batchV1.Received(1).ReadNamespacedJobWithHttpMessagesAsync(JobName, Namespace, cancellationToken: Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GetJobStatusAsync_JobNotFound_ReturnsDeleted()
        {
            // Arrange
            batchV1.ReadNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ThrowsAsyncForAnyArgs(CreateHttpOperationException(HttpStatusCode.NotFound, "not found"));

            // Act
            var status = await client.GetJobStatusAsync(job);

            // Assert
            status.Should().Be(KubernetesJobStatus.Deleted);
        }

        [Fact]
        public async Task GetJobStatusAsync_ReadFailsWithHttpError_ReturnsUnknown()
        {
            // Arrange
            batchV1.ReadNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ThrowsAsyncForAnyArgs(CreateHttpOperationException(HttpStatusCode.InternalServerError, "kubernetes api down"));

            // Act
            var status = await client.GetJobStatusAsync(job);

            // Assert
            status.Should().Be(KubernetesJobStatus.Unknown);
        }

        [Fact]
        public async Task GetJobStatusAsync_ReadFailsUnexpectedly_ReturnsUnknown()
        {
            // Arrange
            batchV1.ReadNamespacedJobWithHttpMessagesAsync(null, null, cancellationToken: TestContext.Current.CancellationToken)
                .ThrowsAsyncForAnyArgs(new HttpRequestException("connection refused"));

            // Act
            var status = await client.GetJobStatusAsync(job);

            // Assert
            status.Should().Be(KubernetesJobStatus.Unknown);
        }

        private static HttpOperationException CreateHttpOperationException(HttpStatusCode statusCode, string content)
        {
            return new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(statusCode), content)
            };
        }

        private class TestJobKubernetesClient : AbstractJobKubernetesClient<TestDeployJobModel>
        {
            private readonly IKubernetes kubernetes;

            public TestJobKubernetesClient(ILogger<TestJobKubernetesClient> logger, IHostEnvironment hostEnvironment, IKubernetes kubernetes)
                : base(logger, hostEnvironment)
            {
                this.kubernetes = kubernetes;
            }

            public override string CreateJobName(TestDeployJobModel jobModel)
            {
                return JobName;
            }

            protected override IKubernetes GetKubernetes()
            {
                return kubernetes;
            }

            protected override Dictionary<string, string> CreatePlaceholderDictionary(TestDeployJobModel jobModel)
            {
                return new Dictionary<string, string>();
            }

            protected override string GetTemplateDirectory()
            {
                throw new NotSupportedException();
            }

            protected override string ReadTemplate()
            {
                return JobTemplate;
            }

            protected override Dictionary<string, string> CreateJobTypeLabelSelector()
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
