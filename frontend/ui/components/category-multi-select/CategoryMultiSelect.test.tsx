import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';

jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        listCategoryMetadata: jest.fn(),
    },
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import CategoryMultiSelect from './CategoryMultiSelect';

const listCategoryMetadataMock = TaskMasterServices.listCategoryMetadata as jest.Mock;

const categories = [
    {
        id: 'appliance-repair',
        displayName: 'Appliance Repair',
        description: 'Household appliance diagnosis and repair.',
    },
    {
        id: 'carpentry',
        displayName: 'Carpentry',
        description: 'Wood construction and repair.',
    },
];

describe('CategoryMultiSelect', () => {
    beforeEach(() => {
        listCategoryMetadataMock.mockReset();
    });

    test('displays readable names and emits canonical IDs', async () => {
        listCategoryMetadataMock.mockResolvedValue(categories);
        const onChange = jest.fn();

        render(<CategoryMultiSelect selectedIds={[]} onChange={onChange} />);

        expect(screen.getByText('Loading categories...')).toBeInTheDocument();

        fireEvent.click(await screen.findByRole('checkbox', { name: /appliance repair/i }));

        expect(onChange).toHaveBeenCalledWith(['appliance-repair']);
        expect(screen.getByText('Household appliance diagnosis and repair.')).toBeInTheDocument();
    });

    test('shows an empty-catalog state without a free-text fallback', async () => {
        listCategoryMetadataMock.mockResolvedValue([]);

        render(<CategoryMultiSelect selectedIds={[]} onChange={() => {}} />);

        expect(await screen.findByText(/no categories are currently available/i))
            .toBeInTheDocument();
        expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    });

    test('shows a load failure and retries the catalog request', async () => {
        listCategoryMetadataMock
            .mockRejectedValueOnce(new Error('network failure'))
            .mockResolvedValueOnce(categories);

        render(<CategoryMultiSelect selectedIds={[]} onChange={() => {}} />);

        expect(await screen.findByText(/categories could not be loaded/i))
            .toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', { name: /retry/i }));

        expect(await screen.findByRole('checkbox', { name: /carpentry/i }))
            .toBeInTheDocument();
        await waitFor(() => {
            expect(listCategoryMetadataMock).toHaveBeenCalledTimes(2);
        });
    });
});
