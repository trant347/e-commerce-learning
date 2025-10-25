using MongoDB.Driver;

namespace calendar_service.Services.DAO
{
    public interface IMongoDBService
    {
        IMongoCollection<T> GetCollection<T>(string collectionName);
    }
}