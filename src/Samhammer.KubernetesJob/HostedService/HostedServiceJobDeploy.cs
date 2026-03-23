using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Samhammer.KubernetesJob.Job;
using Samhammer.TimedHostedService;

namespace Samhammer.KubernetesJob.HostedService
{
    public class HostedServiceJobDeploy<T, TJobModel, TOptions> : TimedHostedService<T>
        where T : IBaseJobDeployService<TJobModel>
        where TJobModel : class
        where TOptions : class, IBaseJobOptionsDeploy
    {
        protected override TimeSpan ExecutionInterval => Options.Value.JobDeployInterval;

        private IHostEnvironment HostEnvironment { get; }

        private IOptions<TOptions> Options { get; }

        public HostedServiceJobDeploy(
            ILogger<HostedServiceJobDeploy<T, TJobModel, TOptions>> logger,
            IServiceScopeFactory services,
            IHostEnvironment hostEnvironment,
            IOptions<TOptions> options)
            : base(logger, services)
        {
            HostEnvironment = hostEnvironment;
            Options = options;
        }

        protected override async Task RunScoped(T service)
        {
            if (!HostEnvironment.IsDevelopment() && !Options.Value.DisableJob)
            {
                await service.DeployQueuedJobs();
            }
        }
    }
}
