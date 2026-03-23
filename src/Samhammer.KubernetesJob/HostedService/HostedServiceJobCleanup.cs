using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Samhammer.KubernetesJob.Job;
using Samhammer.TimedHostedService;

namespace Samhammer.KubernetesJob.HostedService
{
    public class HostedServiceJobCleanup<T, TOptions> : TimedHostedService<T>
        where T : IBaseJobCleanupService
        where TOptions : class, IBaseJobOptionsDeploy
    {
        private IHostEnvironment HostEnvironment { get; }

        private IOptions<TOptions> Options { get; }

        public HostedServiceJobCleanup(
            ILogger<HostedServiceJobCleanup<T, TOptions>> logger,
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
                await service.CleanupJobs();
                await service.CleanupRedisQueue();
            }
        }
    }
}
