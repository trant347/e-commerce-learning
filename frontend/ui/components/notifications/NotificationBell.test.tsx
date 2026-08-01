import { resolveNotificationActionUrl } from './NotificationBell';

describe('resolveNotificationActionUrl', () => {
    test('accepted booking opens escrow payment', () => {
        expect(resolveNotificationActionUrl(
            'VIEW_PAYMENT_REQUEST',
            { bookingId: 'booking-1', taskMasterId: 'taskmaster-1' },
            'BOOKING_REQUEST_ACCEPTED',
        )).toBe('/booking/booking-1/pay');
    });

    test('legacy accepted notification also opens escrow payment', () => {
        expect(resolveNotificationActionUrl(
            'VIEW_OUTGOING_BOOKING_REQUEST',
            { bookingId: 'booking-1', taskMasterId: 'taskmaster-1' },
            'BOOKING_REQUEST_ACCEPTED',
        )).toBe('/booking/booking-1/pay');
    });
});
