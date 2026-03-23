using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samhammer.KubernetesJob.MongoDb;
using Scriban;

namespace Samhammer.KubernetesJob.Kubernetes
{
    public abstract class AbstractJobKubernetesClient<T> : IBaseJobKubernetesClient<T>
        where T : BaseJobModel, new()
    {
        protected ILogger<AbstractJobKubernetesClient<T>> Logger { get; }

        protected IHostEnvironment HostEnvironment { get; }

        protected AbstractJobKubernetesClient(ILogger<AbstractJobKubernetesClient<T>> logger, IHostEnvironment hostEnvironment)
        {
            Logger = logger;
            HostEnvironment = hostEnvironment;
        }

        public V1Job CreateJobDefinition(T jobModel)
        {
            var template = CreateJobTemplate(jobModel);
            return KubernetesYaml.Deserialize<V1Job>(template);
        }

        public string CreateJobTemplate(T jobModel)
        {
            var placeholderValues = CreatePlaceholderDictionary(jobModel);
            var templateDir = GetTemplateDirectory();
            var templateData = System.IO.File.ReadAllText(templateDir);
            var parsingTemplate = Template.Parse(templateData);
            return parsingTemplate.Render(placeholderValues);
        }

        public abstract string CreateJobName(T jobModel);

        protected abstract Dictionary<string, string> CreatePlaceholderDictionary(T jobModel);

        protected abstract string GetTemplateDirectory();

        protected k8s.Kubernetes GetKubernetes()
        {
            if (HostEnvironment.IsDevelopment())
            {
                throw new InvalidOperationException("k8s-job should only be accessed or created in clusters!");
            }

            return new k8s.Kubernetes(KubernetesClientConfiguration.InClusterConfig());
        }

        public async Task DeployJobAsync(string jobTemplate)
        {
            var job = KubernetesYaml.Deserialize<V1Job>(jobTemplate);
            await TryDeleteJobImmediately(job);
            await CreateJobAsync(job);
        }

        public async Task DeployJobAsync(T jobModel)
        {
            var job = CreateJobDefinition(jobModel);
            await TryDeleteJobImmediately(job);
            await CreateJobAsync(job);
        }

        protected async Task CreateJobAsync(V1Job job)
        {
            var jobName = job.Metadata.Name;

            try
            {
                Logger.LogInformation("Creating k8s-job '{JobName}'", jobName);

                var createdJob = await GetKubernetes().CreateNamespacedJobAsync(job, job.Metadata.NamespaceProperty);
                var jobStatus = MapJobStatus(createdJob);

                if (jobStatus != KubernetesJobStatus.Running)
                {
                    Logger.LogError("Creating k8s-job '{JobName}' failed with result {@K8sJob}'", jobName, createdJob);
                }
            }
            catch (HttpOperationException e)
            {
                var statusCode = e.Response?.StatusCode;
                var response = e.Response?.Content;

                Logger.LogError(e, "Creating k8s-job '{JobName}' failed with status code '{Status}' and response '{Response}'", jobName, statusCode, response);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unexpected error while creating k8s-job '{JobName}'", jobName);
            }
        }

        public async Task<List<V1Job>> GetJobsAsync(params KubernetesJobStatus[] status)
        {
            var jobDefinition = CreateJobDefinition(new T());
            var kubernetesNamespace = jobDefinition.Metadata.NamespaceProperty;

            try
            {
                var labelSelector = GetJobTypeLabelSelector();
                var result = await GetKubernetes().ListNamespacedJobAsync(kubernetesNamespace, labelSelector: labelSelector);

                return result.Items
                    .Where(job => HasJobStatus(job, status))
                    .ToList();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unexpected error while loading k8s-jobs of namespace {Namespace}", kubernetesNamespace);
                return new List<V1Job>();
            }
        }

        private string GetJobTypeLabelSelector()
        {
            var labels = CreateJobTypeLabelSelector();
            var labelSelectors = labels.Select(label => $"{label.Key}={label.Value}");
            return string.Join(",", labelSelectors);
        }

        protected abstract Dictionary<string, string> CreateJobTypeLabelSelector();

        protected async Task<V1Job> ReadJobAsync(V1Job job)
        {
            var kubernetesNamespace = job.Metadata.NamespaceProperty;
            var jobName = job.Metadata.Name;

            try
            {
                return await GetKubernetes().ReadNamespacedJobAsync(jobName, kubernetesNamespace);
            }
            catch (HttpOperationException e)
            {
                var statusCode = e.Response?.StatusCode;
                var response = e.Response?.Content;

                if (statusCode == HttpStatusCode.NotFound)
                {
                    Logger.LogDebug("Reading k8s-job '{JobName}' skipped, status code '{Status}''", jobName, statusCode);
                }
                else
                {
                    Logger.LogError(e, "Reading k8s-job '{JobName}' failed with status code '{Status}' and response '{Response}'", jobName, statusCode, response);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unexpected error while reading k8s-job {JobName} of namespace {Namespace}", kubernetesNamespace, jobName);
            }

            return null;
        }

        public async Task<KubernetesJobStatus> GetJobStatusAsync(T jobModel)
        {
            var jobDefinition = CreateJobDefinition(jobModel);
            var job = await ReadJobAsync(jobDefinition);
            var kubernetesJobStatus = MapJobStatus(job);

            Logger.LogDebug("GetOrCreateConfigs status of k8s-job '{JobName}' returned '{KubernetesStatus}'", jobDefinition.Metadata.Name, kubernetesJobStatus);
            return kubernetesJobStatus;
        }

        public async Task DeleteJobAsync(T jobModel)
        {
            var jobDefinition = CreateJobDefinition(jobModel);
            await DeleteJobAsync(jobDefinition);
        }

        private bool HasJobStatus(V1Job job, params KubernetesJobStatus[] status)
        {
            if (status.Length == 0)
            {
                return true;
            }

            var jobStatus = MapJobStatus(job);
            return status.Contains(jobStatus);
        }

        private KubernetesJobStatus MapJobStatus(V1Job job)
        {
            if (job == null)
            {
                return KubernetesJobStatus.Deleted;
            }

            if (job.Status == null || job.Status.Conditions == null)
            {
                return KubernetesJobStatus.Running;
            }

            if (job.Status.Conditions.Any(c => c.Type.Equals("Complete")))
            {
                return KubernetesJobStatus.Completed;
            }

            if (job.Status.Conditions.Any(c => c.Type.Equals("Failed")))
            {
                return KubernetesJobStatus.Failed;
            }

            return KubernetesJobStatus.Running;
        }

        private async Task TryDeleteJobImmediately(V1Job job)
        {
            try
            {
                // This immediately kills the job without properly executing cancellationToken logic for a clean shutdown
                await DeleteJobAsync(job, 0);
            }
            catch (HttpOperationException e)
            {
                var statusCode = e.Response?.StatusCode;
                var response = e.Response?.Content;

                Logger.LogError(e, "Deleting k8s-job '{JobName}' failed with status code '{Status}' and response '{Response}'", job.Metadata.Name, statusCode, response);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unexpected error while deleting k8s-job '{JobName}'", job.Metadata.Name);
            }
        }

        private async Task DeleteJobAsync(V1Job job, int? gracePeriodSeconds = null)
        {
            string jobName = job.Metadata.Name;
            string kubernetesNamespace = job.Metadata.NamespaceProperty;

            try
            {
                Logger.LogInformation("Deleting k8s-job '{JobName}'", jobName);

                var resultStatus = await GetKubernetes().DeleteNamespacedJobAsync(
                    jobName,
                    kubernetesNamespace,
                    propagationPolicy: "Foreground",
                    gracePeriodSeconds: gracePeriodSeconds);

                if (!string.Equals(resultStatus.Status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError("Deleting k8s-job '{JobName}' failed with result {@K8sStatus}'", jobName, resultStatus);
                }
            }
            catch (HttpOperationException e)
            {
                var statusCode = e.Response?.StatusCode;

                // Ignore not found (already deleted)
                if (statusCode == HttpStatusCode.NotFound)
                {
                    Logger.LogDebug("Deleting k8s-job '{JobName}' skipped, status code '{Status}'", jobName, statusCode);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    public interface IBaseJobKubernetesClient<in T>
    {
        string CreateJobTemplate(T jobModel);

        string CreateJobName(T jobModel);

        Task DeployJobAsync(T jobModel);

        Task DeployJobAsync(string jobTemplate);

        Task<List<V1Job>> GetJobsAsync(params KubernetesJobStatus[] status);

        Task<KubernetesJobStatus> GetJobStatusAsync(T jobModel);

        Task DeleteJobAsync(T jobModel);
    }
}
