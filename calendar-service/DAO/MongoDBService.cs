using MongoDB.Driver;
namespace calendar_service.Services.DAO
{
    public class MongoDBService : IMongoDBService
    {
        private readonly IMongoDatabase database;

        public MongoDBService()
        {
            string connectionString = Environment.GetEnvironmentVariable("ConnectionsString", EnvironmentVariableTarget.Process) ?? "";
            var databaseName = "BookingsDB";
            var client = new MongoClient(connectionString);
            database = client.GetDatabase(databaseName);
        }

        public IMongoCollection<T> GetCollection<T>(string collectionName)
        {
            return database.GetCollection<T>(collectionName);
        }

    }
}
   