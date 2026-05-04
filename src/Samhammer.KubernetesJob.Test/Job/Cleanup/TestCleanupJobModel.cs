using Samhammer.KubernetesJob.MongoDb;

namespace Samhammer.KubernetesJob.Test.Job.Cleanup;

public class TestCleanupJobModel : BaseJobModel
{
    public TestCleanupJobStatus Status { get; set; }
}
