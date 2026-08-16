import * as React from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import styled from 'styled-components';

import UserContext from '../context/userContext';
import { Calendar, Event } from '../components/calendar/calendar';
import { Booking, BookingService, openAuthenticatedFile } from '../api/bookingServices';
import { TaskMasterServices } from '../api/taskMasterServices';

const BOOKING_EVENT_STYLES: Record<string, React.CSSProperties> = {
    WAITING_FOR_FUNDING: {
        backgroundColor: '#fff4ce',
        borderLeftColor: '#f0a500',
        color: '#5c4300',
    },
    READY_TO_START: {
        backgroundColor: '#deecf9',
        borderLeftColor: '#0078d4',
        color: '#004578',
    },
    IN_PROGRESS: {
        backgroundColor: '#e8e1f8',
        borderLeftColor: '#6b4eff',
        color: '#3b2470',
    },
    IMPLEMENTED: {
        backgroundColor: '#fde7d9',
        borderLeftColor: '#d83b01',
        color: '#7a2100',
    },
    COMPLETED: {
        backgroundColor: '#dff6dd',
        borderLeftColor: '#107c10',
        color: '#0b5a0b',
    },
};

function bookingEventState(b: Booking): string {
    if (b.status === 'ACCEPTED') {
        return b.escrowStatus === 'FUNDED'
            ? 'READY_TO_START'
            : 'WAITING_FOR_FUNDING';
    }
    return b.status;
}

export function bookingToEvent(b: Booking): Event {
    const start = new Date(b.slotStart);
    const end = new Date(start.getTime() + b.durationHours * 60 * 60 * 1000);
    return {
        id: b.id,
        title: b.requesterUsername,
        start,
        end,
        data: b,
        style: BOOKING_EVENT_STYLES[bookingEventState(b)],
    };
}

export function getBookingStatusMessage(booking: Booking): string | null {
    if (booking.status === 'ACCEPTED' && booking.escrowStatus !== 'FUNDED') {
        return 'Waiting for the requester to fund escrow. Start Work will become available once payment is safely held.';
    }
    if (booking.status === 'ACCEPTED' && booking.escrowStatus === 'FUNDED') {
        return 'Payment is funded and safely held in escrow. You can start work when ready.';
    }
    if (booking.status === 'IN_PROGRESS') {
        return 'Work is in progress. Submit proof after the task is finished to request escrow release.';
    }
    if (booking.status === 'IMPLEMENTED') {
        return 'Proof was submitted. Escrow release is being processed.';
    }
    if (booking.status === 'COMPLETED') {
        return 'Escrow was released and this booking is complete.';
    }
    return null;
}

export default function MyCalendar() {
    const { username } = React.useContext(UserContext);

    const [allowed, setAllowed] = React.useState<boolean | null>(null);
    const [events, setEvents] = React.useState<Event[]>([]);
    const [error, setError] = React.useState<string | null>(null);
    const [selected, setSelected] = React.useState<Booking | null>(null);

    React.useEffect(() => {
        if (!username) { setAllowed(false); return; }
        TaskMasterServices.getMyTaskMaster()
            .then(tm => setAllowed(tm != null))
            .catch(() => setAllowed(false));
    }, [username]);

    React.useEffect(() => {
        if (allowed !== true) return;
        BookingService.listIncoming()
            .then(bookings => {
                const visible = bookings.filter(b =>
                    b.status === 'ACCEPTED' || b.status === 'IN_PROGRESS'
                    || b.status === 'IMPLEMENTED' || b.status === 'COMPLETED');
                setEvents(visible.map(bookingToEvent));
            })
            .catch(() => setError('Failed to load your calendar.'));
    }, [allowed]);

    if (!username) return <Navigate to="/" replace />;
    if (allowed === false) return <Navigate to="/" replace />;
    if (allowed === null) return <p style={{ padding: '20px' }}>Loading...</p>;

    const updateBooking = (updated: Booking) => {
        setSelected(updated);
        setEvents(currentEvents => currentEvents.map(event =>
            event.id === updated.id ? bookingToEvent(updated) : event));
    };

    return (
        <div style={{ padding: '20px' }}>
            <h2 style={{ marginBottom: '20px' }}>
                <i className="calendar outline icon" /> My Calendar
            </h2>
            {error && <div style={{ color: '#db2828', marginBottom: '12px' }}>{error}</div>}
            <Calendar
                events={events}
                onChange={() => { /* read-only view: ignore selections */ }}
                onEventClick={(e) => setSelected(e.data as Booking)}
            />

            {selected && (
                <BookingDetailsModal
                    booking={selected}
                    onUpdated={updateBooking}
                    onClose={() => setSelected(null)}
                />
            )}
        </div>
    );
}

function formatSlot(b: Booking): string {
    const start = new Date(b.slotStart);
    const end = new Date(start.getTime() + b.durationHours * 60 * 60 * 1000);
    const sameDay = start.toDateString() === end.toDateString();
    const endStr = sameDay
        ? end.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
        : end.toLocaleString();
    return `${start.toLocaleString()} → ${endStr}`;
}

