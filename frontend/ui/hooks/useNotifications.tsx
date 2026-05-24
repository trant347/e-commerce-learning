import { useState, useEffect, useRef } from 'react';
import { notificationServices, Notification } from '../api/notificationServices';

export const useNotifications = (userEmail: string | null) => {
    const [notifications, setNotifications] = useState<Notification[]>([]);
    const [unreadCount, setUnreadCount] = useState<number>(0);
    const [isLoading, setIsLoading] = useState<boolean>(false);
    const [lastNotification, setLastNotification] = useState<Notification | null>(null);
    const eventSourceRef = useRef<EventSource | null>(null);

    // Fetch initial notifications
    useEffect(() => {
        if (!userEmail) {
            setNotifications([]);
            setUnreadCount(0);
            return;
        }

        const fetchNotifications = async () => {
            setIsLoading(true);
            try {
                const data = await notificationServices.getNotifications(userEmail);
                setNotifications(data);
                // Count unread (e.g., where status is "Pending" or any logic you prefer)
                const unread = data.filter(n => n.status === 'Pending').length;
                setUnreadCount(unread);
            } catch (error) {
                console.error('Error loading notifications:', error);
            } finally {
                setIsLoading(false);
            }
        };

        fetchNotifications();
    }, [userEmail]);

    // Subscribe to SSE for real-time notifications
    useEffect(() => {
        if (!userEmail) {
            return;
        }

        const eventSource = notificationServices.subscribeToNotifications(
            userEmail,
            (newNotification: Notification) => {
                setNotifications(prev => [newNotification, ...prev]);
                setLastNotification(newNotification);
                // Increment unread count for new notifications
                if (newNotification.status === 'Pending') {
                    setUnreadCount(prev => prev + 1);
                }
            }
        );

        eventSourceRef.current = eventSource;

        return () => {
            if (eventSourceRef.current) {
                eventSourceRef.current.close();
                eventSourceRef.current = null;
            }
        };
    }, [userEmail]);

    const markAsRead = (notificationId: string) => {
        setNotifications(prev =>
            prev.map(n =>
                n.id === notificationId ? { ...n, status: 'Read' } : n
            )
        );
        setUnreadCount(prev => Math.max(0, prev - 1));
        notificationServices.markAsRead(notificationId);
    };

    const clearAll = () => {
        setNotifications([]);
        setUnreadCount(0);
    };

    return {
        notifications,
        unreadCount,
        isLoading,
        lastNotification,
        markAsRead,
        clearAll
    };
};
