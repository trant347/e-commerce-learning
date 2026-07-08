using notification_service.Services;
using Xunit;

namespace notification_service.Tests
{
    /// <summary>
    /// Regression tests for the recipient case-mismatch bug: TaskMaster/requester usernames
    /// are normalized to lowercase upstream (e.g. calendar-service's NormalizeUsername), but
    /// the frontend passes the username exactly as typed at login when subscribing to the
    /// SSE stream (<see cref="NotificationController"/>) and when fetching notification
    /// history (<see cref="notification_service.DAO.MongoDbService.GetNotificationsByUserEmailAsync"/>).
    /// Before the fix, "Mary" (frontend) never matched "mary" (stored by
    /// <see cref="NotificationConsumerWorker"/>), so notifications silently never reached
    /// the user. Both the write side (NotificationConsumerWorker.NormalizeRecipient) and the
    /// read side (NotificationController.NormalizeUserId) must normalize identically for the
    /// match to work.
    /// </summary>
    public class NotificationCaseNormalizationTests
    {
        [Theory]
        [InlineData("Mary", "mary")]
        [InlineData("MARY", "mary")]
        [InlineData("mary", "mary")]
        [InlineData("  Mary  ", "mary")]
        [InlineData("Johny.Smith", "johny.smith")]
        public void NormalizeRecipient_LowercasesAndTrims(string input, string expected)
        {
            Assert.Equal(expected, NotificationConsumerWorker.NormalizeRecipient(input));
        }

        [Theory]
        [InlineData("Mary", "mary")]
        [InlineData("MARY", "mary")]
        [InlineData("mary", "mary")]
        [InlineData("  Mary  ", "mary")]
        [InlineData("Johny.Smith", "johny.smith")]
        public void NormalizeUserId_LowercasesAndTrims(string input, string expected)
        {
            Assert.Equal(expected, NotificationController.NormalizeUserId(input));
        }

        [Fact]
        public void NormalizeRecipient_AndNormalizeUserId_ProduceTheSameKey_ForMismatchedCasing()
        {
            // Simulates the real bug: calendar-service publishes recipientUsername = "mary"
            // (already normalized lowercase upstream), while the frontend subscribes using
            // whatever casing the user typed at login, "Mary".
            var storedKey = NotificationConsumerWorker.NormalizeRecipient("mary");
            var lookupKey = NotificationController.NormalizeUserId("Mary");

            Assert.Equal(storedKey, lookupKey);
        }

        [Fact]
        public async Task Streamer_DeliversNotification_WhenBothSidesNormalizeMismatchedCasing()
        {
            // End-to-end (minus Kafka/Mongo) demonstration of the fix: a client subscribes
            // using the raw, as-typed-at-login username, and a notification is "produced"
            // for a differently-cased username. Both must funnel through the same
            // normalization before reaching NotificationStreamer, or the message is dropped.
            var streamer = new NotificationStreamer(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationStreamer>.Instance);
            var pipe = new System.IO.Pipelines.Pipe();
            var cts = new CancellationTokenSource();

            var subscribedUserId = NotificationController.NormalizeUserId("Mary");
            var streamTask = streamer.StreamToClientAsync(subscribedUserId, pipe.Writer, cts.Token);

            var recipientKey = NotificationConsumerWorker.NormalizeRecipient("mary");
            await streamer.SendNotificationAsync(recipientKey, new NotificationStreamedEvent
            {
                Id = "n-1",
                Type = "BOOKING_PAYMENT_REQUIRED",
                Message = "Proof submitted",
                Timestamp = DateTime.UtcNow,
                NotificationStatus = "Pending"
            });

            var result = await pipe.Reader.ReadAsync();
            cts.Cancel();
            try { await streamTask; } catch (OperationCanceledException) { }

            Assert.False(result.Buffer.IsEmpty);
        }
    }
}
