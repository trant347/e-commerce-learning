using calendar_service.Model;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace calendar_service.MessageQueue
{
    public class BookingProducer : IBookingProducer<string, Booking>
    {
        private readonly IProducer<string, Booking> _producer;
        private readonly string _topic;

        public BookingProducer(string topic, IProducer<string, Booking> producer)
        {
            _topic =  topic;
            _producer = producer;
        }

        public async Task ProduceBookingAsync(string key, Booking value)
        {
            // Implementation for producing a booking message
            await _producer.ProduceAsync(_topic, new Message<string, Booking>
            {
                Key = key,
                Value = value
            });
        }

        public void Dispose()
        {
            // Cleanup resources
        }
    }
}
