import axios from 'axios';

export interface Notification {
    id: string;
    bookingId: string;
    type: string;
    message: string;
    timestamp: string;
    notificationStatus: string;
    actionType?: string;
    actionPayload?: Record<string, string>;
}

const NOTIFICATION_SERVICE_URL = '/api/notification';

export const notificationServices = {
    getNotifications: async (userEmail: string): Promise<Notification[]> => {
        try {
            const response = await axios.get(`${NOTIFICATION_SERVICE_URL}/${userEmail}`);
            return response.data;
        } catch (error) {
            console.error('Error fetching notifications:', error);
            return [];
        }
    },

    subscribeToNotifications: (userEmail: string, onNotification: (notification: Notification) => void): EventSource => {
        const eventSource = new EventSource(`${NOTIFICATION_SERVICE_URL}/${userEmail}/stream`);
        
        eventSource.onmessage = (event) => {
            try {
                const notification = JSON.parse(event.data);
                onNotification(notification);
            } catch (error) {
                console.error('Error parsing notification:', error);
            }
        };

        eventSource.onerror = (error) => {
            console.error('SSE Error:', error);
        };

        return eventSource;
    },

    markAsRead: async (notificationId: string): Promise<void> => {
        try {
            await axios.patch(`${NOTIFICATION_SERVICE_URL}/${notificationId}/read`);
        } catch (error) {
            console.error('Error marking notification as read:', error);
        }
    }
};
