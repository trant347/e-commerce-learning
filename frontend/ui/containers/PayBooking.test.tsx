import '@testing-library/jest-dom';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

jest.mock('../api/bookingServices', () => ({
    BookingService: {
        get: jest.fn(),
        pay: jest.fn(),
        getPaymentStatus: jest.fn(),
        cancel: jest.fn(),
    },
}));
jest.mock('../context/userContext', () => {
    const React = require('react');
    return { __esModule: true, default: React.createContext({ username: 'alice' }) };
});

import { BookingService, Booking } from '../api/bookingServices';
import PayBooking from './PayBooking';

function renderAt(id: string = 'bk-1') {
    return render(
        <MemoryRouter initialEntries={[`/booking/${id}/pay`]}>
            <Routes>
                <Route path="/booking/:id/pay" element={<PayBooking />} />
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
        status: 'ACCEPTED',
        agreedAmount: 100,
        agreedCurrency: 'USD',
        createdAt: new Date().toISOString(),
        ...overrides,
    };
}

async function fillCardAndSubmit() {
    const user = userEvent.setup();
    await user.type(screen.getByLabelText(/Cardholder Name/i), 'Alice');
    const cardNumber = screen.getByLabelText(/Card Number/i);
    await user.clear(cardNumber);
    await user.type(cardNumber, '4111111111111111');
    await user.type(screen.getByLabelText(/Expiry Date/i), '12/30');
    await user.type(screen.getByLabelText(/CVV/i), '123');
    await user.click(screen.getByRole('button', { name: /Pay \$100\.00/i }));
}

