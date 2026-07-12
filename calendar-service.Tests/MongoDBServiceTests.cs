using calendar_service.Services.DAO;
using MongoDB.Driver;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// Unit tests for <see cref="MongoDBService.DescribeTopology"/> — the pure helper backing the
    /// startup sanity-check log added alongside the reconciliation job's claim-based lock (see
    /// PAYMENT_SAGA_SPEC.md). Constructing a real <see cref="MongoClient"/> doesn't open a
    /// network connection (the driver connects lazily), so these tests can verify the topology
    /// summary without a live Mongo instance.
    /// </summary>
    public class MongoDBServiceTests
    {
        [Fact]
        public void DescribeTopology_StandaloneConnectionString_ReportsNoReplicaSet()
        {
            var client = new MongoClient("mongodb://localhost:27017");

            var summary = MongoDBService.DescribeTopology(client, "BookingsDB");

            Assert.Contains("localhost:27017", summary);
            Assert.Contains("standalone", summary);
            Assert.Contains("database=BookingsDB", summary);
        }

        [Fact]
        public void DescribeTopology_ReplicaSetConnectionString_ReportsReplicaSetNameAndAllHosts()
        {
            var client = new MongoClient("mongodb://host1:27017,host2:27017,host3:27017/?replicaSet=rs0");

            var summary = MongoDBService.DescribeTopology(client, "BookingsDB");

            Assert.Contains("host1:27017", summary);
            Assert.Contains("host2:27017", summary);
            Assert.Contains("host3:27017", summary);
            Assert.Contains("replicaSet=rs0", summary);
        }
    }
}
