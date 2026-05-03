using MongoDB.Driver;
using notification_service.DAO;
using notification_service.Model;

namespace notification_service.Services
{
    public class MongoDbService : IMongoDbService
    {
        private readonly ILogger<MongoDbService> _logger;
        private readonly IMongoClient _mongoClient;
        
        public MongoDbService(IMongoClient mongoClient, IConfiguration configuration, ILogger<MongoDbService> logger)
        {
            _logger = logger;
            _mongoClient = mongoClient;
        }

        public async Task CreateNotificationAsync(NotificationEventModel notificationEvent)
        {
            var database = _mongoClient.GetDatabase("NotificationDB");
            var collection = database.GetCollection<NotificationEventModel>("Notifications");
            await collection.InsertOneAsync(notificationEvent);
            _logger.LogInformation("Created notification event with ID: {Id}", notificationEvent.Id);
        }

        public async Task<NotificationEventModel?> GetNotificationByIdAsync(string id)
        {
            var database = _mongoClient.GetDatabase("NotificationDB");
            var collection = database.GetCollection<NotificationEventModel>("Notifications");
            var filter = Builders<NotificationEventModel>.Filter.Eq(n => n.Id, id);
            return await collection.Find(filter).FirstOrDefaultAsync();
        }

        public async Task UpdateNotificationStatusAsync(string id, string status, string? errorMessage)
        {
            var database = _mongoClient.GetDatabase("NotificationDB");
            var collection = database.GetCollection<NotificationEventModel>("Notifications");
            var filter = Builders<NotificationEventModel>.Filter.Eq(n => n.Id, id);
            var update = Builders<NotificationEventModel>.Update
                .Set(n => n.Status, status)
                .Set(n => n.ErrorMessage, errorMessage)
                .Set(n => n.SentAt, DateTime.UtcNow);
            await collection.UpdateOneAsync(filter, update);
            _logger.LogInformation("Updated notification event ID: {Id} to status: {Status}", id, status);
        }

        public async Task<List<NotificationEventModel>> GetPendingNotificationsAsync()
        {
            var database = _mongoClient.GetDatabase("NotificationDB");
            var collection = database.GetCollection<NotificationEventModel>("Notifications");
            var filter = Builders<NotificationEventModel>.Filter.Eq(n => n.Status, "Pending");
            return await collection.Find(filter).ToListAsync();
        }

        public async Task<List<NotificationEventModel>> GetNotificationsByUserEmailAsync(string email, int limit = 50)
        {
            var database = _mongoClient.GetDatabase("NotificationDB");
            var collection = database.GetCollection<NotificationEventModel>("Notifications");
            var filter = Builders<NotificationEventModel>.Filter.Eq(n => n.RecipientEmail, email);
            var sort = Builders<NotificationEventModel>.Sort.Descending(n => n.Timestamp);
            return await collection.Find(filter).Sort(sort).Limit(limit).ToListAsync();
        }
    }
}