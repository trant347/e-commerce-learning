import * as React from 'react';
import { useHistory } from 'react-router-dom';
import { Popup, Label, List, Icon } from 'semantic-ui-react';
import { formatDistance } from 'date-fns';
import { Notification } from '../../api/notificationServices';
import './notifications.css';

interface NotificationBellProps {
    notifications: Notification[];
    unreadCount: number;
    onMarkAsRead?: (notificationId: string) => void;
}

export const NotificationBell: React.FC<NotificationBellProps> = ({
    notifications,
    unreadCount,
    onMarkAsRead
}) => {
    const history = useHistory();
    const getNotificationIcon = (type: string) => {
        switch (type.toLowerCase()) {
            case 'booking_confirmed':
                return 'check circle';
            case 'booking_cancelled':
                return 'times circle';
            case 'booking_updated':
                return 'edit';
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
            case 'reminder':
                return 'orange';
            default:
                return 'grey';
        }
    };

    const trigger = (
        <div style={{ position: 'relative', cursor: 'pointer' }}>
            <Icon name="bell" size="large" />
            {unreadCount > 0 && (
                <Label 
                    circular 
                    color="red" 
                    floating
                    size="mini"
                >
                   Notifications {unreadCount}
                </Label>
            )}
        </div>
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
                            className={notification.status === 'Pending' ? 'unread' : 'read'}
                            style={notification.actionUrl ? { cursor: 'pointer' } : undefined}
                            onClick={() => {
                                if (onMarkAsRead) onMarkAsRead(notification.id);
                                if (notification.actionUrl) history.push(notification.actionUrl);
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
