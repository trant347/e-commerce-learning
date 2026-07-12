import '@testing-library/jest-dom';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

// Mock the api service BEFORE importing the component under test.
jest.mock('../api/bookingServices', () => ({
    BookingService: {
        get: jest.fn(),
        pay: jest.fn(),
    },
    openAuthenticatedFile: jest.fn(),
}));
jest.mock('../context/userContext', () => {
    const React = require('react');
    return { __esModule: true, default: React.createContext({ username: 'alice' }) };
});

import { BookingService, Booking } from '../api/bookingServices';
import PayBooking from './PayBooking';

function renderAt(id: string) {
    return render(
        <MemoryRouter initialEntries={[`/pay/${id}`]}>
            <Routes>
                <Route path="/pay/:id" element={<PayBooking />} />
            </Routes>
        </MemoryRouter>
    );
}

function baseBooking(overrides: Partial<Booking> = {}): Booking {
    return {
        id: 'bk-1',
        taskMasterId: 'tm-1',
        taskMasterUsername: 'bob',
        requesterUsername: 'alice',
        slotStart: new Date().toISOString(),
        durationHours: 1,
        status: 'IMPLEMENTED',
        invoiceAmount: 100,
        createdAt: new Date().toISOString(),
        ...overrides,
    };
}

describe('PayBooking — payment-pending guard', () => {
    afterEach(() => {
        jest.clearAllMocks();
    });

    test('shows the payment form when paymentPending is false', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({ paymentPending: false }));

        renderAt('bk-1');

        await waitFor(() => expect(screen.getByText(/Amount due/i)).toBeInTheDocument());
        expect(screen.queryByText(/being processed/i)).not.toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Pay \$100\.00/i })).toBeInTheDocument();
    });

    test('blocks the payment form and shows a processing message when paymentPending is true', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({ paymentPending: true }));

        renderAt('bk-1');

        await waitFor(() => expect(screen.getByText(/being processed/i)).toBeInTheDocument());
        expect(screen.queryByRole('button', { name: /Pay \$100\.00/i })).not.toBeInTheDocument();
        expect(BookingService.pay).not.toHaveBeenCalled();
    });

    test('paymentPending persists across a fresh page load (no reliance on submit-time state)', async () => {
        // Simulates the user closing and reopening the browser: the component mounts fresh
        // with no prior submit attempt, yet still must show the processing message because the
        // signal comes from the server (Booking.paymentPending), not local React state.
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({ paymentPending: true }));

        renderAt('bk-1');

        await waitFor(() => expect(screen.getByText(/being processed/i)).toBeInTheDocument());
        expect(BookingService.get).toHaveBeenCalledWith('bk-1');
    });
});
