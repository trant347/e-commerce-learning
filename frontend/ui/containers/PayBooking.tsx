import * as React from 'react';
import { useContext, useEffect, useState } from 'react';
import { useParams, useNavigate, Navigate } from 'react-router-dom';

import UserContext from '../context/userContext';
import {
    Booking,
    BookingService,
    PaymentAcceptedResponse,
    PaymentOperation,
    PaymentStatusResponse,
} from '../api/bookingServices';
import Dialog from '../components/dialog/dialog';

import '../components/new-task-master/new-task-master.css';

interface CardFormState {
    cardNumber: string;
    expiryDate: string;
    cvv: string;
    ownerName: string;
}

const POLL_DELAYS_MS = [1000, 2000, 4000, 8000, 10000];

function isAcceptedPayment(value: Booking | PaymentAcceptedResponse): value is PaymentAcceptedResponse {
    return 'sagaId' in value;
}

function operationLabel(operation?: PaymentOperation): string {
    switch (operation) {
        case 'RELEASE_ESCROW': return 'escrow release';
        case 'REFUND_ESCROW': return 'escrow refund';
        default: return 'escrow funding';
    }
}

function formatCardNumber(value: string): string {
    return value
        .replace(/\D/g, '')
        .slice(0, 16)
        .replace(/(\d{4})(?=\d)/g, '$1 ');
}

function formatExpiryDate(value: string): string {
    const digits = value.replace(/\D/g, '').slice(0, 4);
    return digits.length > 2
        ? `${digits.slice(0, 2)}/${digits.slice(2)}`
        : digits;
}

