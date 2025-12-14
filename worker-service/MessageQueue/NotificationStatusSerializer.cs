namespace worker_service.MessageQueue
{
    public class NotificationStatusSerializer<T> : Confluent.Kafka.ISerializer<T>
    {
        public byte[] Serialize(T data, Confluent.Kafka.SerializationContext context)
        {
            if (data == null)
            {
                return Array.Empty<byte>();
            }
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }
    }
}
