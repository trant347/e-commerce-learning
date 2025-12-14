using MongoDB.Driver;
using worker_service;
using worker_service.DAO;
using worker_service.MessageQueue;
using worker_service.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetValue<string>("ConnectionsString");
    var databaseName = configuration.GetValue<string>("MongoBookingDatabaseName") ?? "BookingsDB";
    
    if (string.IsNullOrEmpty(connectionString))
    {
        throw new InvalidOperationException(
            "MongoDB connection string is not configured. Please set the 'ConnectionsString' environment variable or add it to appsettings.json");
    }
    
    Console.WriteLine($"Connecting to MongoDB with connection string: {connectionString}");
    Console.WriteLine($"Using database: {databaseName}");
    
    return new MongoClient(connectionString).GetDatabase(databaseName);
});
builder.Services.AddSingleton<IBookingService, BookingService>();
builder.Services.AddSingleton<IProcessBookingService, ProcessBookingService>();
builder.Services.AddOptions<KafkaProducerConfig>()
    .Bind(builder.Configuration.GetSection("Kafka"))
    .ValidateDataAnnotations();
builder.Services.AddSingleton(provider =>
{
    var config = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KafkaProducerConfig>>().Value;
    Console.WriteLine($"Creating Kafka Producer with the following configuration: {config.BootstrapServers} , {config.OutputTopics}");
    return CreateKafkaProducer.CreateProducer(config);
});

builder.Services.AddHostedService<BookingJobConsumerWorker>();


builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

var app = builder.Build();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
