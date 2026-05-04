using Samhammer.KubernetesJob.MongoDb;

namespace Samhammer.KubernetesJob.Test.Job.Deploy;

public class TestDeployJobModel : BaseJobModel
{
    public TestDeployJobStatus Status { get; set; }
}