export default function PayBooking() {
    const { username } = useContext(UserContext);
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();

    const [booking, setBooking] = useState<Booking | null>(null);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState<string | null>(null);
    const [paymentStatus, setPaymentStatus] = useState<PaymentStatusResponse | null>(null);
    const [pollTimedOut, setPollTimedOut] = useState(false);
    const [card, setCard] = useState<CardFormState>({
        cardNumber: '',
        expiryDate: '',
        cvv: '',
        ownerName: '',
    });
    const [submitting, setSubmitting] = useState(false);
    const [cancelling, setCancelling] = useState(false);
    const [feedback, setFeedback] = useState<string | null>(null);
    const [successOpen, setSuccessOpen] = useState(false);

    const loadBooking = React.useCallback(async () => {
        if (!id) return;
        const loaded = await BookingService.get(id);
        setBooking(loaded);
        if (loaded.latestPaymentSagaId && loaded.latestPaymentStatus) {
            setPaymentStatus({
                sagaId: loaded.latestPaymentSagaId,
                bookingId: loaded.id,
                escrowId: loaded.escrowId ?? '',
                operation: loaded.latestPaymentOperation ?? 'FUND_ESCROW',
                status: loaded.latestPaymentStatus,
                escrowStatus: loaded.escrowStatus,
                failureReason: loaded.latestPaymentFailureReason,
                updatedAt: loaded.createdAt,
            });
        }
    }, [id]);

    useEffect(() => {
        loadBooking()
            .catch(() => setLoadError('Failed to load this booking.'))
            .finally(() => setLoading(false));
    }, [loadBooking]);

    useEffect(() => {
        if (paymentStatus?.status !== 'PENDING') return;

        let cancelled = false;
        let timer: ReturnType<typeof setTimeout> | undefined;
        let attempt = 0;

        const poll = async () => {
            try {
                const current = await BookingService.getPaymentStatus(paymentStatus.sagaId);
                if (cancelled) return;
                setPaymentStatus(current);
                if (current.status !== 'PENDING') {
                    await loadBooking();
                    return;
                }
            } catch {
                if (cancelled) return;
            }

            if (attempt >= POLL_DELAYS_MS.length) {
                setPollTimedOut(true);
                return;
            }
            timer = setTimeout(poll, POLL_DELAYS_MS[attempt++]);
        };

        timer = setTimeout(poll, POLL_DELAYS_MS[attempt++]);
        return () => {
            cancelled = true;
            if (timer) clearTimeout(timer);
        };
    }, [paymentStatus?.sagaId, paymentStatus?.status, loadBooking]);

    if (!username) return <Navigate to="/signin" replace />;
    if (loading) return <div className="new-taskmaster-page"><p>Loading...</p></div>;
    if (loadError || !booking) {
        return <div className="new-taskmaster-page"><div className="form-feedback error">{loadError ?? 'Booking not found.'}</div></div>;
    }
    if (booking.requesterUsername.toLowerCase() !== username.toLowerCase()) {
        return <Navigate to="/" replace />;
    }

    const operation = paymentStatus?.operation ?? booking.latestPaymentOperation;
    const status = paymentStatus?.status ?? booking.latestPaymentStatus;
    const amountDue = booking.agreedAmount ?? booking.invoiceAmount ?? 0;
    const fundingEligible = booking.status === 'ACCEPTED'
        && booking.escrowStatus !== 'FUNDED'
        && status !== 'PENDING';
    const legacyEligible = booking.status === 'IMPLEMENTED' && !booking.escrowId;

    const handleSubmit = async (event: React.FormEvent) => {
        event.preventDefault();
        setFeedback(null);
        setPollTimedOut(false);
        if (!card.cardNumber.trim() || !card.expiryDate.trim() || !card.cvv.trim() || !card.ownerName.trim()) {
            setFeedback('Please fill in all card details.');
            return;
        }

        setSubmitting(true);
        try {
            const result = await BookingService.pay(booking.id, {
                ...card,
                cardNumber: card.cardNumber.replace(/\s/g, ''),
            });
            if (isAcceptedPayment(result)) {
                setPaymentStatus({
                    sagaId: result.sagaId,
                    bookingId: booking.id,
                    escrowId: result.escrowId,
                    operation: 'FUND_ESCROW',
                    status: result.status,
                    escrowStatus: booking.escrowStatus,
                    updatedAt: new Date().toISOString(),
                });
            } else {
                setSuccessOpen(true);
            }
        } catch (err: any) {
            setFeedback(err?.response?.data?.error || err?.response?.data?.message || 'Payment failed. Please try again.');
        } finally {
            setSubmitting(false);
        }
    };

    const requestRefund = async () => {
        setCancelling(true);
        setFeedback(null);
        try {
            const result = await BookingService.cancel(booking.id);
            if (isAcceptedPayment(result)) {
                setPaymentStatus({
                    sagaId: result.sagaId,
                    bookingId: booking.id,
                    escrowId: result.escrowId,
                    operation: 'REFUND_ESCROW',
                    status: result.status,
                    escrowStatus: booking.escrowStatus,
                    updatedAt: new Date().toISOString(),
                });
            } else {
                setBooking(result);
            }
        } catch (err: any) {
            setFeedback(err?.response?.data?.error || 'Refund request failed. Please try again.');
        } finally {
            setCancelling(false);
        }
    };

    if (status === 'PENDING') {
        return (
            <StatusPage>
                <div className="form-feedback success">
                    Your {operationLabel(operation)} request is safely queued and still being processed.
                </div>
                {pollTimedOut && (
                    <div className="form-feedback error">
                        This is taking longer than expected. The request is still durable; reload later to continue checking.
                    </div>
                )}
            </StatusPage>
        );
    }

    if (booking.paymentPending && !booking.latestPaymentSagaId) {
        return (
            <StatusPage>
                <div className="form-feedback success">
                    Your payment is still being reconciled. Please do not submit another payment.
                </div>
            </StatusPage>
        );
    }

    if (booking.escrowStatus === 'FUNDED' && booking.status === 'ACCEPTED') {
        return (
            <StatusPage>
                <div className="form-feedback success">
                    Payment is safely held in escrow. The TaskMaster can now start work.
                </div>
                {feedback && <div className="form-feedback error">{feedback}</div>}
                <button className="submit-btn" onClick={requestRefund} disabled={cancelling}>
                    {cancelling ? 'Requesting refund...' : 'Cancel Booking & Request Refund'}
                </button>
            </StatusPage>
        );
    }

    if (booking.escrowStatus === 'RELEASED' || (status === 'COMPLETED' && operation === 'RELEASE_ESCROW')) {
        return <StatusPage><div className="form-feedback success">Escrow funds were released to the TaskMaster.</div></StatusPage>;
    }

    if (booking.escrowStatus === 'REFUNDED' || (status === 'COMPLETED' && operation === 'REFUND_ESCROW')) {
        return <StatusPage><div className="form-feedback success">Escrow funds were refunded to you.</div></StatusPage>;
    }

    if (!fundingEligible && !legacyEligible) {
        return (
            <StatusPage>
                <div className="form-feedback error">
                    This booking is {booking.status} and has no payment due right now.
                </div>
            </StatusPage>
        );
    }

    return (
        <div className="new-taskmaster-page">
            <h1><i className="credit card icon" /> Fund Booking Escrow</h1>
            <p style={{ color: '#666', marginBottom: '1rem' }}>
                Payment is required after acceptance and before the TaskMaster can start work.
            </p>
            <div className="form-feedback success" style={{ fontSize: '1.1rem', marginBottom: '1.5rem' }}>
                Amount: <strong>${amountDue.toFixed(2)}</strong>
            </div>
            {status === 'FAILED' && (
                <div className="form-feedback error">
                    {paymentStatus?.failureReason ?? booking.latestPaymentFailureReason ?? 'The previous payment attempt failed.'} You may try again.
                </div>
            )}
            <form onSubmit={handleSubmit}>
                <div className="user-input-row">
                    <label htmlFor="payment-owner-name">Cardholder Name *</label>
                    <input id="payment-owner-name" type="text" name="ownerName" value={card.ownerName} onChange={e => setCard({ ...card, ownerName: e.target.value })} required />
                </div>
                <div className="user-input-row">
                    <label htmlFor="payment-card-number">Card Number *</label>
                    <input
                        id="payment-card-number"
                        type="text"
                        inputMode="numeric"
                        autoComplete="cc-number"
                        maxLength={19}
                        name="cardNumber"
                        value={card.cardNumber}
                        placeholder="Test card: 4242 4242 4242 4242"
                        onChange={e => setCard({
                            ...card,
                            cardNumber: formatCardNumber(e.target.value),
                        })}
                        required
                    />
                </div>
                <div className="user-input-row">
                    <label htmlFor="payment-expiry-date">Expiry Date *</label>
                    <input
                        id="payment-expiry-date"
                        type="text"
                        inputMode="numeric"
                        autoComplete="cc-exp"
                        maxLength={5}
                        name="expiryDate"
                        value={card.expiryDate}
                        onChange={e => setCard({
                            ...card,
                            expiryDate: formatExpiryDate(e.target.value),
                        })}
                        required
                        placeholder="MM/YY"
                    />
                </div>
                <div className="user-input-row">
                    <label htmlFor="payment-cvv">CVV *</label>
                    <input id="payment-cvv" type="text" name="cvv" value={card.cvv} onChange={e => setCard({ ...card, cvv: e.target.value })} required />
                </div>
                {feedback && <div className="form-feedback error">{feedback}</div>}
                <button type="submit" className="submit-btn" disabled={submitting}>
                    {submitting ? 'Submitting...' : `Pay $${amountDue.toFixed(2)}`}
                </button>
            </form>
            {successOpen && (
                <Dialog
                    title="Payment successful"
                    message="Your payment was processed successfully."
                    onClose={() => navigate('/')}
                />
            )}
        </div>
    );
}

function StatusPage({ children }: { children: React.ReactNode }) {
    const navigate = useNavigate();
    return (
        <div className="new-taskmaster-page">
            <h1><i className="credit card icon" /> Booking Payment</h1>
            <div style={{ padding: '1.5rem' }}>{children}</div>
            <button className="submit-btn" style={{ marginTop: '1rem' }} onClick={() => navigate('/')}>
                Back to Home
            </button>
        </div>
    );
}
