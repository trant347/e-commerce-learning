using System.Text.Json;
using notification_service.Contracts;
using notification_service.Services;
using Xunit;

namespace notification_service.Tests
{
    /// <summary>
    /// Regression tests for the Java-to-C# JSON casing bug: messages produced by the
    /// product-service (Jackson, camelCase) must be deserialized into <see cref="NotificationMessage"/>
    /// even though the C# properties are PascalCase. Default <see cref="JsonSerializer"/>
    /// options are case-sensitive — only the shared <see cref="NotificationJsonOptions.Deserialize"/>
    /// (with PropertyNameCaseInsensitive = true) supports this.
    /// </summary>
    public class NotificationMessageDeserializationTests
    {
        // Real payload shape produced by ApplicationEventPublisher.java
        private const string JavaProducedJson =
            "{\"type\":\"TASKMASTER_APPLICATION_SUBMITTED\"," +
            "\"recipientUsername\":\"admin\"," +
            "\"message\":\"steventran has applied to become a TaskMaster.\"," +
            "\"actionType\":\"VIEW_ADMIN_APPLICATION\"," +
            "\"actionPayload\":{\"applicationId\":\"abc123\"}}";

        [Fact]
        public void Deserialize_WithSharedOptions_PopulatesAllCamelCaseFields()
        {
            var message = JsonSerializer.Deserialize<NotificationMessage>(
                JavaProducedJson, NotificationJsonOptions.Deserialize);

            Assert.NotNull(message);
            Assert.Equal("TASKMASTER_APPLICATION_SUBMITTED", message!.Type);
            Assert.Equal("admin", message.RecipientUsername);
            Assert.Equal("steventran has applied to become a TaskMaster.", message.Message);
            Assert.Equal("VIEW_ADMIN_APPLICATION", message.ActionType);
            Assert.NotNull(message.ActionPayload);
            Assert.Equal("abc123", message.ActionPayload!["applicationId"]);
        }

        [Fact]
        public void Deserialize_WithDefaultOptions_LeavesCamelCaseFieldsUnpopulated()
        {
            // Pin the bug: default options ARE case-sensitive and would silently lose data.
            // If this assertion ever flips, the .NET JSON behavior changed and the
            // PropertyNameCaseInsensitive option may no longer be required.
            var message = JsonSerializer.Deserialize<NotificationMessage>(JavaProducedJson);

            Assert.NotNull(message);
            Assert.Null(message!.RecipientUsername);
            Assert.Null(message.ActionType);
        }
    }
}
