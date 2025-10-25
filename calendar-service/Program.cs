using calendar_service.MessageQueue;
using calendar_service.Models.ConsulConfig;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using calendar_service.Services.Implementation;
using Consul;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Remove this line to fix ASP0000 diagnostic:
// var serviceProvider = builder.Services.BuildServiceProvider();
builder.Services.AddOptions<KafkaProducerConfig>().Bind(builder.Configuration.GetSection("KafkaProducerConfig"));
// Instead, inject IOptions<KafkaProducerConfig> using the service provider in the AddSingleton registration:
builder.Services.AddSingleton(provider =>
{
    var kafkaConfig = provider.GetRequiredService<IOptions<KafkaProducerConfig>>().Value;
    Console.WriteLine($"Kafka config: {kafkaConfig.BootstrapServers}");
    return CreateKafkaProducer.CreateProducer(kafkaConfig);
});

builder.Services.AddSingleton<IMongoDBService, MongoDBService>();
builder.Services.AddSingleton<IBookingService, BookingService>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
//string connectionString = Environment.GetEnvironmentVariable("ConnectionsString", EnvironmentVariableTarget.Process) ?? "";

//// Debug only
//Console.WriteLine($"Connection string: {connectionString}");

//builder.Services.AddSingleton(new MongoDBService(connectionString, "CalendarDB"));

// Configure Consul
builder.Services.Configure<ConsulConfig>(builder.Configuration.GetSection(nameof(ConsulConfig)));

builder.Services.AddSingleton<IConsulClient, ConsulClient>(p =>
{
    var consulConfig = p.GetRequiredService<IOptions<ConsulConfig>>().Value;
    return new ConsulClient(cfg =>
    {
        cfg.Address = new Uri(consulConfig.ConsulAddress);
    });
});

// Register the hosted service for Consul registration
builder.Services.AddHostedService<ConsulHostedService>();
builder.Services.AddHealthChecks();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

// Map the Health Check endpoint
// This creates the /health endpoint that will return 200 OK if the app is running.
app.MapHealthChecks("/health");

app.Run();
