import * as React from 'react';
import { render, fireEvent } from '@testing-library/react';

// Mock the api services BEFORE importing the component under test.
jest.mock('../api/bookingServices', () => ({
    BookingService: {
        getTimetable: jest.fn().mockResolvedValue([]),
        create: jest.fn().mockResolvedValue({}),
    },
}));
jest.mock('../api/taskMasterServices', () => ({
    TaskMasterServices: {
        getTaskMasterById: jest.fn().mockResolvedValue({ id: 'tm-1', name: 'Alice' }),
    },
}));

import CalendarPage from './Booking';

const FIXED_NOW = new Date(2026, 5, 1, 8, 0, 0);

beforeAll(() => {
    jest.useFakeTimers();
    jest.setSystemTime(FIXED_NOW);
});

afterAll(() => {
    jest.useRealTimers();
});

function findSubmitButton(container: HTMLElement): HTMLButtonElement {
    const buttons = container.querySelectorAll('button');
    const submit = Array.from(buttons).find(b => (b.textContent ?? '').trim() === 'Submit');
    expect(submit).toBeTruthy();
    return submit as HTMLButtonElement;
}

describe('Booking page — Submit button', () => {
    test('is disabled on first render when no time slot has been selected', () => {
        const props = { events: [], match: { params: { id: 'tm-1' } } };

        // Render is wrapped in act() by RTL; we don't need to await the async
        // services because the Submit button's disabled state is derived
        // synchronously from `selectedDate`, which starts as null.
        const { container } = render(<CalendarPage {...props} />);

        const submit = findSubmitButton(container);
        expect(submit.disabled).toBe(true);
    });

    test('becomes enabled once a time slot is selected', () => {
        const props = { events: [], match: { params: { id: 'tm-1' } } };
        const { container } = render(<CalendarPage {...props} />);

        // Click an available cell — Mon 10:00 (Sun=0, Mon=1) in the freshly rendered week.
        const dayCols = container.querySelectorAll('.day-column');
        const cells = dayCols[1].querySelectorAll('.hour-cell');
        fireEvent.mouseDown(cells[10]);

        const submit = findSubmitButton(container);
        expect(submit.disabled).toBe(false);
    });
});
