using Consul;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using payment_service.Data;
using payment_service.MessageQueue;
using payment_service.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var otelServiceName =
    builder.Configuration["OTEL_SERVICE_NAME"] ?? "payment-service";
var otelEndpoint =
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
    ?? "http://otel-collector:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelServiceName))
    .WithTracing(tracing => tracing
        .AddSource("Kafka.Producer")
        .AddSource("Kafka.Consumer")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(options =>
            options.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddMeter(payment_service.Observability.PaymentSagaMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(options =>
            options.Endpoint = new Uri(otelEndpoint)));

builder.Services.AddControllers();

// PostgreSQL (EF Core) — ACID-compliant relational store for financial transaction records.
builder.Services.AddDbContext<PaymentDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Postgres is required");
    }
    options.UseNpgsql(connectionString);
});
builder.Services.AddScoped<IPaymentService, payment_service.Services.PaymentService>();
builder.Services.AddScoped<payment_service.Services.IPaymentGateway, payment_service.Services.WalletSimulationPaymentGateway>();
builder.Services.AddScoped<payment_service.Services.IWalletService, payment_service.Services.WalletService>();
builder.Services.Configure<PaymentMethodTokenOptions>(builder.Configuration.GetSection("PaymentMethodTokens"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPaymentMethodTokenService, PaymentMethodTokenService>();
builder.Services.AddScoped<IPaymentMethodTokenCleanupService, PaymentMethodTokenCleanupService>();
builder.Services.AddScoped<IEscrowService, EscrowService>();
builder.Services.AddScoped<IPaymentRequestProcessor, PaymentRequestProcessor>();
builder.Services.AddScoped<IPaymentResultOutboxStore, PaymentResultOutboxStore>();
builder.Services.Configure<PaymentResultProducerOptions>(
    builder.Configuration.GetSection("PaymentResultProducer"));
builder.Services.AddSingleton<IPaymentResultProducer, PaymentResultProducer>();
builder.Services.AddSingleton<IKafkaDeadLetterProducer, KafkaDeadLetterProducer>();
builder.Services.AddHostedService<CustodyWalletInitializer>();
builder.Services.AddHostedService<PaymentMethodTokenCleanupWorker>();
builder.Services.AddHostedService<payment_service.MessageQueue.UserRegisteredConsumerWorker>();
builder.Services.AddHostedService<payment_service.MessageQueue.PaymentRequestConsumerWorker>();
builder.Services.AddHostedService<PaymentResultOutboxWorker>();
builder.Services.AddHostedService<CustodyReconciliationWorker>();

// Consul service discovery
builder.Services.Configure<payment_service.ConsulConfig.ConsulConfig>(builder.Configuration.GetSection("ConsulConfig"));
builder.Services.AddSingleton<IConsulClient, ConsulClient>(p =>
{
    var consulConfig = p.GetRequiredService<IOptions<payment_service.ConsulConfig.ConsulConfig>>().Value;
    return new ConsulClient(cfg =>
    {
        cfg.Address = new Uri(consulConfig.ConsulAddress);
    });
});
builder.Services.AddHostedService<payment_service.ConsulConfig.ConsulHostedService>();

builder.Services.AddHealthChecks();

var app = builder.Build();

// Apply pending EF Core migrations on startup so the schema is always up to date.
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    dbContext.Database.Migrate();
}

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
