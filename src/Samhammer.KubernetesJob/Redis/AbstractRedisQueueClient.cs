using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RedLockNet;
using StackExchange.Redis;

namespace Samhammer.KubernetesJob.Redis
{
    public abstract class AbstractRedisQueueClient<TOptions> : IBaseRedisQueueClient
        where TOptions : class, IBaseJobOptionsRedis
    {
        protected IConnectionMultiplexer ConnectionMultiplexer { get; }

        protected IDistributedLockFactory RedLockFactory { get; }

        protected IOptions<TOptions> RedisOptions { get; }

        protected AbstractRedisQueueClient(IOptions<TOptions> redisOptions, IConnectionMultiplexer connectionMultiplexer, IDistributedLockFactory redLockFactory)
        {
            RedisOptions = redisOptions;
            ConnectionMultiplexer = connectionMultiplexer;
            RedLockFactory = redLockFactory;
        }

        public async Task SetAsync(QueueRedisMessage value)
        {
            var jsonValue = JsonSerializer.Serialize(value);
            await ConnectionMultiplexer.GetDatabase().StringSetAsync(RedisOptions.Value.RedisManagerKey, jsonValue);
        }

        public async Task<QueueRedisMessage> GetAsync()
        {
            var value = await ConnectionMultiplexer.GetDatabase().StringGetAsync(RedisOptions.Value.RedisManagerKey);

            if (value.HasValue)
            {
                return JsonSerializer.Deserialize<QueueRedisMessage>(value.ToString());
            }

            var defaultValue = new QueueRedisMessage();
            await SetAsync(defaultValue);
            return defaultValue;
        }

        public async Task<IRedLock> CreateLockAsync()
        {
            return await RedLockFactory.CreateLockAsync(
                RedisOptions.Value.RedisManagerKey,
                RedisOptions.Value.RedisLockExpireTime,
                RedisOptions.Value.RedisLockWaitTime,
                RedisOptions.Value.RedisLockRetryTime);
        }

        public async Task AddJobToQueueAsync(QueueJobRedisMessage queueJobDefinition)
        {
            var jobManagerData = await GetAsync();
            jobManagerData.QueuedJobs.Add(queueJobDefinition);
            await SetAsync(jobManagerData);
        }

        public async Task<QueueRedisMessage> MoveJobToRunningAsync(QueueJobRedisMessage queueJobDefinition)
        {
            var jobManagerData = await GetAsync();
            jobManagerData.QueuedJobs = jobManagerData.QueuedJobs.Where(job => job.JobName != queueJobDefinition.JobName).ToList();
            jobManagerData.RunningJobs.Add(queueJobDefinition);
            await SetAsync(jobManagerData);
            return jobManagerData;
        }

        public async Task<List<string>> RemoveRunningJobsExceptAsync(List<string> runningJobNames)
        {
            var jobManagerData = await GetAsync();

            var removedJobs = jobManagerData.RunningJobs
                .Select(c => c.JobName)
                .Where(c => !runningJobNames.Any(jobName => jobName.Equals(c)))
                .ToList();

            jobManagerData.RunningJobs.RemoveAll(c => removedJobs.Contains(c.JobName));

            await SetAsync(jobManagerData);

            return removedJobs;
        }

        public async Task RemoveRunningJobAsync(string jobId)
        {
            var jobManagerData = await GetAsync();
            jobManagerData.RunningJobs.RemoveAll(c => c.JobName == jobId);
            await SetAsync(jobManagerData);
        }

        public async Task RemoveQueuedJobAsync(string jobId)
        {
            var jobManagerData = await GetAsync();
            jobManagerData.QueuedJobs.RemoveAll(c => c.JobName == jobId);
            await SetAsync(jobManagerData);
        }

        public async Task<bool> AnyRunningJobs()
        {
            var jobManagerData = await GetAsync();
            return jobManagerData.RunningJobs.Any();
        }

        public async Task AddJobToRunningListAsync(QueueJobRedisMessage queueJobDefinition)
        {
            var jobManagerData = await GetAsync();
            jobManagerData.RunningJobs.Add(queueJobDefinition);
            await SetAsync(jobManagerData);
        }
    }

    public interface IBaseRedisQueueClient
    {
        Task<QueueRedisMessage> GetAsync();

        Task SetAsync(QueueRedisMessage value);

        Task<IRedLock> CreateLockAsync();

        Task AddJobToQueueAsync(QueueJobRedisMessage queueJobDefinition);

        Task AddJobToRunningListAsync(QueueJobRedisMessage queueJobDefinition);

        Task<QueueRedisMessage> MoveJobToRunningAsync(QueueJobRedisMessage queueJobDefinition);

        Task<List<string>> RemoveRunningJobsExceptAsync(List<string> runningJobNames);

        Task RemoveRunningJobAsync(string jobName);

        Task RemoveQueuedJobAsync(string jobName);

        Task<bool> AnyRunningJobs();
    }

    public interface IBaseJobOptionsRedis
    {
        public string RedisManagerKey { get; set; }

        public TimeSpan RedisLockExpireTime { get; set; }

        public TimeSpan RedisLockWaitTime { get; set; }

        public TimeSpan RedisLockRetryTime { get; set; }
    }
}
