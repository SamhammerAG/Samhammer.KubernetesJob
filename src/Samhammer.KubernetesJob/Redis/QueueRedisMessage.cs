using System.Collections.Generic;

namespace Samhammer.KubernetesJob.Redis
{
    public class QueueRedisMessage
    {
        public List<QueueJobRedisMessage> QueuedJobs { get; set; } = new();

        public List<QueueJobRedisMessage> RunningJobs { get; set; } = new();
    }
}
