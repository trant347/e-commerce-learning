using calendar_service.Model;
using Confluent.Kafka;

namespace calendar_service.MessageQueue
{
    public class BookingSerializer<T> : ISerializer<T>
    {
        public byte[] Serialize (T data, SerializationContext context)
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data);
        }
    }
}
