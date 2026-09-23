import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router-dom';

import UserContext from '../../context/userContext';

jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        createTaskMaster: jest.fn(),
        listCategoryMetadata: jest.fn(),
    },
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import NewTaskMaster from './NewTaskMaster';

const createTaskMasterMock = TaskMasterServices.createTaskMaster as jest.Mock;
const listCategoryMetadataMock = TaskMasterServices.listCategoryMetadata as jest.Mock;

function renderPage() {
    return render(
        <UserContext.Provider value={{ username: 'admin', setUsername: () => {} } as any}>
            <MemoryRouter>
                <NewTaskMaster />
            </MemoryRouter>
        </UserContext.Provider>
    );
}

describe('NewTaskMaster category selection', () => {
    beforeEach(() => {
        createTaskMasterMock.mockReset();
        createTaskMasterMock.mockResolvedValue({ id: 'taskmaster-1' });
        listCategoryMetadataMock.mockReset();
        listCategoryMetadataMock.mockResolvedValue([
            {
                id: 'appliance-repair',
                displayName: 'Appliance Repair',
                description: 'Household appliance diagnosis and repair.',
            },
        ]);
    });

    test('submits the selected canonical category ID', async () => {
        renderPage();

        fireEvent.change(screen.getByPlaceholderText(/e\.g\. john smith/i), {
            target: { value: 'Alice Smith' },
        });
        fireEvent.change(screen.getByPlaceholderText(/e\.g\. 30/i), {
            target: { value: '35' },
        });
        fireEvent.change(screen.getByLabelText(/city/i), {
            target: { value: 'Boston' },
        });
        fireEvent.change(screen.getByLabelText(/state/i), {
            target: { value: 'MA' },
        });
        fireEvent.change(screen.getByPlaceholderText(/e\.g\. 45\.00/i), {
            target: { value: '60' },
        });
        fireEvent.click(await screen.findByRole('checkbox', { name: /appliance repair/i }));
        fireEvent.click(screen.getByRole('button', { name: /create taskmaster/i }));

        await waitFor(() => {
            expect(createTaskMasterMock).toHaveBeenCalledWith(
                expect.objectContaining({
                    jobCategories: ['appliance-repair'],
                })
            );
        });
    });

    test('keeps submission disabled when the catalog is empty', async () => {
        listCategoryMetadataMock.mockResolvedValue([]);

        renderPage();

        expect(await screen.findByText(/no categories are currently available/i))
            .toBeInTheDocument();
        expect(screen.getByRole('button', { name: /create taskmaster/i })).toBeDisabled();
        expect(screen.queryByRole('textbox', { name: /categories/i })).not.toBeInTheDocument();
    });
});
