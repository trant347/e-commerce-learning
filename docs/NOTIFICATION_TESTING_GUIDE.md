# Testing the Notification System

## Prerequisites
- All Docker services running: `docker-compose up -d`
- User account created and logged in
- Frontend accessible at `http://localhost:3000`
- Notification service at `http://localhost:8084`

## Test Steps

### 1. Visual Verification (Quick Test)
1. Open browser: `http://localhost:3000`
2. Log in with your user account (email)
3. Look at the top-right header
4. You should see a **bell icon (🔔)** next to "Order Status" and "Wishlist"
5. If you have notifications, you'll see a red badge with the count

### 2. View Existing Notifications
1. Click the bell icon
2. A dropdown will appear showing:
   - Notification count
   - List of recent notifications
   - Timestamp for each notification
   - Different icons based on notification type

### 3. Test Real-Time Notifications

#### Option A: Create a Booking
1. Click "Book Now" in the header
2. Select a date and create a booking
3. The booking will be processed:
   - Calendar Service → Kafka (bookings topic)
   - Worker Service → Processes → Kafka (notification-events topic)
   - Notification Service → Saves to MongoDB + SSE to browser
4. **Watch the bell icon** - it should update automatically within seconds!
5. Click the bell to see the new notification

#### Option B: Direct API Test
You can test the SSE connection directly:

```bash
# In a browser or curl, connect to the SSE stream
# Replace YOUR_EMAIL with your user's email
curl -N http://localhost:8084/api/notification/YOUR_EMAIL/stream

# In another terminal, publish a test notification directly to MongoDB
# (This would normally come from Kafka)
```

### 4. Test REST API

#### Get Notifications for a User
```bash
# Replace YOUR_EMAIL with actual user email
curl http://localhost:8084/api/notification/YOUR_EMAIL
```

Expected response:
```json
[
  {
    "id": "...",
    "bookingId": "...",
    "type": "booking_confirmed",
    "recipientEmail": "user@example.com",
    "message": "Your booking has been confirmed",
    "timestamp": "2026-01-18T03:00:00Z",
    "status": "Pending"
  }
]
```

### 5. Verify SSE Connection

Open Browser DevTools:
1. Press F12
2. Go to "Network" tab
3. Filter by "notification"
4. Look for a request to `/api/notification/{email}/stream`
5. Type should be "EventStream"
6. Status should be "200 OK" and connection remains open

### 6. Mark as Read
1. Click on any notification in the dropdown
2. The notification should change appearance (no longer bold)
3. Unread count should decrease

## Expected Behavior

### Notification Types and Icons:
- 🟢 **booking_confirmed** - Green checkmark
- 🔴 **booking_cancelled** - Red X
- 🔵 **booking_updated** - Blue edit icon
- 🟠 **reminder** - Orange clock

### Notification Flow:
```
User Books → Calendar Service → Kafka → Worker Service → 
Kafka (notification-events) → Notification Service → 
MongoDB + SSE → Browser (Bell Icon Updates)
```

## Troubleshooting

### Bell Icon Not Showing
- Check browser console for errors (F12 → Console)
- Verify user is logged in (username should be set)
- Check frontend logs: `docker logs e-commerce-learning-frontend-1`

### No Notifications Appearing
- Check notification service logs: `docker logs e-commerce-learning-notification-service-1`
- Verify Kafka is running: `docker logs e-commerce-learning-kafka-1`
- Check if notifications exist in MongoDB:
  ```bash
  docker exec -it e-commerce-learning-mongodb-1 mongosh
  use NotificationDB
  db.Notifications.find()
  ```

### SSE Not Connecting
- Check CORS headers in notification service
- Verify proxy route in frontend app.js
- Check browser console for connection errors
- Verify Consul service discovery is working

### Real-Time Updates Not Working
1. Verify SSE connection is active (DevTools → Network)
2. Check if NotificationStreamer is receiving events
3. Verify Kafka consumer is running:
   ```bash
   docker logs e-commerce-learning-notification-service-1 | grep "NotificationConsumerWorker"
   ```

## Debug Commands

```bash
# Check all services
docker-compose ps

# View notification service logs
docker logs -f e-commerce-learning-notification-service-1

# Check Kafka topics
docker exec e-commerce-learning-kafka-1 kafka-topics --list --bootstrap-server localhost:9092

# View MongoDB notifications
docker exec -it e-commerce-learning-mongodb-1 mongosh NotificationDB --eval "db.Notifications.find().pretty()"

# Test Consul service registration
curl http://localhost:8500/v1/catalog/service/notification-service
```

## Success Indicators
✅ Bell icon visible in header when logged in  
✅ Clicking bell shows notification dropdown  
✅ Creating a booking generates a notification automatically  
✅ Bell badge count updates in real-time  
✅ No errors in browser console  
✅ SSE connection stays open in Network tab  
✅ Notification service logs show "Streaming to client"  
