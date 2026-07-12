using MongoDB.Driver;
namespace calendar_service.Services.DAO
{
    public class MongoDBService : IMongoDBService
    {
        private readonly IMongoDatabase database;

        public MongoDBService(ILogger<MongoDBService> logger)
        {
            string connectionString = Environment.GetEnvironmentVariable("ConnectionsString", EnvironmentVariableTarget.Process) ?? "";
            var databaseName = "BookingsDB";
            var client = new MongoClient(connectionString);
            database = client.GetDatabase(databaseName);

            // Sanity check: log the Mongo topology this instance actually resolved to on
            // startup. All calendar-service replicas MUST resolve to the same logical
            // deployment (standalone instance, or same replica set / shard cluster) for the
            // reconciliation job's claim-based lock (see ISagaStateService.TryClaimAsync) to be
            // safe — that lock relies on a single document's FindOneAndUpdate being atomic,
            // which only holds if every replica is actually talking to the same database. This
            // can't be enforced in code (a misconfigured/independent unsynced Mongo deployment
            // is a deployment bug, not something the app can detect for certain), but logging it
            // makes such a misconfiguration visible instead of silently causing duplicate work.
            var summary = DescribeTopology(client, databaseName);
            logger.LogInformation("MongoDB topology: {Summary}", summary);
        }

        /// <summary>
        /// Builds a human-readable summary of the Mongo client's resolved topology (servers,
        /// replica set name, database) for startup sanity-check logging. Pure/static so it can
        /// be unit-tested without a live Mongo connection.
        /// </summary>
        public static string DescribeTopology(IMongoClient client, string databaseName)
        {
            var servers = string.Join(", ", client.Settings.Servers.Select(s => $"{s.Host}:{s.Port}"));
            var replicaSet = string.IsNullOrEmpty(client.Settings.ReplicaSetName)
                ? "standalone (no replica set configured)"
                : $"replicaSet={client.Settings.ReplicaSetName}";
            return $"servers=[{servers}] {replicaSet} database={databaseName}";
        }

        public IMongoCollection<T> GetCollection<T>(string collectionName)
        {
            return database.GetCollection<T>(collectionName);
        }

    }
}
   