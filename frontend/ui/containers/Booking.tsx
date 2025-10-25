import * as React from "react";
import { Calendar, Event } from "../components/calendar/calendar";
import { Button } from "semantic-ui-react";
import { BookingService } from "../api/bookingServices";

export default function CalendarPage(props: {events: Event[]}) {

    let [selectedDate, setSelected] = React.useState<Date | null>(null);
    let [bookingService] = React.useState(() => new BookingService());

    let onChange = (selected: Date) => {
        setSelected(selected);
    };


    return (
        <div>
            <Calendar events={props.events} onChange={onChange}/>
            <Button onClick={() => { selectedDate && bookingService.bookService(selectedDate, "Register for Event").then(() => { alert("Booking successful!"); }); }}>Submit</Button>
        </div>
    );
};
