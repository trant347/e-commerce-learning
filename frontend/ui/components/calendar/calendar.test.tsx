import { render, fireEvent } from '@testing-library/react';

import { Calendar, BusyRange } from './calendar';

/**
 * The Calendar starts on `new Date()` and only allows selection from "now" forward.
 * We freeze the clock to a known Monday morning so the cells we exercise are stable.
 */
const FIXED_NOW = new Date(2026, 5, 1, 8, 0, 0); // Mon Jun 1, 2026 08:00 local

beforeAll(() => {
    jest.useFakeTimers();
    jest.setSystemTime(FIXED_NOW);
});

afterAll(() => {
    jest.useRealTimers();
});

/**
 * Returns the hour-cell <div> for (dayIndex, hour) in the visible week.
 * The DOM lays out 7 day columns with 24 hour-cells each, in render order.
 */
function getCell(container: HTMLElement, dayIndex: number, hour: number): HTMLElement {
    const columns = container.querySelectorAll('.day-column');
    expect(columns.length).toBe(7);
    const cells = columns[dayIndex].querySelectorAll('.hour-cell');
    expect(cells.length).toBe(24);
    return cells[hour] as HTMLElement;
}

describe('Calendar', () => {
    test('marks busy ranges as .busy and ignores mouseDown on them', () => {
        const onChange = jest.fn();
        // Block Mon 14:00 – 15:00 local
        const busy: BusyRange[] = [{
            start: new Date(2026, 5, 1, 14, 0, 0),
            end: new Date(2026, 5, 1, 15, 0, 0),
        }];

        const { container } = render(<Calendar events={[]} busy={busy} onChange={onChange} />);

        const blockedCell = getCell(container, 1 /* Mon, Sun is index 0 */, 14);
        expect(blockedCell.className).toMatch(/\bbusy\b/);

        fireEvent.mouseDown(blockedCell);
        expect(onChange).not.toHaveBeenCalled();
    });

    test('single click on an available cell selects that hour (duration = 1)', () => {
        const onChange = jest.fn();

        const { container } = render(<Calendar events={[]} onChange={onChange} />);

        const cell = getCell(container, 1, 10); // Mon 10:00
        fireEvent.mouseDown(cell);

        expect(onChange).toHaveBeenCalledTimes(1);
        const [start, duration] = onChange.mock.calls[0];
        expect(duration).toBe(1);
        expect(start.getHours()).toBe(10);
        expect(start.getDate()).toBe(1);
        expect(start.getMonth()).toBe(5);
    });

    test('click-then-click on a later cell in the same day produces the correct multi-hour duration', () => {
        const onChange = jest.fn();

        const { container } = render(<Calendar events={[]} onChange={onChange} />);

        const startCell = getCell(container, 1, 10); // Mon 10:00
        const endCell = getCell(container, 1, 12);   // Mon 12:00 — inclusive, so 3 hours

        fireEvent.mouseDown(startCell);              // anchor + initial 1h selection
        fireEvent.mouseUp(startCell);                // no drag → goes to 'awaiting-end'
        fireEvent.mouseDown(endCell);                // closes range

        const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1];
        const [start, duration] = lastCall;
        expect(start.getHours()).toBe(10);
        expect(duration).toBe(3);
    });

    test('drag selection across multiple cells emits the spanned duration', () => {
        const onChange = jest.fn();

        const { container } = render(<Calendar events={[]} onChange={onChange} />);

        const startCell = getCell(container, 2, 9);  // Tue 09:00
        const midCell   = getCell(container, 2, 10);
        const endCell   = getCell(container, 2, 11); // Tue 11:00 — 3 hours

        fireEvent.mouseDown(startCell);
        fireEvent.mouseEnter(midCell);
        fireEvent.mouseEnter(endCell);
        fireEvent.mouseUp(window);

        const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1];
        const [start, duration] = lastCall;
        expect(start.getHours()).toBe(9);
        expect(duration).toBe(3);
    });

    test('drag selection that crosses a busy cell does NOT emit onChange', () => {
        const onChange = jest.fn();
        // Block Tue 10:00 - 11:00 (right in the middle of the planned drag)
        const busy: BusyRange[] = [{
            start: new Date(2026, 5, 2, 10, 0, 0),
            end: new Date(2026, 5, 2, 11, 0, 0),
        }];

        const { container } = render(<Calendar events={[]} busy={busy} onChange={onChange} />);

        const startCell = getCell(container, 2, 9);   // Tue 09:00 (free)
        const busyCell  = getCell(container, 2, 10);  // Tue 10:00 (blocked)

        fireEvent.mouseDown(startCell);   // 1h selection of just 09:00, emits onChange(_, 1)
        const callsBefore = onChange.mock.calls.length;

        // Try to drag through the blocked cell. Calendar's onMouseEnter is undefined for busy
        // cells, so this should be a no-op and no further onChange should fire.
        fireEvent.mouseEnter(busyCell);
        fireEvent.mouseUp(window);

        expect(onChange.mock.calls.length).toBe(callsBefore);
    });
});
