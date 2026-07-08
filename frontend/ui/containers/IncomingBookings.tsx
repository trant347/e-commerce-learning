import { useContext, useEffect, useState } from 'react';
import { useNavigate, Navigate } from 'react-router-dom';

import UserContext from '../context/userContext';
import { Booking, BookingService, BookingStatus } from '../api/bookingServices';

import '../components/new-task-master/new-task-master.css';
import '../components/admin-applications/admin-applications.css';

type FilterStatus = 'ALL' | BookingStatus;

const statusColor: Record<string, string> = {
    PENDING: '#f0a500',
    ACCEPTED: '#21ba45',
    DECLINED: '#db2828',
    CANCELLED: '#767676',
    IMPLEMENTED: '#2185d0',
    COMPLETED: '#00b5ad',
};

function formatSlot(iso: string, durationHours: number): string {
    const start = new Date(iso);
    const end = new Date(start.getTime() + durationHours * 60 * 60 * 1000);
    const sameDay = start.toDateString() === end.toDateString();
    const startStr = start.toLocaleString();
    const endStr = sameDay
        ? end.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
        : end.toLocaleString();
    return `${startStr} → ${endStr}`;
}

export default function IncomingBookings() {
    const { username } = useContext(UserContext);
    const navigate = useNavigate();

    const [bookings, setBookings] = useState<Booking[]>([]);
    const [filter, setFilter] = useState<FilterStatus>('PENDING');
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [responses, setResponses] = useState<Record<string, string>>({});
    const [pendingId, setPendingId] = useState<string | null>(null);

    if (!username) {
        return <Navigate to="/" replace />;
    }

    const reload = () => {
        setLoading(true);
        setError(null);
        const statusParam = filter === 'ALL' ? undefined : filter;
        BookingService.listIncoming(statusParam)
            .then(data => {
                setBookings(data);
                setLoading(false);
            })
            .catch(() => {
                setError('Failed to load booking requests.');
                setLoading(false);
            });
    };

    useEffect(reload, [filter]);

    const onResponseChange = (id: string, value: string) => {
        setResponses(prev => ({ ...prev, [id]: value }));
    };

    const respond = (b: Booking, action: 'accept' | 'decline') => {
        if (pendingId) return;
        setPendingId(b.id);
        const msg = (responses[b.id] ?? '').trim();
        const call = action === 'accept'
            ? BookingService.accept(b.id, msg.length > 0 ? msg : undefined)
            : BookingService.decline(b.id, msg.length > 0 ? msg : undefined);
        call
            .then(updated => {
                setBookings(prev => prev.map(x => x.id === updated.id ? updated : x));
                setResponses(prev => {
                    const next = { ...prev };
                    delete next[b.id];
                    return next;
                });
            })
            .catch(err => {
                alert(err?.response?.data?.error ?? `Failed to ${action} booking`);
            })
            .finally(() => setPendingId(null));
    };

    return (
        <div className="new-taskmaster-page">
            <h1><i className="calendar alternate icon" /> Booking Requests</h1>

            <div className="filter-tabs">
                {(['PENDING', 'ALL', 'ACCEPTED', 'IMPLEMENTED', 'COMPLETED', 'DECLINED', 'CANCELLED'] as FilterStatus[]).map(s => (
                    <button
                        key={s}
                        className={`filter-tab${filter === s ? ' active' : ''}`}
                        onClick={() => setFilter(s)}
                    >
                        {s === 'ALL' ? 'All' : s.charAt(0) + s.slice(1).toLowerCase()}
                    </button>
                ))}
            </div>

            {loading && <p className="loading-text">Loading...</p>}
            {error && <div className="form-feedback error">{error}</div>}

            {!loading && !error && bookings.length === 0 && (
                <div className="empty-state">
                    <i className="inbox icon" style={{ fontSize: '2rem', color: '#bbb' }} />
                    <p>No {filter !== 'ALL' ? filter.toLowerCase() : ''} booking requests.</p>
                </div>
            )}

            {!loading && !error && bookings.length > 0 && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                    {bookings.map(b => {
                        const isPending = b.status === 'PENDING';
                        const isBusy = pendingId === b.id;
                        return (
                            <div
                                key={b.id}
                                style={{
                                    border: '1px solid #e1dfdd',
                                    borderRadius: '6px',
                                    padding: '16px',
                                    background: '#fff',
                                    boxShadow: '0 1px 2px rgba(0,0,0,0.04)',
                                }}
                            >
                                <div style={{
                                    display: 'flex',
                                    justifyContent: 'space-between',
                                    alignItems: 'center',
                                    marginBottom: '8px',
                                    flexWrap: 'wrap',
                                    gap: '8px',
                                }}>
                                    <div style={{ fontSize: '1rem' }}>
                                        <strong>{b.requesterUsername}</strong>
                                        <span style={{ color: '#605e5c', marginLeft: '8px' }}>
                                            requested {formatSlot(b.slotStart, b.durationHours)}
                                        </span>
                                    </div>
                                    <span
                                        className="status-badge-sm"
                                        style={{ backgroundColor: statusColor[b.status] || '#999' }}
                                    >
                                        {b.status}
                                    </span>
                                </div>

                                <div style={{ fontSize: '0.85rem', color: '#605e5c', marginBottom: '8px' }}>
                                    Sent {new Date(b.createdAt).toLocaleString()} ·
                                    {' '}<strong>{b.durationHours}</strong> {b.durationHours === 1 ? 'hour' : 'hours'}
                                    {b.offeredRatePerHour != null && (
                                        <>
                                            {' · '}Offered <strong>${b.offeredRatePerHour.toFixed(2)}/hr</strong>
                                            {b.offeredTotalAmount != null && (
                                                <> (total <strong>${b.offeredTotalAmount.toFixed(2)}</strong>)</>
                                            )}
                                        </>
                                    )}
                                </div>

                                <div style={{
                                    background: '#f3f2f1',
                                    padding: '10px 12px',
                                    borderRadius: '4px',
                                    fontSize: '0.92rem',
                                    marginBottom: '12px',
                                    whiteSpace: 'pre-wrap',
                                    color: b.requestMessage ? '#252423' : '#a19f9d',
                                }}>
                                    {b.requestMessage ? b.requestMessage : '(no message provided)'}
                                </div>

                                {!isPending && b.responseMessage && (
                                    <div style={{ marginBottom: '8px', fontSize: '0.9rem' }}>
                                        <div style={{ fontWeight: 600, marginBottom: '4px' }}>Your reply:</div>
                                        <div style={{
                                            background: '#fff4ce',
                                            padding: '8px 10px',
                                            borderRadius: '4px',
                                            whiteSpace: 'pre-wrap',
                                        }}>{b.responseMessage}</div>
                                    </div>
                                )}

                                {isPending && (
                                    <>
                                        <textarea
                                            value={responses[b.id] ?? ''}
                                            onChange={e => onResponseChange(b.id, e.target.value)}
                                            placeholder="Private reply to the requester (optional)"
                                            rows={3}
                                            style={{
                                                width: '100%',
                                                padding: '8px',
                                                border: '1px solid #d2d0ce',
                                                borderRadius: '4px',
                                                fontFamily: 'inherit',
                                                fontSize: '0.9rem',
                                                resize: 'vertical',
                                                boxSizing: 'border-box',
                                                marginBottom: '10px',
                                            }}
                                        />
                                        <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end' }}>
                                            <button
                                                className="review-btn"
                                                style={{ background: '#db2828' }}
                                                disabled={isBusy}
                                                onClick={() => respond(b, 'decline')}
                                            >
                                                {isBusy ? '...' : 'Decline'}
                                            </button>
                                            <button
                                                className="review-btn"
                                                style={{ background: '#21ba45' }}
                                                disabled={isBusy}
                                                onClick={() => respond(b, 'accept')}
                                            >
                                                {isBusy ? '...' : 'Accept'}
                                            </button>
                                        </div>
                                    </>
                                )}
                            </div>
                        );
                    })}
                </div>
            )}
        </div>
    );
}
