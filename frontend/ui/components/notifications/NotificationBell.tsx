import * as React from 'react';
import { useNavigate } from 'react-router-dom';
import { Popup, Label, List, Icon } from 'semantic-ui-react';
import { formatDistance } from 'date-fns';
import { Notification } from '../../api/notificationServices';
import './notifications.css';

interface NotificationBellProps {
    notifications: Notification[];
    unreadCount: number;
    onMarkAsRead?: (notificationId: string) => void;
}

export function resolveNotificationActionUrl(
    actionType: string | undefined,
    payload: Record<string, string> | undefined,
    notificationType?: string,
): string | null {
    const p = payload ?? {};
    // Existing acceptance notifications used the generic outgoing-booking action. Route those
    // records to payment as well so users are not stranded by notifications created pre-fix.
    if (notificationType === 'BOOKING_REQUEST_ACCEPTED' && p.bookingId) {
        return `/booking/${p.bookingId}/pay`;
    }
    if (!actionType) return null;
    switch (actionType) {
        case 'VIEW_MY_APPLICATION':    return '/my-application';
        case 'VIEW_ADMIN_APPLICATION': return `/admin/applications/${p.applicationId}`;
        case 'VIEW_TASKMASTER':        return `/product/${p.taskMasterId}`;
        case 'VIEW_INCOMING_BOOKING_REQUEST': return '/bookings/incoming';
        case 'VIEW_OUTGOING_BOOKING_REQUEST': return p.taskMasterId ? `/booking/${p.taskMasterId}` : '/';
        case 'VIEW_PAYMENT_REQUEST': return p.bookingId ? `/booking/${p.bookingId}/pay` : '/';
        default: return null;
    }
}

export const NotificationBell: React.FC<NotificationBellProps> = ({
    notifications,
    unreadCount,
    onMarkAsRead
}) => {
    const navigate = useNavigate();

    const getNotificationIcon = (type: string) => {
        switch (type.toLowerCase()) {
            case 'booking_confirmed':
                return 'check circle';
            case 'booking_cancelled':
                return 'times circle';
            case 'booking_updated':
                return 'edit';
            case 'booking_payment_required':
                return 'credit card';
            case 'booking_payment_received':
                return 'dollar sign';
            case 'reminder':
                return 'clock';
            default:
                return 'bell';
        }
    };

    const getNotificationColor = (type: string) => {
        switch (type.toLowerCase()) {
            case 'booking_confirmed':
                return 'green';
            case 'booking_cancelled':
                return 'red';
            case 'booking_updated':
                return 'blue';
            case 'booking_payment_required':
                return 'orange';
            case 'booking_payment_received':
                return 'green';
            case 'reminder':
                return 'orange';
            default:
                return 'grey';
        }
    };

    const trigger = (
        <a style={{ cursor: 'pointer' }}>
            <span style={{ position: 'relative', display: 'inline-block' }}>
                <Icon name="bell" />
                {unreadCount > 0 && (
                    <Label circular color="red" floating size="mini">
                        {unreadCount}
                    </Label>
                )}
            </span>
            {' '}Notifications
        </a>
    );

    const content = (
        <div className="notification-popup">
            <div className="notification-header">
                <h4>Notifications</h4>
                {unreadCount > 0 && (
                    <Label size="tiny" color="red">
                        {unreadCount} new
                    </Label>
                )}
            </div>
            
            <List divided relaxed className="notification-list">
                {notifications.length === 0 ? (
                    <List.Item>
                        <div className="no-notifications">
                            <Icon name="inbox" size="large" />
                            <p>No notifications</p>
                        </div>
                    </List.Item>
                ) : (
                    notifications.slice(0, 10).map((notification) => (
                        <List.Item 
                            key={notification.id}
                            className={notification.notificationStatus === 'Pending' ? 'unread' : 'read'}
                            style={resolveNotificationActionUrl(notification.actionType, notification.actionPayload, notification.type) ? { cursor: 'pointer' } : undefined}
                            onClick={() => {
                                if (onMarkAsRead) onMarkAsRead(notification.id);
                                const url = resolveNotificationActionUrl(
                                    notification.actionType,
                                    notification.actionPayload,
                                    notification.type);
                                if (url) navigate(url);
                            }}
                        >
                            <List.Icon 
                                name={getNotificationIcon(notification.type)} 
                                color={getNotificationColor(notification.type)}
                                verticalAlign="middle"
                            />
                            <List.Content>
                                <List.Header>{notification.type.replace(/_/g, ' ')}</List.Header>
                                <List.Description>
                                    {notification.message}
                                </List.Description>
                                <div className="notification-time">
                                    {formatDistance(new Date(notification.timestamp), new Date(), { addSuffix: true })}
                                </div>
                            </List.Content>
                        </List.Item>
                    ))
                )}
            </List>
            
            {notifications.length > 10 && (
                <div className="notification-footer">
                    <a href="/notifications">View all notifications</a>
                </div>
            )}
        </div>
    );

    return (
        <Popup
            trigger={trigger}
            content={content}
            on="click"
            position="bottom right"
            wide="very"
            hoverable
        />
    );
};