describe('PayBooking escrow lifecycle', () => {
    afterEach(() => {
        jest.clearAllMocks();
        jest.useRealTimers();
    });

    test('shows the test-only card number as a placeholder', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking());

        renderAt();

        expect(await screen.findByLabelText(/Card Number/i))
            .toHaveAttribute('placeholder', 'Test card: 4242 4242 4242 4242');
        expect(screen.getByLabelText(/Card Number/i)).toHaveValue('');
    });

    test('formats card and expiry fields while typing', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking());
        const user = userEvent.setup();

        renderAt();
        await user.type(
            await screen.findByLabelText(/Card Number/i),
            '4242424242424242',
        );
        await user.type(screen.getByLabelText(/Expiry Date/i), '1230');

        expect(screen.getByLabelText(/Card Number/i))
            .toHaveValue('4242 4242 4242 4242');
        expect(screen.getByLabelText(/Expiry Date/i)).toHaveValue('12/30');
    });

    test('funds an accepted booking and shows durable pending state from the 202 response', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking());
        (BookingService.pay as jest.Mock).mockResolvedValue({
            sagaId: 'saga-1',
            escrowId: 'escrow-1',
            status: 'PENDING',
            statusUrl: '/api/booking/payment-status/saga-1',
        });

        renderAt();
        await waitFor(() => expect(screen.getByRole('button', { name: /Pay \$100\.00/i })).toBeInTheDocument());
        await fillCardAndSubmit();

        await waitFor(() => expect(screen.getByText(/safely queued/i)).toBeInTheDocument());
        expect(BookingService.pay).toHaveBeenCalledWith('bk-1', {
            ownerName: 'Alice',
            cardNumber: '4111111111111111',
            expiryDate: '12/30',
            cvv: '123',
        });
    });

    test('shows that funded money is safely held and work may begin', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            escrowId: 'escrow-1',
            escrowStatus: 'FUNDED',
            latestPaymentStatus: 'COMPLETED',
            latestPaymentOperation: 'FUND_ESCROW',
            latestPaymentSagaId: 'saga-1',
        }));

        renderAt();

        await waitFor(() => expect(screen.getByText(/safely held in escrow/i)).toBeInTheDocument());
        expect(screen.queryByRole('button', { name: /Pay \$/i })).not.toBeInTheDocument();
    });

    test('shows completed escrow release', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            status: 'COMPLETED',
            escrowStatus: 'RELEASED',
            latestPaymentStatus: 'COMPLETED',
            latestPaymentOperation: 'RELEASE_ESCROW',
            latestPaymentSagaId: 'saga-release',
        }));

        renderAt();

        await waitFor(() => expect(screen.getByText(/released to the TaskMaster/i)).toBeInTheDocument());
    });

    test('shows completed escrow refund', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            status: 'CANCELLED',
            escrowStatus: 'REFUNDED',
            latestPaymentStatus: 'COMPLETED',
            latestPaymentOperation: 'REFUND_ESCROW',
            latestPaymentSagaId: 'saga-refund',
        }));

        renderAt();

        await waitFor(() => expect(screen.getByText(/refunded to you/i)).toBeInTheDocument());
    });

    test('requests a durable refund from funded escrow', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            escrowId: 'escrow-1',
            escrowStatus: 'FUNDED',
            latestPaymentStatus: 'COMPLETED',
            latestPaymentOperation: 'FUND_ESCROW',
            latestPaymentSagaId: 'saga-fund',
        }));
        (BookingService.cancel as jest.Mock).mockResolvedValue({
            sagaId: 'saga-refund',
            escrowId: 'escrow-1',
            status: 'PENDING',
            statusUrl: '/api/booking/payment-status/saga-refund',
        });

        renderAt();
        const button = await screen.findByRole('button', { name: /Request Refund/i });
        await userEvent.click(button);

        await waitFor(() => expect(screen.getByText(/escrow refund request is safely queued/i)).toBeInTheDocument());
        expect(BookingService.cancel).toHaveBeenCalledWith('bk-1');
    });

    test('shows decline reason and permits a retry after the saga is terminal', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            escrowId: 'escrow-1',
            escrowStatus: 'PENDING',
            latestPaymentStatus: 'FAILED',
            latestPaymentOperation: 'FUND_ESCROW',
            latestPaymentSagaId: 'saga-failed',
            latestPaymentFailureReason: 'Simulated decline test card',
        }));

        renderAt();

        await waitFor(() => expect(screen.getByText(/Simulated decline test card/i)).toBeInTheDocument());
        expect(screen.getByRole('button', { name: /Pay \$100\.00/i })).toBeInTheDocument();
    });

    test('blocks a historical synchronous payment while reconciliation is pending', async () => {
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            status: 'IMPLEMENTED',
            agreedAmount: undefined,
            invoiceAmount: 100,
            paymentPending: true,
        }));

        renderAt();

        await waitFor(() => expect(screen.getByText(/still being reconciled/i)).toBeInTheDocument());
        expect(BookingService.getPaymentStatus).not.toHaveBeenCalled();
        expect(screen.queryByRole('button', { name: /Pay \$100\.00/i })).not.toBeInTheDocument();
    });

    test('resumes pending polling from server state after a reload', async () => {
        jest.useFakeTimers();
        (BookingService.get as jest.Mock)
            .mockResolvedValueOnce(baseBooking({
                escrowId: 'escrow-1',
                escrowStatus: 'PENDING',
                latestPaymentStatus: 'PENDING',
                latestPaymentOperation: 'FUND_ESCROW',
                latestPaymentSagaId: 'saga-1',
            }))
            .mockResolvedValue(baseBooking({
                escrowId: 'escrow-1',
                escrowStatus: 'FUNDED',
                latestPaymentStatus: 'COMPLETED',
                latestPaymentOperation: 'FUND_ESCROW',
                latestPaymentSagaId: 'saga-1',
            }));
        (BookingService.getPaymentStatus as jest.Mock).mockResolvedValue({
            sagaId: 'saga-1',
            bookingId: 'bk-1',
            escrowId: 'escrow-1',
            operation: 'FUND_ESCROW',
            status: 'COMPLETED',
            escrowStatus: 'FUNDED',
            updatedAt: new Date().toISOString(),
        });

        renderAt();
        await waitFor(() => expect(screen.getByText(/safely queued/i)).toBeInTheDocument());
        await act(async () => {
            jest.advanceTimersByTime(1000);
            await Promise.resolve();
        });

        await waitFor(() => expect(screen.getByText(/safely held in escrow/i)).toBeInTheDocument());
        expect(BookingService.getPaymentStatus).toHaveBeenCalledWith('saga-1');
    });

    test('stops polling after bounded backoff and reports a timeout without losing the durable request', async () => {
        jest.useFakeTimers();
        (BookingService.get as jest.Mock).mockResolvedValue(baseBooking({
            escrowId: 'escrow-1',
            escrowStatus: 'PENDING',
            latestPaymentStatus: 'PENDING',
            latestPaymentOperation: 'FUND_ESCROW',
            latestPaymentSagaId: 'saga-1',
        }));
        (BookingService.getPaymentStatus as jest.Mock).mockResolvedValue({
            sagaId: 'saga-1',
            bookingId: 'bk-1',
            escrowId: 'escrow-1',
            operation: 'FUND_ESCROW',
            status: 'PENDING',
            escrowStatus: 'PENDING',
            updatedAt: new Date().toISOString(),
        });

        renderAt();
        await waitFor(() => expect(screen.getByText(/safely queued/i)).toBeInTheDocument());

        for (const delay of [1000, 2000, 4000, 8000, 10000]) {
            await act(async () => {
                jest.advanceTimersByTime(delay);
                await Promise.resolve();
            });
        }

        await waitFor(() => expect(screen.getByText(/taking longer than expected/i)).toBeInTheDocument());
        expect(BookingService.getPaymentStatus).toHaveBeenCalledTimes(5);
    });
});
