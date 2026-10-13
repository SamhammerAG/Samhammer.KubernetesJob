using System;
using System.Net;

namespace Samhammer.KubernetesJob.Kubernetes
{
    public class KubernetesJobDeployException : Exception
    {
        public string JobName { get; }

        public HttpStatusCode? StatusCode { get; }

        public string Response { get; }

        public KubernetesJobDeployException(string jobName, HttpStatusCode? statusCode, string response, Exception innerException)
            : base(CreateMessage(jobName, statusCode), innerException)
        {
            JobName = jobName;
            StatusCode = statusCode;
            Response = response;
        }

        private static string CreateMessage(string jobName, HttpStatusCode? statusCode)
        {
            return statusCode.HasValue
                ? $"Creating k8s-job '{jobName}' failed with status code '{statusCode}'"
                : $"Creating k8s-job '{jobName}' failed";
        }
    }
}
