using calendar_service.Model;
using calendar_service.Services.DAO;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Payment.Contracts.V1;
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

        [Fact]
        public void RegisteredGuidSerializer_RoundTripsSagaStateSagaId()
        {
            // Regression test for the BsonSerializationException ("GuidSerializer cannot
            // serialize a Guid when GuidRepresentation is Unspecified") hit against a real
            // Mongo instance: MongoDB.Driver 3.x requires an explicit Guid representation to be
            // registered before any Guid property is (de)serialized (see MongoDbGuidSupport,
            // registered once at calendar-service startup). This exercises BSON
            // serialization/deserialization directly — no live Mongo connection required — so it
            // would have caught the regression without needing to hit a real database.
            calendar_service.MongoDbGuidSupport.Register();

            var saga = new SagaState { SagaId = Guid.NewGuid(), BookingId = "bk-1", Status = SagaState.StatusStarted, RequestedAmount = 10m };

            var bytes = saga.ToBson();
            var roundTripped = BsonSerializer.Deserialize<SagaState>(bytes);

            Assert.Equal(saga.SagaId, roundTripped.SagaId);
        }

        [Fact]
        public void RegisteredGuidSerializer_RoundTripsEmbeddedPaymentRequest()
        {
            calendar_service.MongoDbGuidSupport.Register();
            var saga = new SagaState
            {
                SagaId = Guid.NewGuid(),
                EscrowId = Guid.NewGuid(),
                BookingId = "bk-1",
                Operation = PaymentOperation.FundEscrow,
                Status = SagaState.StatusStarted,
                RequestedAmount = 10m,
                DispatchStatus = SagaDispatchStatus.PENDING
            };
            saga.PaymentRequest = new PendingPaymentRequest
            {
                SchemaVersion = PaymentRequestedV1.CurrentSchemaVersion,
                SagaId = saga.SagaId,
                EscrowId = saga.EscrowId.Value,
                BookingId = saga.BookingId,
                Operation = saga.Operation!,
                Amount = saga.RequestedAmount,
                Currency = "USD",
                PayerUserId = "alice",
                PayeeUserId = "admin-custody",
                TaskMasterUserId = "taskmaster",
                PaymentMethodToken = "pmt_token"
            };

            var document = saga.ToBsonDocument();
            var roundTripped = BsonSerializer.Deserialize<SagaState>(document);

            Assert.Equal("PENDING", document["DispatchStatus"].AsString);
            Assert.Equal(saga.EscrowId, roundTripped.EscrowId);
            Assert.Equal(SagaDispatchStatus.PENDING, roundTripped.DispatchStatus);
            Assert.NotNull(roundTripped.PaymentRequest);
            Assert.Equal(saga.SagaId, roundTripped.PaymentRequest!.SagaId);
            Assert.Equal(saga.EscrowId, roundTripped.PaymentRequest.EscrowId);
        }
    }
}
