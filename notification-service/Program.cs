using Confluent.Kafka;
using MongoDB.Driver;
using notification_service;
using notification_service.DAO;
using notification_service.Services;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "notification-service";
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
builder.Services.AddLogging();
// Register MongoDB client as singleton
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("MongoDB");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("ConnectionStrings:MongoDB is required");
    return new MongoClient(connectionString);
});

// Register MongoDB service as scoped
builder.Services.AddScoped<IMongoDbService, MongoDbService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Register NotificationStreamer as singleton (maintains user connections)
builder.Services.AddSingleton<INotificationStreamer, NotificationStreamer>();

// Register Kafka consumer configuration as singleton
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    
    var bootstrapServers = configuration["KafkaConsumerConfig:BootstrapServers"];
    var groupId = configuration["KafkaConsumerConfig:GroupId"];
    
    if (string.IsNullOrWhiteSpace(bootstrapServers))
        throw new InvalidOperationException("KafkaConsumerConfig:BootstrapServers is required");
    
    if (string.IsNullOrWhiteSpace(groupId))
        throw new InvalidOperationException("KafkaConsumerConfig:GroupId is required");
    
    return new ConsumerConfig
    {
        BootstrapServers = bootstrapServers,
        GroupId = groupId,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = bool.Parse(configuration["KafkaConsumerConfig:AutoCommit"] ?? "true")
    };
});

// Register Kafka consumer background service
builder.Services.AddHostedService<NotificationConsumerWorker>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});


var app = builder.Build();
app.UseCors();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();

