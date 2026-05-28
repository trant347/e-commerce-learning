import * as React from "react";
import { Calendar, Event } from "../components/calendar/calendar";
import { Button } from "semantic-ui-react";
import { BookingService } from "../api/bookingServices";
import { TaskMasterServices } from "../api/taskMasterServices";

export default function CalendarPage(props: {events: Event[], match?: any}) {

    let [selectedDate, setSelected] = React.useState<Date | null>(null);
    let [duration, setDuration] = React.useState<number>(1);
    let [taskMasterName, setTaskMasterName] = React.useState<string | null>(null);
    const taskMasterId: string | undefined = props.match?.params?.id;

    React.useEffect(() => {
        if (taskMasterId) {
            TaskMasterServices.getTaskMasterById(taskMasterId)
                .then(tm => setTaskMasterName(tm.name))
                .catch(() => setTaskMasterName(null));
        }
    }, [taskMasterId]);

    let onChange = (selected: Date) => {
        setSelected(selected);
    };

    const onSubmit = () => {
        if (!selectedDate || !taskMasterId) return;
        // Hour-align in UTC: backend rejects non-hour-aligned or past slots.
        const slot = new Date(Date.UTC(
            selectedDate.getUTCFullYear(), selectedDate.getUTCMonth(), selectedDate.getUTCDate(),
            selectedDate.getUTCHours(), 0, 0, 0));
        BookingService.create(taskMasterId, slot, duration)
            .then(() => alert("Booking request sent!"))
            .catch(err => alert(err?.response?.data?.error ?? "Failed to send booking request"));
    };

    return (
        <div style={{ padding: '20px' }}>
            {taskMasterName && (
                <h2 style={{ marginBottom: '20px' }}>
                    Book an appointment with <strong>{taskMasterName}</strong>
                </h2>
            )}
            <Calendar events={props.events} onChange={onChange}/>
            <div style={{ margin: '12px 0' }}>
                <label htmlFor="duration-hours">Duration (hours):&nbsp;</label>
                <input
                    id="duration-hours"
                    type="number"
                    min={1}
                    max={24}
                    step={1}
                    value={duration}
                    onChange={e => setDuration(Math.max(1, Math.min(24, parseInt(e.target.value, 10) || 1)))}
                    style={{ width: 70 }}
                />
            </div>
            <Button onClick={onSubmit} disabled={!taskMasterId || !selectedDate}>Submit</Button>
        </div>
    );
};
