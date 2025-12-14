using Confluent.Kafka;
using MongoDB.Driver;
using notification_service;
using notification_service.DAO;
using notification_service.Services;

var builder = WebApplication.CreateBuilder(args);

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


var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();

