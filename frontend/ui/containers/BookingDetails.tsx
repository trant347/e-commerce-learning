import * as React from 'react';
import { Navigate, useNavigate, useParams } from 'react-router-dom';

import { Booking, BookingService } from '../api/bookingServices';
import UserContext from '../context/userContext';
import { BookingDetailsModal } from './MyCalendar';

export default function BookingDetails() {
    const { username } = React.useContext(UserContext);
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();
    const [booking, setBooking] = React.useState<Booking | null>(null);
    const [error, setError] = React.useState<string | null>(null);

    React.useEffect(() => {
        if (!username || !id) return;

        BookingService.get(id)
            .then(setBooking)
            .catch(() => setError('Unable to load the booking details.'));
    }, [id, username]);

    if (!username) return <Navigate to="/signin" replace />;
    if (!id) return <Navigate to="/" replace />;

    return (
        <div style={{ padding: '20px' }}>
            {error && <div className="form-feedback error">{error}</div>}
            {!error && !booking && <p>Loading booking details...</p>}
            {booking && (
                <BookingDetailsModal
                    booking={booking}
                    viewer="requester"
                    onUpdated={setBooking}
                    onClose={() => navigate('/')}
                />
            )}
        </div>
    );
}
