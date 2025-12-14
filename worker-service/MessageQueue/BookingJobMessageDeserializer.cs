using Confluent.Kafka;
using System.Text;
using System.Text.Json;

namespace worker_service.MessageQueue
{
    public class BookingJobMessageDeserializer<T> : IDeserializer<T>
    {
        public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
        {
            if (isNull || data.IsEmpty)
            {
                return default(T);
            }

            var json = Encoding.UTF8.GetString(data);
            return JsonSerializer.Deserialize<T>(json);
        }
    }
}
