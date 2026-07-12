using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace calendar_service
{
    /// <summary>
    /// MongoDB.Driver 3.x removed the ability to leave Guid serialization ambiguous (no more
    /// implicit "Unspecified" representation, and the old per-property [BsonGuidRepresentation]
    /// attribute is deprecated and no longer honored) — see PAYMENT_SAGA_SPEC.md. Without this,
    /// writing a <see cref="calendar_service.Model.SagaState"/> (whose SagaId is a Guid) throws
    /// <c>BsonSerializationException: GuidSerializer cannot serialize a Guid when
    /// GuidRepresentation is Unspecified</c>. Registering a single global Guid serializer at
    /// startup (before any Mongo document is (de)serialized) fixes this for every Guid property
    /// in the app, not just SagaState.SagaId.
    /// </summary>
    public static class MongoDbGuidSupport
    {
        public static void Register()
        {
            try
            {
                BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            }
            catch (BsonSerializationException)
            {
                // Already registered (e.g. called more than once in the same process, such as
                // multiple WebApplicationFactory-hosted test runs) — safe to ignore since the
                // registration is idempotent in intent.
            }
        }
    }
}
