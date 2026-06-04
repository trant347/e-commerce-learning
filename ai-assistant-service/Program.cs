using ai_assistant_service.Services;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "ai-assistant-service";
var otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelServiceName))
    .WithTracing(tracing => tracing
        .AddSource("Kafka.Consumer")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddMeter("AiAssistant.Cache")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)));

builder.Services.AddLogging();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
    options.InstanceName = "ai:";
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(corsBuilder =>
    {
        corsBuilder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
    });
});

builder.Services.AddHttpClient<IOllamaClient, OllamaClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
    var timeoutSeconds = int.TryParse(config["Ollama:TimeoutSeconds"], out var value) ? value : 60;

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

builder.Services.AddHttpClient<IProductApiClient, ProductApiClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["ExternalServices:ProductServiceBaseUrl"] ?? "http://product-service:8080";
    client.BaseAddress = new Uri(baseUrl);
});

builder.Services.AddHttpClient<IBookingApiClient, BookingApiClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["ExternalServices:BookingServiceBaseUrl"] ?? "http://calendar-service:8080";
    client.BaseAddress = new Uri(baseUrl);
});

// Register MCP-style tools — each IToolDefinition implementation is discovered by ToolRegistry.
// SearchTaskMastersTool is also registered as its concrete type so CategoryRefreshConsumerWorker
// can inject it directly to call SetCategories().
builder.Services.AddSingleton<SearchTaskMastersTool>();
builder.Services.AddSingleton<IToolDefinition>(sp => sp.GetRequiredService<SearchTaskMastersTool>());
builder.Services.AddSingleton<IToolDefinition, GetBookingsTool>();
builder.Services.AddSingleton<ToolRegistry>();

builder.Services.AddHostedService<ai_assistant_service.MessageQueue.CategoryRefreshConsumerWorker>();

builder.Services.AddScoped<IAiAssistantService, AiAssistantService>();

var app = builder.Build();

// Categories are seeded in the background by CategoryRefreshConsumerWorker
// so the HTTP server starts immediately without blocking on product-service.

app.UseCors();
app.UseHttpsRedirection();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-assistant-service" }));
app.MapControllers();

app.Run();
