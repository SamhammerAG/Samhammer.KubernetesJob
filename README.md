# Samhammer.KubernetesJob

This package provides a wrapper to run kubernetes jobs, including redis queuing.

## How to add this to your project:
- reference this package to your project: https://www.nuget.org/packages/Samhammer.KubernetesJob/
- add implementation as described below

Note: This is built upon multiple of our other libraries:
* Samhammer.TimedHostedService (Mandatory)
* Samhammer.DependencyInjection (Mandatory)
* Samhammer.Mongo (Mandatory - only built to be used with mongodb)
* Samhammer.Options (Optional - if used as dscribed below with the Option attribute)

### Model:
Create a custom model to track import details and status.

```csharp
[MongoCollection("myImport")]
public class MyImportModel : BaseJobModel
{
    public string UploadedFileName { get; set; }

    [BsonRepresentation(BsonType.String)]
    public MyImportJobStatus Status { get; set; }
}

public enum MyImportJobStatus
{
    Created,
    Queued,
    Processing,
    Failed,
    Completed
}
```

### Options:

Create options of the job

```csharp
[Option]
public class ImportJobOptions : IBaseJobOptionsDeploy, IBaseJobOptionsRedis
{
    // If disabled, the hosted services are not running
    // and on deploying a job you will only get a log entry
    public bool DisableJob { get; set; }

    // Number of jobs allowed to run at a time
    public int MaxRunningJobs { get; set; }

    // How often the hosted service looks for queued jobs to execute them
    public TimeSpan JobDeployInterval { get; set; }

    // Directory where the job definition json is located at
    public string TemplateDirectory { get; set; }

    // Key for the queue in redis. Suggested schema: appname:keyname
    public string RedisManagerKey { get; set; }

    public TimeSpan RedisLockExpireTime { get; set; }

    public TimeSpan RedisLockWaitTime { get; set; }

    public TimeSpan RedisLockRetryTime { get; set; }
}
```

Example configuration in appsettings:
```json
  "ImportJobOptions": {
    "DisableJob": false,
    "MaxRunningJobs": 1,
    "JobDeployInterval": "00:00:10",
    "TemplateDirectory": "/opt/app/data/MyImportJob.yaml",
    "RedisManagerKey": "myapp:import-job",
    "RedisLockExpireTime": "00:00:30",
    "RedisLockWaitTime": "00:00:30",
    "RedisLockRetryTime": "00:00:01",
    "JobDeployInterval": "00:00:10",
  },
```

### Mongodb Repository:

Implement repository method to load the running jobs and the jobs waiting for being started in the queue

```csharp
public class MyImportRepositoryMongo : BaseRepositoryMongo<MyImportModel>, IMyImportRepositoryMongo
{
    public MyImportRepositoryMongo(
        ILogger<MyImportRepositoryMongo> logger,
        IMongoDbConnector connector)
        : base(logger, connector)
    {
    }

    public async Task<List<MyImportModel>> GetRunningAndCreatedJobs()
    {
        // Load all jobs that are running or stay in created without being queued
        var filter = Filter.Where(m => m.Status == MyImportStatus.Created || m.Status == MyImportStatus.Processing);

        var jobs = await Collection.Find(filter).ToListAsync();

        return jobs;
    }
}

public interface IMyImportRepositoryMongo : IBaseJobRepository<MyImportModel>, IBaseRepositoryMongo<MyImportModel>
{
}
```

### Handle Job Deployment:

Use the job deploy service to update your job collection (status) if your job gets queued or started

```csharp
public class MyImportJobDeployService : AbstractJobDeployService<MyImportModel, IMyImportRepositoryMongo, ImportJobOptions, IMyImportImportJobKubernetesClient, IMyImportImportJobRedisQueueClient>, IMyImportImportJobDeployService
{
    public MyImportJobDeployService(ILogger<MyImportJobDeployService> logger, IOptions<ImportJobOptions> options,
    TJobRepository jobRepository
    IMyImportImportJobKubernetesClient jobKubernetesClient, IMyImportImportJobRedisQueueClient jobRedisQueueClient)
        : base(logger, options, jobRepository, jobKubernetesClient, jobRedisQueueClient)
    {
    }

    public override async Task OnCouldNotAcquireLock(MyImportModel jobModel)
    {
        // The redis lock could not be acquired within the confugired time
    }

    public override async Task OnJobQueued(MyImportModel jobModel)
    {
        // The job execution has been queued
    }

    public override async Task OnJobStarted(MyImportModel jobModel)
    {
        // The job has been started
    }
}
```

