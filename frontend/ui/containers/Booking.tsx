import * as React from "react";
import { Calendar, Event } from "../components/calendar/calendar";
import { Button } from "semantic-ui-react";
import { BookingService } from "../api/bookingServices";
import { TaskMasterServices } from "../api/taskMasterServices";

export default function CalendarPage(props: {events: Event[], match?: any}) {

    let [selectedDate, setSelected] = React.useState<Date | null>(null);
    let [duration, setDuration] = React.useState<number>(1);
    let [taskMasterName, setTaskMasterName] = React.useState<string | null>(null);
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
        BookingService.create(taskMasterId, slot, duration)
            .then(() => alert("Booking request sent!"))
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
            <Calendar events={props.events} onChange={onChange}/>
            <div style={{ margin: '12px 0', fontSize: '0.95rem' }}>
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
    );
};
