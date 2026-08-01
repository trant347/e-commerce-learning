import { Booking } from '../api/bookingServices';
import { bookingToEvent, getBookingStatusMessage } from './MyCalendar';

function booking(
    status: Booking['status'],
    escrowStatus?: Booking['escrowStatus'],
): Booking {
    return {
        id: 'booking-1',
        taskMasterId: 'taskmaster-1',
        taskMasterUsername: 'worker',
        requesterUsername: 'requester',
        slotStart: '2030-01-15T12:00:00Z',
        durationHours: 1,
        status,
        escrowStatus,
        createdAt: '2030-01-01T12:00:00Z',
    };
}

describe('MyCalendar booking status presentation', () => {
    test('accepted unfunded booking explains that requester funding is required', () => {
        const accepted = booking('ACCEPTED', 'PENDING');

        expect(getBookingStatusMessage(accepted)).toContain(
            'Waiting for the requester to fund escrow',
        );
        expect(bookingToEvent(accepted).style?.backgroundColor).toBe('#fff4ce');
    });

    test('funded and active booking states use distinct colors', () => {
        const funded = bookingToEvent(booking('ACCEPTED', 'FUNDED'));
        const inProgress = bookingToEvent(booking('IN_PROGRESS', 'FUNDED'));
        const completed = bookingToEvent(booking('COMPLETED', 'RELEASED'));

        expect(funded.style?.backgroundColor).toBe('#deecf9');
        expect(inProgress.style?.backgroundColor).toBe('#e8e1f8');
        expect(completed.style?.backgroundColor).toBe('#dff6dd');
    });
});