Register the deploy service in program.cs

```csharp
builder.Services.AddHostedService<HostedServiceJobDeploy<IMyImportJobDeployService, MyImportModel, ImportJobOptions>>();
```

### Kubernetes:

Define the name of the job and other kubernetes specific data by implementing a KubernetesClient.

```csharp
public class MyImportJobKubernetesClient : AbstractJobKubernetesClient<MyImportModel>, IMyImportJobKubernetesClient
{
    public MyImportJobKubernetesClient(
        ILogger<MyImportJobKubernetesClient> logger,
        IHostEnvironment hostEnvironment)
        : base(logger, hostEnvironment)
    {
    }

    public override string CreateJobName(MyImportModel jobModel)
    {
        // Must identify a unique instance of the job
        return $"myimport-{jobModel.Id}".ToLowerInvariant();
    }

    protected override Dictionary<string, string> CreatePlaceholderDictionary(MyImportModel model)
    {
        // Set all placeholders that you use in your template
        return new Dictionary<string, string>
        {
            { "jobId", model.Id }
            { "name", CreateJobName(model) }
        };
    }

    protected override string GetTemplateDirectory()
    {
        return "/opt/app/data/mytemplate.yaml";
    }

    protected override Dictionary<string, string> CreateJobTypeLabelSelector()
    {
        // A Dictionary with label selectors to find all jobs of this type
        // (not the specific instance!)
        return new Dictionary<string, string>
        {
            { "branch", "todo" },
            { "brand", "todo" },
            { "job", "todo" }
        };
    }
}
```

### Handle Cleanup:

Use the cleanup service to update your job collection (status) if something unexpected happens to the kubernetes job

* Processes all started and running jobs
* In case of an unexpected status like failed or deleted you can react


```csharp
public class MyImportJobCleanupService : AbstractJobCleanupService<MyImportModel, IMyImportRepositoryMongo, IMyImportJobKubernetesClient, IMyImportJobRedisQueueClient>, IMyImportJobCleanupService
    {
        public MyImportJobCleanupService(ILogger<MyImportJobCleanupService> logger, IMyImportRepositoryMongo jobRepository, IMyImportJobKubernetesClient jobKubernetesClient, IMyImportJobRedisQueueClient jobRedisQueueClient)
            : base(logger, jobRepository, jobKubernetesClient, jobRedisQueueClient)
        {
        }

        public override async Task OnKubernetesJobDeleted(MyImportModel jobModel)
        {
            // Called if the job isn't there anymore in kubernetes -> Can be used by you to sync the status to the model
        }

        
        public override async Task OnKubernetesJobFailed(MyImportModel jobModel)
        {
            // Called if the job is failed in kubernetes -> Can be used by you to sync the status to the model
        }
    }
```

Register the cleanup service in program.cs

```csharp

builder.Services.AddHostedService<HostedServiceJobCleanup<IMyImportJobCleanupService, ImportJobOptions>>();
```

### How to start a job

Create a transient model and call DeployOrQueueJob.
The job will be started immediately or queued if the max. number of jobs is already running. In this case your job starts as soon as the next slot is free during a JobDeployInterval tick.

```csharp
var jobModel = new MyImportModel
{
    Creator = userName,
    Status = PublishJobStatus.Created
    // DateCreated will be set internally
};

await JobDeployService.DeployOrQueueJob(jobModel);
```

### Example template

You need to create a template with the path to it configured in TemplateDirectory.
It is possible to use placeholder values of placeholders configured in MyImportJobKubernetesClient.
Here you can see name and jobId as sample placeholders.

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: "{{name}}"
  namespace: "project-namespace"
spec:
  template:
    metadata:
      name: "{{name}}"
    spec:
      containers:
        - name: my-import-job
          image: my-registry.local/project/my-import-job:v1
          command: ["dotnet", "Project.MyImportJob.dll"]
          env:
            - name: TrainingOptions__jobId
              value: "{{jobId}}"
```

## Contribute

#### How to publish package
- create git tag
- The nuget package will be published automatically by a github action
