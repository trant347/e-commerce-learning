using calendar_service.Auth;
using calendar_service.MessageQueue;
using calendar_service.Models.ConsulConfig;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using calendar_service.Services.Implementation;
using Consul;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "calendar-service";
var otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelServiceName))
    .WithTracing(tracing => tracing
        .AddSource("Kafka.Producer")
        .AddSource("Kafka.Consumer")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)));

builder.Services.AddOptions<KafkaProducerConfig>().Bind(builder.Configuration.GetSection("KafkaProducerConfig"));

builder.Services.AddSingleton<IMongoDBService, MongoDBService>();
builder.Services.AddSingleton<IBookingService, BookingService>();
builder.Services.AddSingleton<INotificationProducer, NotificationProducer>();

builder.Services.AddOptions<JwtSettings>().Bind(builder.Configuration.GetSection("JwtSettings"));

builder.Services.AddHttpClient<ITaskMasterApiClient, TaskMasterApiClient>((sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["ExternalServices:ProductServiceBaseUrl"] ?? "http://product-service:8080";
    client.BaseAddress = new Uri(baseUrl);
});

builder.Services.AddHttpClient<IPaymentApiClient, PaymentApiClient>((sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["ExternalServices:PaymentServiceBaseUrl"] ?? "http://payment-service:8080";
    client.BaseAddress = new Uri(baseUrl);
});

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
builder.Services.AddHostedService<calendar_service.MessageQueue.UserEventConsumerWorker>();
builder.Services.AddHealthChecks();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


app.UseHttpsRedirection();

app.UseMiddleware<JwtAuthMiddleware>();
app.UseAuthorization();

app.MapControllers();

// Map the Health Check endpoint
// This creates the /health endpoint that will return 200 OK if the app is running.
app.MapHealthChecks("/health");

app.Run();
