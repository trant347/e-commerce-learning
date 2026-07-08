import * as React from 'react';
import { useContext, useEffect, useState } from 'react';
import { useParams, useNavigate, Navigate } from 'react-router-dom';
import axios from 'axios';

import UserContext from '../context/userContext';
import { Booking, BookingService } from '../api/bookingServices';
import Dialog from '../components/dialog/dialog';

import '../components/new-task-master/new-task-master.css';

export default function SubmitProof() {
    const { username } = useContext(UserContext);
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();

    const [booking, setBooking] = useState<Booking | null>(null);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState<string | null>(null);

    const [file, setFile] = useState<File | null>(null);
    const [invoiceAmount, setInvoiceAmount] = useState<string>('');
    const [submitting, setSubmitting] = useState(false);
    const [feedback, setFeedback] = useState<{ type: 'success' | 'error'; message: string } | null>(null);
    const [successOpen, setSuccessOpen] = useState(false);

    useEffect(() => {
        if (!id) return;
        BookingService.get(id)
            .then(b => {
                setBooking(b);
                setInvoiceAmount(b.offeredTotalAmount != null ? b.offeredTotalAmount.toFixed(2) : '');
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

    if (booking.taskMasterUsername.toLowerCase() !== username.toLowerCase()) {
        return <Navigate to="/" replace />;
    }

    if (booking.status !== 'ACCEPTED') {
        return (
            <div className="new-taskmaster-page">
                <h1><i className="file alternate icon" /> Submit Proof of Job</h1>
                <div className="form-feedback error" style={{ padding: '1.5rem' }}>
                    This booking is <strong>{booking.status}</strong> and cannot be invoiced right now.
                </div>
                <button className="submit-btn" style={{ marginTop: '1rem' }} onClick={() => navigate('/my-calendar')}>
                    Back to My Calendar
                </button>
            </div>
        );
    }

    const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        setFile(e.target.files && e.target.files.length > 0 ? e.target.files[0] : null);
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setFeedback(null);

        if (!file) {
            setFeedback({ type: 'error', message: 'Please select a proof file (image or document) to upload.' });
            return;
        }
        const amount = parseFloat(invoiceAmount);
        if (!invoiceAmount.trim() || isNaN(amount) || amount <= 0) {
            setFeedback({ type: 'error', message: 'Please enter a valid invoice amount greater than 0.' });
            return;
        }

        setSubmitting(true);
        try {
            const token = localStorage.getItem('token');
            const formData = new FormData();
            formData.append('file', file);
            const uploadRes = await axios.post('/products/image', formData, {
                headers: {
                    ...(token ? { Authorization: `Bearer ${token}` } : {}),
                    'Content-Type': 'multipart/form-data',
                },
            });
            const filename: string = uploadRes.data;
            const proofFileUrl = `/products/image/${filename}`;

            await BookingService.submitProof(booking.id, proofFileUrl, amount);
            setSuccessOpen(true);
        } catch (err: any) {
            const msg = err?.response?.data?.error || err?.response?.data?.message || 'Failed to submit proof of job. Please try again.';
            setFeedback({ type: 'error', message: msg });
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="new-taskmaster-page">
            <h1><i className="file alternate icon" /> Submit Proof of Job</h1>
            <p style={{ color: '#666', marginBottom: '1.5rem' }}>
                Upload proof that you completed the job for <strong>{booking.requesterUsername}</strong>,
                then send the invoice. The booking will move to <strong>Implemented</strong> and the requester
                will be asked to pay.
            </p>

            <form onSubmit={handleSubmit}>
                <div className="user-input-row">
                    <label>Proof (image or file) *</label>
                    <input type="file" accept="image/*,.pdf,.doc,.docx" onChange={handleFileChange} required />
                </div>

                <div className="user-input-row">
                    <label>Invoice Amount (USD) *</label>
                    <input
                        type="number"
                        min={0.01}
                        step="0.01"
                        value={invoiceAmount}
                        onChange={e => setInvoiceAmount(e.target.value)}
                        required
                        placeholder="e.g. 45.00"
                    />
                </div>

                {feedback && (
                    <div className={`form-feedback ${feedback.type}`}>{feedback.message}</div>
                )}

                <button type="submit" className="submit-btn" disabled={submitting}>
                    {submitting ? 'Submitting...' : 'Submit Proof & Send Invoice'}
                </button>
            </form>

            {successOpen && (
                <Dialog
                    title="Invoice sent"
                    message="Proof of job and invoice were submitted. The requester has been notified to pay."
                    onClose={() => navigate('/my-calendar')}
                />
            )}
        </div>
    );
}
