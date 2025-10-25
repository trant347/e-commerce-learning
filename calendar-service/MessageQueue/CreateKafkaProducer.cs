using calendar_service.Model;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace calendar_service.MessageQueue
{
    public static class CreateKafkaProducer
    {
        public static IBookingProducer<string, Booking> CreateProducer(KafkaProducerConfig config)
        {
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = config.BootstrapServers,
                ClientId = config.ClientId
            };
            var producer = new ProducerBuilder<string, Booking>(producerConfig).SetValueSerializer(new BookingSerializer<Booking>()).Build();
            return new BookingProducer(config.Topic, producer);
        }
    }
}
