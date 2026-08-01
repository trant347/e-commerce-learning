import axios from 'axios';

export type BookingStatus = 'PENDING' | 'ACCEPTED' | 'IN_PROGRESS' | 'DECLINED' | 'CANCELLED' | 'IMPLEMENTED' | 'COMPLETED';
export type EscrowStatus = 'PENDING' | 'FUNDED' | 'RELEASED' | 'REFUNDED';
export type PaymentOperation = 'FUND_ESCROW' | 'RELEASE_ESCROW' | 'REFUND_ESCROW';
export type PaymentSagaStatus = 'PENDING' | 'COMPLETED' | 'FAILED';

export interface PaymentAcceptedResponse {
    sagaId: string;
    escrowId: string;
    status: 'PENDING';
    statusUrl: string;
}

export interface PaymentStatusResponse {
    sagaId: string;
    bookingId: string;
    escrowId: string;
    operation: PaymentOperation;
    status: PaymentSagaStatus;
    escrowStatus?: EscrowStatus;
    failureReason?: string;
    updatedAt: string;
}

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
    agreedAmount?: number;
    agreedCurrency?: string;
    escrowId?: string;
    escrowStatus?: EscrowStatus;
    paymentTransactionId?: string;
    /**
     * True when a payment attempt for this booking is currently ambiguous/in-flight (server is
     * still resolving whether a prior charge succeeded — see PAYMENT_SAGA_SPEC.md). While true,
     * the requester must not submit another payment; this is derived server-side so it persists
     * across page reloads, unlike a purely client-side "already paid" message.
     */
    paymentPending?: boolean;
    latestPaymentSagaId?: string;
    latestPaymentStatus?: PaymentSagaStatus;
    latestPaymentOperation?: PaymentOperation;
    latestPaymentFailureReason?: string;
    createdAt: string;
    respondedAt?: string;
    implementedAt?: string;
    completedAt?: string;
}

function authHeader() {
    const token = localStorage.getItem('token');
    return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * Proof/product images are served behind product-service's JWT auth. A plain <a href>
 * navigation (or window.open to the URL directly) won't carry the Authorization header
 * that axios calls attach from localStorage, so it 401s. Instead, open a blank tab
 * synchronously (to avoid popup-blocker issues with the async fetch that follows), fetch
 * the file as an authenticated blob, then redirect that tab to a local object URL.
 */
export function openAuthenticatedFile(url: string): void {
    // Note: we deliberately don't pass 'noopener' here — that flag makes window.open()
    // return null (no reference), which is exactly what we need below to redirect the
    // tab once the authenticated blob is ready.
    const newTab = window.open('', '_blank');
    axios
        .get(url, { headers: authHeader(), responseType: 'blob' })
        .then(res => {
            const blobUrl = window.URL.createObjectURL(res.data);
            if (newTab) {
                newTab.location.href = blobUrl;
            }
        })
        .catch(() => {
            if (newTab) {
                newTab.close();
            }
            window.alert('Unable to load the proof file. Please try again.');
        });
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

    /** TaskMaster submits proof and, for escrow bookings, durably requests release. */
    submitProof(id: string, proofFileUrl: string, invoiceAmount: number): Promise<Booking | PaymentAcceptedResponse> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/submit-proof`,
            { proofFileUrl, invoiceAmount },
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    async pay(id: string, card: { cardNumber: string; expiryDate: string; cvv: string; ownerName: string }): Promise<Booking | PaymentAcceptedResponse> {
        const tokenResponse = await axios.post(
            '/payment-service/api/payment/tokenize',
            card,
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        );
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/pay`,
            { ...card, paymentMethodToken: tokenResponse.data.paymentMethodToken },
            { headers: { ...authHeader(), 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    getPaymentStatus(sagaId: string): Promise<PaymentStatusResponse> {
        return axios.get(
            `${BASE}/payment-status/${encodeURIComponent(sagaId)}`,
            { headers: authHeader() }
        ).then(res => res.data);
    },

    startWork(id: string): Promise<Booking> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/start-work`,
            {},
            { headers: authHeader() }
        ).then(res => res.data);
    },

    cancel(id: string): Promise<Booking | PaymentAcceptedResponse> {
        return axios.post(
            `${BASE}/${encodeURIComponent(id)}/cancel`,
            {},
            { headers: authHeader() }
        ).then(res => res.data);
    },
};
