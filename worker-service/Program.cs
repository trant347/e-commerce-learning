using MongoDB.Driver;
using worker_service;
using worker_service.DAO;
using worker_service.MessageQueue;
using worker_service.Services;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "worker-service";
var otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelServiceName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)));

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
