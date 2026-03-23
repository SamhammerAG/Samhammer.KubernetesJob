using System;
using Samhammer.Mongo.Abstractions;

namespace Samhammer.KubernetesJob.MongoDb
{
    public abstract class BaseJobModel : BaseModelMongo
    {
        public string Creator { get; set; }

        public DateTime DateCreated { get; set; }

        public DateTime? DateFinished { get; set; }
    }
}
