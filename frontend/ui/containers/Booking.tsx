import * as React from "react";
import { Calendar, Event } from "../components/calendar/calendar";
import { Button } from "semantic-ui-react";
import { BookingService } from "../api/bookingServices";
import { TaskMasterServices } from "../api/taskMasterServices";

export default function CalendarPage(props: {events: Event[], match?: any}) {

    let [selectedDate, setSelected] = React.useState<Date | null>(null);
    let [bookingService] = React.useState(() => new BookingService());
    let [taskMasterName, setTaskMasterName] = React.useState<string | null>(null);

    React.useEffect(() => {
        const id = props.match?.params?.id;
        if (id) {
            TaskMasterServices.getTaskMasterById(id)
                .then(tm => setTaskMasterName(tm.name))
                .catch(() => setTaskMasterName(null));
        }
    }, [props.match?.params?.id]);

    let onChange = (selected: Date) => {
        setSelected(selected);
    };

    return (
        <div style={{ padding: '20px' }}>
            {taskMasterName && (
                <h2 style={{ marginBottom: '20px' }}>
                    Book an appointment with <strong>{taskMasterName}</strong>
                </h2>
            )}
            <Calendar events={props.events} onChange={onChange}/>
            <Button onClick={() => { selectedDate && bookingService.bookService(selectedDate, "Register for Event").then(() => { alert("Booking successful!"); }); }}>Submit</Button>
        </div>
    );
};