export function BookingDetailsModal({
    booking,
    onUpdated,
    onClose,
    viewer = 'taskMaster',
}: {
    booking: Booking;
    onUpdated: (booking: Booking) => void;
    onClose: () => void;
    viewer?: 'taskMaster' | 'requester';
}) {
    const navigate = useNavigate();
    const [current, setCurrent] = React.useState(booking);
    const [starting, setStarting] = React.useState(false);
    const [startError, setStartError] = React.useState<string | null>(null);

    const startWork = () => {
        setStarting(true);
        setStartError(null);
        BookingService.startWork(current.id)
            .then(updated => {
                setCurrent(updated);
                onUpdated(updated);
            })
            .catch(err => setStartError(err?.response?.data?.error ?? 'Failed to start work.'))
            .finally(() => setStarting(false));
    };

    return (
        <Overlay onClick={onClose}>
            <Box onClick={(e) => e.stopPropagation()}>
                <Title>Booking details</Title>

                <Field>
                    <Label>{viewer === 'requester' ? 'Task master' : 'Requested by'}</Label>
                    <Value>
                        <strong>
                            {viewer === 'requester'
                                ? booking.taskMasterUsername
                                : booking.requesterUsername}
                        </strong>
                    </Value>
                </Field>

                <Field>
                    <Label>Status</Label>
                    <Value><strong>{current.status}</strong></Value>
                </Field>

                {getBookingStatusMessage(current) && (
                    <StatusNotice>{getBookingStatusMessage(current)}</StatusNotice>
                )}

                <Field>
                    <Label>When</Label>
                    <Value>
                        {formatSlot(booking)}
                        {' · '}
                        <strong>{booking.durationHours} {booking.durationHours === 1 ? 'hour' : 'hours'}</strong>
                    </Value>
                </Field>

                {booking.offeredRatePerHour != null && (
                    <Field>
                        <Label>Offered rate</Label>
                        <Value>
                            <strong>${booking.offeredRatePerHour.toFixed(2)}/hr</strong>
                            {booking.offeredTotalAmount != null && (
                                <> {' · '}total <strong>${booking.offeredTotalAmount.toFixed(2)}</strong></>
                            )}
                        </Value>
                    </Field>
                )}

                <Field>
                    <Label>Description / comments</Label>
                    <Message $empty={!booking.requestMessage}>
                        {booking.requestMessage ? booking.requestMessage : '(no message provided)'}
                    </Message>
                </Field>

                {booking.responseMessage && (
                    <Field>
                        <Label>{viewer === 'requester' ? 'Task master reply' : 'Your reply'}</Label>
                        <Message style={{ background: '#fff4ce' }}>{booking.responseMessage}</Message>
                    </Field>
                )}

                {(current.status === 'IMPLEMENTED' || current.status === 'COMPLETED') && (
                    <Field>
                        <Label>Invoice</Label>
                        <Value>
                            {booking.invoiceAmount != null && <>Amount: <strong>${booking.invoiceAmount.toFixed(2)}</strong></>}
                            {booking.proofFileUrl && (
                                <div style={{ marginTop: '6px' }}>
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
                            <div style={{ marginTop: '6px' }}>
                                {current.status === 'COMPLETED'
                                    ? 'Payment received. This booking is complete.'
                                    : 'Awaiting payment from the requester.'}
                            </div>
                        </Value>
                    </Field>
                )}

                <Actions>
                    {startError && <span style={{ color: '#db2828' }}>{startError}</span>}
                    {viewer === 'taskMaster'
                        && current.status === 'ACCEPTED'
                        && current.escrowStatus === 'FUNDED' && (
                        <SecondaryButton onClick={startWork} disabled={starting}>
                            {starting ? 'Starting...' : 'Start Work'}
                        </SecondaryButton>
                    )}
                    {viewer === 'taskMaster' && current.status === 'IN_PROGRESS' && (
                        <SecondaryButton onClick={() => navigate(`/booking/${current.id}/submit-proof`)}>
                            <i className="file alternate icon" /> Submit Proof &amp; Request Release
                        </SecondaryButton>
                    )}
                    <PrimaryButton onClick={onClose}>Close</PrimaryButton>
                </Actions>
            </Box>
        </Overlay>
    );
}

const Overlay = styled.div`
    position: fixed;
    top: 0; left: 0; right: 0; bottom: 0;
    background-color: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
`;

const Box = styled.div`
    background: white;
    border-radius: 8px;
    padding: 1.8em 2em;
    max-width: 480px;
    width: 90%;
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.25);
`;

const Title = styled.h3`
    margin: 0 0 1em;
    font-size: 1.25em;
    color: #333;
`;

const Field = styled.div`
    margin-bottom: 0.9em;
`;

const Label = styled.div`
    font-size: 0.78rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: #605e5c;
    margin-bottom: 4px;
`;

const Value = styled.div`
    font-size: 0.95rem;
    color: #252423;
`;

const Message = styled.div<{ $empty?: boolean }>`
    background: #f3f2f1;
    padding: 10px 12px;
    border-radius: 4px;
    font-size: 0.92rem;
    white-space: pre-wrap;
    color: ${p => p.$empty ? '#a19f9d' : '#252423'};
`;

const StatusNotice = styled.div`
    margin-bottom: 1em;
    padding: 10px 12px;
    border-left: 4px solid #f0a500;
    border-radius: 4px;
    background: #fff8dc;
    color: #5c4300;
    font-size: 0.92rem;
`;

const Actions = styled.div`
    display: flex;
    justify-content: flex-end;
    gap: 0.6em;
    margin-top: 1.4em;
`;

const PrimaryButton = styled.button`
    padding: 0.5em 1.2em;
    border-radius: 4px;
    cursor: pointer;
    font-size: 0.95em;
    background-color: #0275d8;
    color: white;
    border: none;

    &:hover {
        background-color: #025aa5;
    }
`;

const SecondaryButton = styled.button`
    padding: 0.5em 1.2em;
    border-radius: 4px;
    cursor: pointer;
    font-size: 0.95em;
    background-color: #f0f0f0;
    color: #333;
    border: 1px solid #ccc;

    &:hover {
        background-color: #e0e0e0;
    }
`;
