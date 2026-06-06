using ai_assistant_service.Services;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;
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
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)));

builder.Services.AddLogging();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

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

builder.Services.AddHttpClient<IBookingApiClient, BookingApiClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["ExternalServices:BookingServiceBaseUrl"] ?? "http://calendar-service:8080";
    client.BaseAddress = new Uri(baseUrl);
});

// Local tools — booking tool remains local until calendar-service has MCP.
// Product-service tools are discovered dynamically via MCP below.
builder.Services.AddSingleton<IToolDefinition, GetBookingsTool>();
builder.Services.AddSingleton<ToolRegistry>();

// MCP tool discovery — connects to remote MCP servers and registers their tools dynamically.
builder.Services.AddHostedService<McpToolDiscoveryService>();

builder.Services.AddScoped<IAiAssistantService, AiAssistantService>();

var app = builder.Build();

app.UseCors();
app.UseHttpsRedirection();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-assistant-service" }));
app.MapControllers();

app.Run();
