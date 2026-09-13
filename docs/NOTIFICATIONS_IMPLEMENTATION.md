# Real-Time Notifications Implementation

## Overview
Implemented a real-time notification system using Server-Sent Events (SSE) that displays notifications to users based on their email address. Notifications are automatically updated when new events occur (e.g., booking status changes).

## Architecture

### Backend (Notification Service - C#/.NET)
1. **REST API Endpoint**: `GET /api/notification/{userId}` - Fetches historical notifications
2. **SSE Endpoint**: `GET /api/notification/{userId}/stream` - Real-time notification streaming
3. **Kafka Integration**: Consumes notification events and pushes them to connected clients
4. **MongoDB**: Stores notification history

### Frontend (React/TypeScript)
1. **Custom Hook**: `useNotifications` - Manages notification state and SSE connection
2. **API Service**: `notificationServices` - Handles HTTP and SSE communication
3. **UI Component**: `NotificationBell` - Interactive bell icon with dropdown
4. **Express Proxy**: Routes notification requests through Consul service discovery

## How It Works

### Flow:
1. **Worker Service** processes bookings and publishes notification events to Kafka
2. **Notification Service** consumes Kafka events and:
   - Saves to MongoDB
   - Pushes to connected SSE clients (via NotificationStreamer)
3. **Frontend** (when user logs in):
   - Fetches initial notifications via REST API
   - Opens SSE connection to receive real-time updates
   - Displays notification count in bell icon
   - Shows notifications in dropdown popup

### Real-Time Updates:
- When a booking status changes, the worker service publishes to Kafka
- Notification service receives the event
- If the user is connected, they immediately receive the notification via SSE
- Notification bell updates in real-time without page refresh

## Files Modified/Created

### Backend (notification-service/)
- ✅ `Controllers/NotificationController.cs` - Added GET endpoint for fetching notifications
- ✅ `DAO/IMongoDBService.cs` - Added GetNotificationsByUserEmailAsync method
- ✅ `DAO/MongoDBService.cs` - Implemented query for user notifications
- ✅ `Services/NotificationService.cs` - Integrated with NotificationStreamer
- ✅ `Services/Streamers/NotificationStreamedEvent.cs` - Updated model
- ✅ `Program.cs` - Registered NotificationStreamer and added CORS

### Frontend
- ✅ `ui/api/notificationServices.tsx` - API client for notifications
- ✅ `ui/hooks/useNotifications.tsx` - React hook for notification management
- ✅ `ui/components/notifications/NotificationBell.tsx` - Bell icon component
- ✅ `ui/components/notifications/notifications.css` - Styling
- ✅ `ui/components/page-header/index.tsx` - Integrated notification bell
- ✅ `routes/notification.js` - Express proxy route
- ✅ `app.js` - Added notification route

## Usage

### For Users:
1. Log in to the application
2. The notification bell appears in the header (when logged in)
3. Red badge shows unread notification count
4. Click the bell to see notification list
5. Click a notification to mark it as read
6. Notifications update automatically when booking status changes

### For Developers:

#### Build & Deploy:
```bash
# Build notification service
cd notification-service
dotnet build
docker build -t notification-service:latest .

# Build frontend
cd ../frontend
npm run build
docker build -t frontend:latest .

# Restart services
cd ..
docker-compose down
docker-compose up -d
```

#### Testing:
1. Create a booking via the calendar service
2. Check the notification bell for new notifications
3. Observe real-time updates when booking status changes

## Configuration

### Environment Variables (docker-compose.yml):
```yaml
notification-service:
  environment:
    - KafkaConsumerConfig__BootstrapServers=kafka:29092
    - KafkaConsumerConfig__GroupId=notification-service-group
    - KafkaConsumerConfig__Topics_0=notification-events
    - ConnectionsString=${ConnectionsString}
```

### Frontend Proxy:
The frontend proxies notification requests through Consul to the notification-service:
- Frontend → `/api/notification/*` → Consul → notification-service

## Key Features

✅ **Real-Time Updates**: SSE provides instant notification delivery  
✅ **Persistent Storage**: All notifications saved to MongoDB  
✅ **User-Specific**: Notifications filtered by user email  
✅ **Unread Count**: Badge shows number of unread notifications  
✅ **Auto-Reconnect**: SSE automatically reconnects on disconnect  
✅ **Scalable**: Uses Kafka for reliable message delivery  
✅ **Type-Safe**: TypeScript interfaces for notifications  
✅ **Responsive UI**: Semantic UI components with custom styling  

## Future Enhancements

- [ ] Mark all as read functionality
- [ ] Delete notifications
- [ ] Notification preferences/settings
- [ ] Push notifications for mobile
- [ ] Notification sounds
- [ ] Filter notifications by type
- [ ] Pagination for notification list
