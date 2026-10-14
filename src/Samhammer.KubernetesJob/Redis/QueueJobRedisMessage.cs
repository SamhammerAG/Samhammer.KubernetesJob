namespace Samhammer.KubernetesJob.Redis
{
    public class QueueJobRedisMessage
    {
        public string JobId { get; set; }

        public string JobName { get; set; }

        public string JobTemplate { get; set; }
    }
}
