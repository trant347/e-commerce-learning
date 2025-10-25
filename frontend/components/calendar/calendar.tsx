import { useState, useEffect } from 'react';
import { format, startOfMonth, endOfMonth, startOfWeek, endOfWeek, addDays, isSameDay, isSameMonth, isBefore, isAfter } from 'date-fns';
import './calendar.css'; // Import the CSS file
import * as React from 'react';

export interface Event {
  id: number;
  title: string;
  start: Date;
  end: Date;
}

interface CalendarProps {
  events: Event[];
  onChange: (date: Date) => void;
}

export const Calendar: React.FC<CalendarProps> = ({ events, onChange }) => {
  const [currentMonth, setCurrentMonth] = useState(new Date());
  const [selectedDate, setSelectedDate] = useState(new Date());

  const firstDayOfMonth = startOfMonth(currentMonth);
  const lastDayOfMonth = endOfMonth(currentMonth);
  const firstDayOfWeek = startOfWeek(firstDayOfMonth);
  const lastDayOfWeek = endOfWeek(lastDayOfMonth);

  const days = [];
  let currentDay = firstDayOfWeek;

  while (currentDay <= lastDayOfWeek) {
    days.push(currentDay);
    currentDay = addDays(currentDay, 1);
  }

  const handleDayClick = (day: Date) => {
    setSelectedDate(day);
    onChange(day);
  };

  const handlePrevMonth = () => {
    setCurrentMonth(addDays(startOfMonth(currentMonth), -1));
  };

  const handleNextMonth = () => {
    setCurrentMonth(addDays(startOfMonth(currentMonth), 31));
  };

  const getEventsForDay = (day: Date): Event[] => {
    if (!events) {
      return [];
    }
    return events.filter(event => {
      return (
        (isSameDay(event.start, day) ||
          (isBefore(day, event.end) && isAfter(day, event.start)) ||
          isSameDay(event.end, day)
        )
      );
    });
  };

  return (
    <div className="calendar">
      <div className="calendar-header">
        <button onClick={handlePrevMonth}>&lt;</button>
        <h2>{format(currentMonth, 'MMMM yyyy')}</h2>
        <button onClick={handleNextMonth}>&gt;</button>
      </div>
      <div className="calendar-grid">
        <div className="weekdays">
          {['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map(day => (
            <div key={day} className="weekday">
              {day}
            </div>
          ))}
        </div>
        <div className="days">
          {days.map(day => (
            <div
              key={day.toISOString()}
              className={`day ${
                !isSameMonth(day, currentMonth) ? 'disabled' : ''
              } ${isSameDay(day, selectedDate) ? 'selected' : ''}`}
              onClick={() => handleDayClick(day)}
            >
              <div className="day-number">{format(day, 'd')}</div>
              <div className="day-events">
                {getEventsForDay(day).map(event => (
                  <div key={event.id} className="event">
                    {event.title}
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};