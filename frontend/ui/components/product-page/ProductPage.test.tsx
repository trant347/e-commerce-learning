import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router-dom';

import ProductPage from './index';

describe('ProductPage category labels', () => {
    test('displays catalog names while preserving canonical category props', () => {
        const jobCategories = ['appliance-repair', 'legacy-category'];

        render(
            <MemoryRouter>
                <ProductPage
                    id="taskmaster-1"
                    name="Alice Smith"
                    hourlyRateUsd={60}
                    jobCategories={jobCategories}
                    categoryDisplayNames={{ 'appliance-repair': 'Appliance Repair' }}
                />
            </MemoryRouter>
        );

        expect(screen.getByText('Appliance Repair')).toBeInTheDocument();
        expect(screen.getByText('legacy-category')).toBeInTheDocument();
        expect(jobCategories).toEqual(['appliance-repair', 'legacy-category']);
    });
});
