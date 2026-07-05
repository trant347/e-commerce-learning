using Consul;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using payment_service.Data;
using payment_service.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

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


