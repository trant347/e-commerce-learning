import * as React from 'react';
import { useContext, useEffect, useState } from 'react';
import { useParams, useNavigate, Navigate } from 'react-router-dom';
import axios from 'axios';

import UserContext from '../context/userContext';
import { Booking, BookingService, PaymentAcceptedResponse } from '../api/bookingServices';
import Dialog from '../components/dialog/dialog';

import '../components/new-task-master/new-task-master.css';

export default function SubmitProof() {
    const { username } = useContext(UserContext);
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();

    // Keep this in sync with product-service's spring.servlet.multipart.max-file-size.
    const MAX_FILE_SIZE_BYTES = 2 * 1024 * 1024;

    const [booking, setBooking] = useState<Booking | null>(null);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState<string | null>(null);

    const [file, setFile] = useState<File | null>(null);
    const [submitting, setSubmitting] = useState(false);
    const [feedback, setFeedback] = useState<{ type: 'success' | 'error'; message: string } | null>(null);
    const [successOpen, setSuccessOpen] = useState(false);
    const [releaseRequest, setReleaseRequest] = useState<PaymentAcceptedResponse | null>(null);

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

    useEffect(() => {
        if (!releaseRequest) return;
        let cancelled = false;
        let attempts = 0;
        let timer: ReturnType<typeof setTimeout>;
        const delays = [1000, 2000, 4000, 8000, 10000];

        const poll = async () => {
            try {
                const status = await BookingService.getPaymentStatus(releaseRequest.sagaId);
                if (cancelled) return;
                if (status.status === 'COMPLETED') {
                    setSuccessOpen(true);
                    return;
                }
                if (status.status === 'FAILED') {
                    setReleaseRequest(null);
                    setFeedback({
                        type: 'error',
                        message: status.failureReason ?? 'Escrow release failed. Please retry.',
                    });
                    return;
                }
            } catch {
                if (cancelled) return;
            }

            if (attempts < delays.length) {
                timer = setTimeout(poll, delays[attempts++]);
            } else {
                setFeedback({
                    type: 'error',
                    message: 'Escrow release is still processing. Reload this booking later to continue checking.',
                });
            }
        };

        timer = setTimeout(poll, delays[attempts++]);
        return () => {
            cancelled = true;
            clearTimeout(timer);
        };
    }, [releaseRequest]);

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

    if (booking.status !== 'IN_PROGRESS') {
        return (
            <div className="new-taskmaster-page">
                <h1><i className="file alternate icon" /> Submit Proof of Job</h1>
                <div className="form-feedback error" style={{ padding: '1.5rem' }}>
                    This booking is <strong>{booking.status}</strong> and cannot submit proof right now.
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
            setFeedback({ type: 'error', message: 'Please select a proof file (png, jpg or pdf) to upload.' });
            return;
        }
        if (file.size > MAX_FILE_SIZE_BYTES) {
            setFeedback({ type: 'error', message: 'File exceeds the maximum allowed size of 2MB.' });
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

            const result = await BookingService.submitProof(booking.id, proofFileUrl);
            setReleaseRequest(result);
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
                Upload proof that you completed the job for <strong>{booking.requesterUsername}</strong>.
                The agreed escrow amount will then be durably queued for release.
            </p>

            <form onSubmit={handleSubmit}>
                <div className="user-input-row">
                    <label>Proof (png, jpg or pdf, max 2MB) *</label>
                    <input type="file" accept=".png,.jpg,.jpeg,.pdf" onChange={handleFileChange} required />
                </div>

                {feedback && (
                    <div className={`form-feedback ${feedback.type}`}>{feedback.message}</div>
                )}
                {releaseRequest && (
                    <div className="form-feedback success">
                        Proof is saved and escrow release is safely queued.
                    </div>
                )}

                <button type="submit" className="submit-btn" disabled={submitting || releaseRequest != null}>
                    {submitting ? 'Submitting...' : 'Submit Proof & Request Release'}
                </button>
            </form>

            {successOpen && (
                <Dialog
                    title="Release requested"
                    message="Proof of job was saved and escrow release is processing."
                    onClose={() => navigate('/my-calendar')}
                />
            )}
        </div>
    );
}
