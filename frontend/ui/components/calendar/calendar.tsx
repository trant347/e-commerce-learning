import { useState, useMemo, useRef, useEffect, useCallback } from 'react';
import {
  format,
  startOfWeek,
  endOfWeek,
  addDays,
  addWeeks,
  addHours,
  isSameDay,
  isToday,
  differenceInMinutes,
  startOfDay,
  max as maxDate,
  min as minDate,
} from 'date-fns';
import './calendar.css';
import * as React from 'react';

export interface Event {
  id: number;
  title: string;
  start: Date;
  end: Date;
}

interface CalendarProps {
  events: Event[];
  /** Fires whenever the user selects a time range. Single-click = 1 hour. */
  onChange: (start: Date, durationHours: number) => void;
}

const HOUR_HEIGHT = 48; // px per hour
const HOURS = Array.from({ length: 24 }, (_, i) => i);

type Cell = { dayIndex: number; hour: number };

export const Calendar: React.FC<CalendarProps> = ({ events, onChange }) => {
  const [currentDate, setCurrentDate] = useState(new Date());

  const weekStart = useMemo(() => startOfWeek(currentDate, { weekStartsOn: 0 }), [currentDate]);
  const weekEnd = useMemo(() => endOfWeek(currentDate, { weekStartsOn: 0 }), [currentDate]);
  const days = useMemo(
    () => Array.from({ length: 7 }, (_, i) => addDays(weekStart, i)),
    [weekStart]
  );

  const [anchor, setAnchor] = useState<Cell | null>(null);
  const [focus, setFocus] = useState<Cell | null>(null);
  const [mode, setMode] = useState<'idle' | 'dragging' | 'awaiting-end'>('idle');
  const movedRef = useRef(false);

  const now = new Date();
  const todayStart = startOfDay(now);
  const currentHour = now.getHours();

  const isPastCell = useCallback(
    (day: Date, hour: number) => {
      if (day < todayStart) return true;
      if (isSameDay(day, now) && hour < currentHour) return true;
      return false;
    },
    [todayStart, now, currentHour]
  );

  const emitSelection = useCallback(
    (a: Cell, f: Cell) => {
      if (a.dayIndex !== f.dayIndex) return;
      const day = days[a.dayIndex];
      const startHour = Math.min(a.hour, f.hour);
      const endHour = Math.max(a.hour, f.hour);
      const start = addHours(startOfDay(day), startHour);
      onChange(start, endHour - startHour + 1);
    },
    [days, onChange]
  );

  useEffect(() => {
    const onUp = () => {
      if (mode !== 'dragging') return;
      if (movedRef.current && anchor && focus) {
        emitSelection(anchor, focus);
        setMode('idle');
      } else {
        setMode('awaiting-end');
      }
    };
    window.addEventListener('mouseup', onUp);
    return () => window.removeEventListener('mouseup', onUp);
  }, [mode, anchor, focus, emitSelection]);

  const handleCellMouseDown = (dayIndex: number, hour: number) => (e: React.MouseEvent) => {
    e.preventDefault();
    if (isPastCell(days[dayIndex], hour)) return;
    const cell: Cell = { dayIndex, hour };
    if (mode === 'awaiting-end' && anchor && anchor.dayIndex === dayIndex) {
      setFocus(cell);
      emitSelection(anchor, cell);
      setMode('idle');
      return;
    }
    movedRef.current = false;
    setAnchor(cell);
    setFocus(cell);
    setMode('dragging');
    emitSelection(cell, cell);
  };

  const handleCellMouseEnter = (dayIndex: number, hour: number) => () => {
    if (mode !== 'dragging' || !anchor) return;
    if (dayIndex !== anchor.dayIndex) return;
    if (isPastCell(days[dayIndex], hour)) return;
    if (!focus || focus.hour !== hour) movedRef.current = true;
    setFocus({ dayIndex, hour });
  };

  const handlePrevWeek = () => setCurrentDate(addWeeks(currentDate, -1));
  const handleNextWeek = () => setCurrentDate(addWeeks(currentDate, 1));
  const handleToday = () => setCurrentDate(new Date());

  const getEventsForDay = (day: Date): Event[] => {
    if (!events) return [];
    const dayStart = startOfDay(day);
    const dayEnd = addDays(dayStart, 1);
    return events.filter(e => e.start < dayEnd && e.end > dayStart);
  };

  const getEventStyle = (event: Event, day: Date): React.CSSProperties => {
    const dayStart = startOfDay(day);
    const dayEnd = addDays(dayStart, 1);
    const start = maxDate([event.start, dayStart]);
    const end = minDate([event.end, dayEnd]);
    const topMinutes = differenceInMinutes(start, dayStart);
    const durationMinutes = Math.max(differenceInMinutes(end, start), 30);
    return {
      top: `${(topMinutes / 60) * HOUR_HEIGHT}px`,
      height: `${(durationMinutes / 60) * HOUR_HEIGHT}px`,
    };
  };

  const selectionForDay = (dayIndex: number) => {
    if (!anchor || !focus) return null;
    if (anchor.dayIndex !== dayIndex || focus.dayIndex !== dayIndex) return null;
    const startHour = Math.min(anchor.hour, focus.hour);
    const endHour = Math.max(anchor.hour, focus.hour);
    const hours = endHour - startHour + 1;
    return {
      top: startHour * HOUR_HEIGHT,
      height: hours * HOUR_HEIGHT,
      hours,
      startHour,
      endHour,
    };
  };

  const weekLabel = (() => {
    if (weekStart.getMonth() === weekEnd.getMonth()) {
      return format(weekStart, 'MMMM yyyy');
    }
    if (weekStart.getFullYear() === weekEnd.getFullYear()) {
      return `${format(weekStart, 'MMM')} – ${format(weekEnd, 'MMM yyyy')}`;
    }
    return `${format(weekStart, 'MMM yyyy')} – ${format(weekEnd, 'MMM yyyy')}`;
  })();

  const nowTop = (differenceInMinutes(now, todayStart) / 60) * HOUR_HEIGHT;

  return (
    <div className="calendar">
      <div className="calendar-header">
        <div className="calendar-header-left">
          <button className="today-button" onClick={handleToday}>Today</button>
          <button className="nav-button" onClick={handlePrevWeek} aria-label="Previous week">&lt;</button>
          <button className="nav-button" onClick={handleNextWeek} aria-label="Next week">&gt;</button>
          <h2>{weekLabel}</h2>
        </div>
        <div className="calendar-header-right">
          <span className="view-label">Week</span>
        </div>
      </div>

      <div className="week-view">
        <div className="week-header">
          <div className="time-gutter-header" />
          {days.map(day => (
            <div
              key={day.toISOString()}
              className={`day-header ${isToday(day) ? 'today' : ''} ${day < todayStart ? 'past' : ''}`}
            >
              <div className="day-name">{format(day, 'EEE')}</div>
              <div className="day-date">{format(day, 'd')}</div>
            </div>
          ))}
        </div>

        <div className="week-body">
          <div className="time-gutter">
            {HOURS.map(h => (
              <div key={h} className="time-slot-label" style={{ height: `${HOUR_HEIGHT}px` }}>
                <span>{format(new Date(2000, 0, 1, h), 'h a')}</span>
              </div>
            ))}
          </div>

          <div className="day-columns">
            {days.map((day, dayIndex) => {
              const sel = selectionForDay(dayIndex);
              const dayIsPast = day < todayStart;
              return (
                <div
                  key={day.toISOString()}
                  className={`day-column ${isToday(day) ? 'today' : ''} ${dayIsPast ? 'past' : ''} ${mode !== 'idle' ? 'selecting' : ''}`}
                >
                  {HOURS.map(h => {
                    const past = isPastCell(day, h);
                    return (
                      <div
                        key={h}
                        className={`hour-cell ${past ? 'past' : ''}`}
                        style={{ height: `${HOUR_HEIGHT}px` }}
                        onMouseDown={past ? undefined : handleCellMouseDown(dayIndex, h)}
                        onMouseEnter={past ? undefined : handleCellMouseEnter(dayIndex, h)}
                      />
                    );
                  })}

                  {isSameDay(day, now) && (
                    <div className="now-line" style={{ top: `${nowTop}px` }}>
                      <div className="now-dot" />
                    </div>
                  )}

                  {sel && (
                    <div
                      className={`selection ${mode === 'awaiting-end' ? 'pending' : ''}`}
                      style={{ top: `${sel.top}px`, height: `${sel.height}px` }}
                    >
                      <div className="selection-label">
                        {sel.hours} {sel.hours === 1 ? 'hour' : 'hours'}
                      </div>
                      <div className="selection-time">
                        {format(addHours(startOfDay(day), sel.startHour), 'p')} – {format(addHours(startOfDay(day), sel.endHour + 1), 'p')}
                      </div>
                    </div>
                  )}

                  {getEventsForDay(day).map(event => (
                    <div
                      key={`${event.id}-${day.toISOString()}`}
                      className="event"
                      style={getEventStyle(event, day)}
                      title={`${event.title}\n${format(event.start, 'p')} – ${format(event.end, 'p')}`}
                    >
                      <div className="event-title">{event.title}</div>
                      <div className="event-time">
                        {format(event.start, 'p')} – {format(event.end, 'p')}
                      </div>
                    </div>
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
};