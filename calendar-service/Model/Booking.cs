using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Runtime.Serialization;

namespace calendar_service.Model
{
    [DataContract]
    public class Booking
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        [DataMember]
        public string? Id { get; set; }

        [DataMember(IsRequired = true, Name = "description")]
        public string Description { get; set; }

        [DataMember(IsRequired = true, Name = "startTime")]
        public DateTime StartTime { get; set; }

        [DataMember(IsRequired = true, Name = "endTime")]
        public DateTime EndTime { get; set; }

    }
}
