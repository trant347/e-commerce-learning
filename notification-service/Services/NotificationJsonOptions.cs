using System.Text.Json;

namespace notification_service.Services
{
    public static class NotificationJsonOptions
    {
        public static readonly JsonSerializerOptions Deserialize = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static readonly JsonSerializerOptions Serialize = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
