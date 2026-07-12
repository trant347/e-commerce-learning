import * as React from 'react';
import { useContext, useEffect, useState } from 'react';
import { useParams, useNavigate, Navigate } from 'react-router-dom';

import UserContext from '../context/userContext';
import { Booking, BookingService, openAuthenticatedFile } from '../api/bookingServices';
import Dialog from '../components/dialog/dialog';

import '../components/new-task-master/new-task-master.css';

interface CardFormState {
    cardNumber: string;
    expiryDate: string;
    cvv: string;
    ownerName: string;
}

export default function PayBooking() {
    const { username } = useContext(UserContext);
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();

    const [booking, setBooking] = useState<Booking | null>(null);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState<string | null>(null);

    const [card, setCard] = useState<CardFormState>({
        cardNumber: '',
        expiryDate: '',
        cvv: '',
        ownerName: '',
    });
    const [submitting, setSubmitting] = useState(false);
    const [feedback, setFeedback] = useState<{ type: 'success' | 'error'; message: string } | null>(null);
    const [successOpen, setSuccessOpen] = useState(false);

    useEffect(() => {
        if (!id) return;
        BookingService.get(id)
            .then(b => {
                setBooking(b);
                setLoading(false);
            })
            .catch(() => {
                setLoadError('Failed to load this booking.');
                setLoading(false);
            });
    }, [id]);

    if (!username) {
        return <Navigate to="/signin" replace />;
    }

    if (loading) {
        return <div className="new-taskmaster-page"><p>Loading...</p></div>;
    }

    if (loadError || !booking) {
        return (
            <div className="new-taskmaster-page">
                <div className="form-feedback error">{loadError ?? 'Booking not found.'}</div>
            </div>
        );
    }

    if (booking.requesterUsername.toLowerCase() !== username.toLowerCase()) {
        return <Navigate to="/" replace />;
    }

    if (booking.status !== 'IMPLEMENTED') {
        return (
            <div className="new-taskmaster-page">
                <h1><i className="credit card icon" /> Pay Invoice</h1>
                <div className="form-feedback error" style={{ padding: '1.5rem' }}>
                    {booking.status === 'COMPLETED'
                        ? 'This booking has already been paid.'
                        : `This booking is ${booking.status} and has no payment due right now.`}
                </div>
                <button className="submit-btn" style={{ marginTop: '1rem' }} onClick={() => navigate('/')}>
                    Back to Home
                </button>
            </div>
        );
    }

    if (booking.paymentPending) {
        return (
            <div className="new-taskmaster-page">
                <h1><i className="credit card icon" /> Pay Invoice</h1>
                <div className="form-feedback error" style={{ padding: '1.5rem' }}>
                    Your payment is being processed. We could not immediately confirm the outcome of a
                    previous payment attempt for this booking, so please do not submit another payment.
                    This is usually resolved automatically within about a minute — refresh this page
                    shortly to see the final status.
                </div>
                <button className="submit-btn" style={{ marginTop: '1rem' }} onClick={() => navigate('/')}>
                    Back to Home
                </button>
            </div>
        );
    }

    const amountDue = booking.invoiceAmount ?? 0;

    const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        setCard({ ...card, [e.target.name]: e.target.value });
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setFeedback(null);

        if (!card.cardNumber.trim() || !card.expiryDate.trim() || !card.cvv.trim() || !card.ownerName.trim()) {
            setFeedback({ type: 'error', message: 'Please fill in all card details.' });
            return;
        }

        setSubmitting(true);
        try {
            await BookingService.pay(booking.id, {
                cardNumber: card.cardNumber,
                expiryDate: card.expiryDate,
                cvv: card.cvv,
                ownerName: card.ownerName,
            });
            setSuccessOpen(true);
        } catch (err: any) {
            const msg = err?.response?.data?.error || err?.response?.data?.message || 'Payment failed. Please try again.';
            setFeedback({ type: 'error', message: msg });
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="new-taskmaster-page">
            <h1><i className="credit card icon" /> Pay Invoice</h1>
            <p style={{ color: '#666', marginBottom: '1rem' }}>
                <strong>{booking.taskMasterUsername}</strong> has submitted proof of the completed job.
            </p>

            {booking.proofFileUrl && (
                <div style={{ marginBottom: '1.5rem' }}>
                    <a
                        href={booking.proofFileUrl}
                        onClick={(e) => {
                            e.preventDefault();
                            openAuthenticatedFile(booking.proofFileUrl!);
                        }}
                    >
                        <i className="paperclip icon" /> View proof of job
                    </a>
                </div>
            )}

            <div className="form-feedback success" style={{ fontSize: '1.1rem', marginBottom: '1.5rem' }}>
                Amount due: <strong>${amountDue.toFixed(2)}</strong>
            </div>

            <form onSubmit={handleSubmit}>
                <div className="user-input-row">
                    <label>Cardholder Name *</label>
                    <input type="text" name="ownerName" value={card.ownerName} onChange={handleChange} required placeholder="e.g. John Smith" />
                </div>

                <div className="user-input-row">
                    <label>Card Number *</label>
                    <input type="text" name="cardNumber" value={card.cardNumber} onChange={handleChange} required placeholder="e.g. 4111 1111 1111 1111" />
                </div>

                <div className="user-input-row">
                    <label>Expiry Date *</label>
                    <input type="text" name="expiryDate" value={card.expiryDate} onChange={handleChange} required placeholder="MM/YY" />
                </div>

                <div className="user-input-row">
                    <label>CVV *</label>
                    <input type="text" name="cvv" value={card.cvv} onChange={handleChange} required placeholder="e.g. 123" />
                </div>

                {feedback && (
                    <div className={`form-feedback ${feedback.type}`}>{feedback.message}</div>
                )}

                <button type="submit" className="submit-btn" disabled={submitting}>
                    {submitting ? 'Processing...' : `Pay $${amountDue.toFixed(2)}`}
                </button>
            </form>

            {successOpen && (
                <Dialog
                    title="Payment successful"
                    message="Your payment was processed and the booking is now complete. Thank you!"
                    onClose={() => navigate('/')}
                />
            )}
        </div>
    );
}
