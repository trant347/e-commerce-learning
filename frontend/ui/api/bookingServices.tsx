import axios from 'axios';

export interface IBookingService {
    bookService(date: Date, userId: string): Promise<string>;
}

export class Booking {
    startTime: Date;
    endTime: Date;
    description: string;
}

export class BookingService implements IBookingService {

    async bookService(date: Date, description: string): Promise<string> {
        const booking = new Booking();
        booking.startTime = date;
        booking.endTime = new Date(date.getTime() + 24 * 60 * 1000 - 1); // 11:59:59 PM
        booking.description = description;

        return axios.post('/calendar-service/api/booking', booking)
    }
}