using Confluent.Kafka;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;

namespace ai_assistant_service.MessageQueue;

/// <summary>
/// Listens on the "categories-updated" Kafka topic and re-fetches the category
/// list from product-service whenever product-service publishes an event
/// (e.g. after a new TaskMaster with new job categories is created).
/// </summary>
public sealed class CategoryRefreshConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchTaskMastersTool _searchTool;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CategoryRefreshConsumerWorker> _logger;

    public CategoryRefreshConsumerWorker(
        IServiceScopeFactory scopeFactory,
        SearchTaskMastersTool searchTool,
        IConfiguration configuration,
        ILogger<CategoryRefreshConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _searchTool = searchTool;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"] ?? "kafka:29092";
        var topic            = _configuration["Kafka:CategoryTopic"]    ?? "categories-updated";
        var groupId          = _configuration["Kafka:GroupId"]          ?? "ai-assistant-service-group";

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId          = groupId,
            AutoOffsetReset  = AutoOffsetReset.Latest,
            EnableAutoCommit = true
        };

        _logger.LogInformation("CategoryRefreshConsumerWorker starting — bootstrapServers={BootstrapServers} topic={Topic}",
            bootstrapServers, topic);

        // Seed categories at startup with retry — product-service may not be ready immediately.
        const int maxSeedAttempts = 10;
        for (int attempt = 1; attempt <= maxSeedAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            _logger.LogInformation("Seeding categories at startup, attempt {Attempt}/{Max}", attempt, maxSeedAttempts);
            bool seeded = await RefreshCategoriesAsync(stoppingToken);
            if (seeded) break;

            var delay = TimeSpan.FromSeconds(Math.Min(attempt * 3, 30));
            _logger.LogWarning("Category seed attempt {Attempt} failed — retrying in {Delay}s", attempt, delay.TotalSeconds);
            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation($"Complete seeding categories at startup.");
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(config).Build();
        consumer.Subscribe(topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Consume blocks until a message arrives or the token is cancelled.
                consumer.Consume(stoppingToken);

                _logger.LogInformation("Received categories-updated event — refreshing category list");
                await RefreshCategoriesAsync(stoppingToken);            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogWarning(ex, "Kafka consume error — retrying in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in CategoryRefreshConsumerWorker — retrying in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        consumer.Close();
        _logger.LogInformation("CategoryRefreshConsumerWorker stopped");
    }

    private async Task<bool> RefreshCategoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // IProductApiClient is a typed HttpClient (transient) — resolve via scope.
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IProductApiClient>();
            var categories = await client.FetchCategoriesAsync(cancellationToken);
            if (categories.Length > 0)
            {
                _searchTool.SetCategories(categories);
                _logger.LogInformation("Category list refreshed — {Count} categories: {Categories}",
                    categories.Length, string.Join(", ", categories));
                return true;
            }

            _logger.LogWarning("Category list from product-service was empty");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh categories from product-service");
            return false;
        }
    }
}
