using System.Text.Json;
using payment_service.Services;

namespace payment_service.MessageQueue
{
    /// <summary>
    /// Parses a "user-events" Kafka payload and, if it's a USER_REGISTERED event, ensures the
    /// user has a wallet. Pulled out of UserRegisteredConsumerWorker so this dispatch/parsing
    /// logic can be unit tested directly, without needing a real Kafka broker.
    /// </summary>
    public static class UserRegisteredEventHandler
    {
        public const string UserRegisteredType = "USER_REGISTERED";

        public static async Task HandleAsync(string payload, IWalletService wallets, ILogger logger)
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, UserRegisteredType, StringComparison.Ordinal))
            {
                logger.LogDebug("Ignoring user-event of type '{Type}'", type);
                return;
            }

            var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(username))
            {
                logger.LogWarning("USER_REGISTERED event missing 'username': {Payload}", payload);
                return;
            }

            var wallet = await wallets.CreateWalletAsync(username);

            logger.LogInformation("USER_REGISTERED wallet ensured for '{Username}': balance={Balance}", username, wallet.Balance);
        }
    }
}
