using System;
using Samhammer.KubernetesJob.Job;

namespace Samhammer.KubernetesJob.Test.Job.Deploy;

public class TestDeployOptions : IBaseJobOptionsDeploy
{
    public bool DisableJob { get; set; }

    public int MaxRunningJobs { get; set; }

    public TimeSpan JobDeployInterval { get; set; }
}
