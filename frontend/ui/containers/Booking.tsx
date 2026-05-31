import * as React from "react";
import { Calendar, Event } from "../components/calendar/calendar";
import { Button } from "semantic-ui-react";
import { BookingService } from "../api/bookingServices";
import { TaskMasterServices } from "../api/taskMasterServices";

export default function CalendarPage(props: {events: Event[], match?: any}) {

    let [selectedDate, setSelected] = React.useState<Date | null>(null);
    let [duration, setDuration] = React.useState<number>(1);
    let [taskMasterName, setTaskMasterName] = React.useState<string | null>(null);
    let [description, setDescription] = React.useState<string>("");
    // Tracks an in-flight POST so a fast double-click on Submit cannot fire two requests.
    // Without this guard, two near-simultaneous bookings race the backend's overlap check
    // and one of them (whichever loses the race) comes back as 409. The backend has a DB-level
    // unique index as a hard guarantee, but disabling the button is the cheap UX fix here.
    let [submitting, setSubmitting] = React.useState<boolean>(false);
    const taskMasterId: string | undefined = props.match?.params?.id;

    React.useEffect(() => {
        if (taskMasterId) {
            TaskMasterServices.getTaskMasterById(taskMasterId)
                .then(tm => setTaskMasterName(tm.name))
                .catch(() => setTaskMasterName(null));
        }
    }, [taskMasterId]);

    let onChange = (start: Date, hours: number) => {
        setSelected(start);
        setDuration(hours);
    };

    const onSubmit = () => {
        if (!selectedDate || !taskMasterId || submitting) return;
        // Hour-align in UTC: backend rejects non-hour-aligned or past slots.
        const slot = new Date(Date.UTC(
            selectedDate.getUTCFullYear(), selectedDate.getUTCMonth(), selectedDate.getUTCDate(),
            selectedDate.getUTCHours(), 0, 0, 0));
        setSubmitting(true);
        const trimmed = description.trim();
        BookingService.create(taskMasterId, slot, duration, trimmed.length > 0 ? trimmed : undefined)
            .then(() => {
                alert("Booking request sent!");
                setDescription("");
            })
            .catch(err => alert(err?.response?.data?.error ?? "Failed to send booking request"))
            .finally(() => setSubmitting(false));
    };

    return (
        <div style={{ padding: '20px' }}>
            {taskMasterName && (
                <h2 style={{ marginBottom: '20px' }}>
                    Book an appointment with <strong>{taskMasterName}</strong>
                </h2>
            )}
            <div style={{ display: 'flex', gap: '20px', alignItems: 'flex-start', flexWrap: 'wrap' }}>
                <div style={{ flex: '1 1 600px', minWidth: 0 }}>
                    <Calendar events={props.events} onChange={onChange}/>
                </div>
                <div style={{ flex: '0 1 320px', minWidth: '260px', display: 'flex', flexDirection: 'column', gap: '12px' }}>
                    <div>
                        <label htmlFor="booking-description" style={{ display: 'block', fontWeight: 600, marginBottom: '6px', fontSize: '0.95rem' }}>
                            Task description
                        </label>
                        <textarea
                            id="booking-description"
                            value={description}
                            onChange={e => setDescription(e.target.value)}
                            placeholder="Describe what you'd like help with (optional)"
                            rows={6}
                            style={{
                                width: '100%',
                                padding: '8px',
                                border: '1px solid #d2d0ce',
                                borderRadius: '4px',
                                fontFamily: 'inherit',
                                fontSize: '0.9rem',
                                resize: 'vertical',
                                boxSizing: 'border-box'
                            }}
                        />
                    </div>
                    <div style={{ fontSize: '0.95rem' }}>
                        {selectedDate ? (
                            <span>
                                Selected: <strong>{selectedDate.toLocaleString()}</strong>
                                {' · '}
                                <strong>{duration} {duration === 1 ? 'hour' : 'hours'}</strong>
                            </span>
                        ) : (
                            <span style={{ color: '#605e5c' }}>
                                Click a time slot, then click another to set the end — or click and drag to select a range.
                            </span>
                        )}
                    </div>
                    <Button onClick={onSubmit} disabled={!taskMasterId || !selectedDate || submitting} loading={submitting}>Submit</Button>
                </div>
            </div>
        </div>
    );
};
