import axios from 'axios';

export type BookingStatus = 'PENDING' | 'ACCEPTED' | 'DECLINED' | 'CANCELLED' | 'IMPLEMENTED' | 'COMPLETED';

export interface Booking {
    id: string;
    taskMasterId: string;
    taskMasterUsername: string;
    requesterUsername: string;
    slotStart: string;          // ISO UTC, hour-aligned
    durationHours: number;      // >= 1
    status: BookingStatus;
    requestMessage?: string;
    responseMessage?: string;
    offeredRatePerHour?: number;
    offeredTotalAmount?: number;
    proofFileUrl?: string;
    invoiceAmount?: number;
    paymentTransactionId?: string;
    createdAt: string;
    respondedAt?: string;
    implementedAt?: string;
    completedAt?: string;
}

function authHeader() {
    const token = localStorage.getItem('token');
    return token ? { Authorization: `Bearer ${token}` } : {};
}

const BASE = '/calendar-service/api/booking';

export const BookingService = {
    getTimetable(taskMasterId: string): Promise<Booking[]> {
        return axios.get(
            `${BASE}/taskmasters/${encodeURIComponent(taskMasterId)}/timetable`,
            { headers: authHeader() }
        ).then(res => res.data);
    },

    /** slotStart MUST be hour-aligned UTC (minutes/seconds/ms = 0). durationHours is the number of consecutive 1-hour slots (>=1). */
    create(taskMasterId: string, slotStart: Date, durationHours: number = 1, message?: string, offeredRatePerHour?: number): Promise<Booking> {
        return axios.post(
            BASE,
            { taskMasterId, slotStart: slotStart.toISOString(), durationHours, message, offeredRatePerHour },
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    listIncoming(status?: BookingStatus): Promise<Booking[]> {
        const q = status ? `?status=${status}` : '';
        return axios.get(`${BASE}/incoming${q}`, { headers: authHeader() }).then(res => res.data);
    },

    listOutgoing(status?: BookingStatus): Promise<Booking[]> {
        const q = status ? `?status=${status}` : '';
        return axios.get(`${BASE}/outgoing${q}`, { headers: authHeader() }).then(res => res.data);
    },

    get(id: string): Promise<Booking> {
        return axios.get(`${BASE}/${encodeURIComponent(id)}`, { headers: authHeader() })
            .then(res => res.data);
    },

    accept(id: string, message?: string): Promise<Booking> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/accept`,
            { message },
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    decline(id: string, message?: string): Promise<Booking> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/decline`,
            { message },
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    /** TaskMaster submits proof of the completed job (a file/image URL) plus the invoice amount. */
    submitProof(id: string, proofFileUrl: string, invoiceAmount: number): Promise<Booking> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/submit-proof`,
            { proofFileUrl, invoiceAmount },
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    /** Requester pays the invoice by submitting card details; calendar-service forwards them
     * to payment-service server-to-server and verifies the result before completing the booking. */
    pay(id: string, card: { cardNumber: string; expiryDate: string; cvv: string; ownerName: string }): Promise<Booking> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/pay`,
            card,
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },
};