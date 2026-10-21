using System.Collections.Generic;
using System.Threading.Tasks;

namespace Samhammer.KubernetesJob.MongoDb
{
    public interface IBaseJobRepository<T>
        where T : BaseJobModel
    {
        Task<List<T>> GetRunningAndCreatedJobs();

        Task<T> GetById(string id);

        Task Save(T jobModel);
    }
}
