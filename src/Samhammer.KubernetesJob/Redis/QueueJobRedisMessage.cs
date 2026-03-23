namespace Samhammer.KubernetesJob.Redis
{
    public class QueueJobRedisMessage
    {
        public string JobName { get; set; }

        public string JobTemplate { get; set; }
    }
}
