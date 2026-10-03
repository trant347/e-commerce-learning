using ai_assistant_service.Auth;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;
using ai_assistant_service.Services.Telemetry;
using ai_assistant_service.Services.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "ai-assistant-service";
var otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelServiceName))
    .WithTracing(tracing => tracing
        .AddSource(AiAssistantTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddMeter(AiAssistantTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)));


var jwtSecret = builder.Configuration["JwtSettings:Secret"]
    ?? throw new InvalidOperationException("JwtSettings:Secret is required.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = "sub",
            RoleClaimType = "authorities"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(CustomAuthorizeAttribute.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new AuthorizeUserRequirement());
    });
});

builder.Services.AddSingleton<IAuthorizationHandler, AuthorizeUserHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

builder.Services.AddLogging();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAiModelRouting(builder.Configuration);
builder.Services
    .AddOptions<McpDiscoveryOptions>()
    .Bind(builder.Configuration.GetSection(McpDiscoveryOptions.SectionName));
builder.Services
    .AddOptions<CategoryResolutionOptions>()
    .Bind(builder.Configuration.GetSection(CategoryResolutionOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CategoryResolutionOptions>, CategoryResolutionOptionsValidator>();
builder.Services.AddHttpClient<IJevDecisionClient, JevDecisionClient>(
    client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CategoryCatalogSnapshotStore>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(corsBuilder =>
    {
        corsBuilder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
    });
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
builder.Services.AddSingleton<McpCategoryEnumRefresher>();

// MCP tool discovery — connects to remote MCP servers and registers their tools dynamically.
builder.Services.AddHostedService<McpToolDiscoveryService>();

builder.Services.AddScoped<IAiAssistantService, AiAssistantService>();

var app = builder.Build();

app.UseCors();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-assistant-service" }));
app.MapGet("/health/ready", (CategoryCatalogSnapshotStore snapshots) =>
{
    var readiness = snapshots.GetReadiness();
    var body = new
    {
        status = readiness.Status,
        service = "ai-assistant-service",
        categoryCatalog = new
        {
            required = readiness.Required,
            usable = readiness.Usable,
            catalogVersion = readiness.CatalogVersion,
            lastSuccessfulRefreshAt = readiness.LastSuccessfulRefreshAt,
            detail = readiness.Detail
        }
    };
    return readiness.Usable ? Results.Ok(body) : Results.Json(body, statusCode: 503);
});
app.MapControllers();

app.Run();

public partial class Program;
