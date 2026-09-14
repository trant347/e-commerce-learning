import { formatUsLocation, US_STATES, validateUsCity } from './usStates';

describe('US location helpers', () => {
    test('formats a canonical city and state value', () => {
        expect(formatUsLocation('  New   York ', 'NY')).toBe('New York, NY');
    });

    test('includes Illinois in the supported states', () => {
        expect(US_STATES).toContainEqual(['IL', 'Illinois']);
    });

    test('rejects a state embedded in the city field', () => {
        expect(validateUsCity('Chicago, IL')).toMatch(/select the state separately/i);
    });
});
