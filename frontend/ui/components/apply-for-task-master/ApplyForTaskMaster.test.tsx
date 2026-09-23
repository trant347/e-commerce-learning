import { fireEvent, render, waitFor, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router-dom';

import UserContext from '../../context/userContext';

// Mock the api service BEFORE importing the component under test.
jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        getMyTaskMaster: jest.fn(),
        listCategoryMetadata: jest.fn(),
        submitApplication: jest.fn(),
    },
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import ApplyForTaskMaster from './ApplyForTaskMaster';

const getMyTaskMasterMock = TaskMasterServices.getMyTaskMaster as jest.Mock;
const listCategoryMetadataMock = TaskMasterServices.listCategoryMetadata as jest.Mock;
const submitApplicationMock = TaskMasterServices.submitApplication as jest.Mock;

function renderWithUser(username: string | null) {
    return render(
        <UserContext.Provider value={{ username, setUsername: () => {} } as any}>
            <MemoryRouter initialEntries={['/apply']}>
                <ApplyForTaskMaster />
            </MemoryRouter>
        </UserContext.Provider>
    );
}

describe('ApplyForTaskMaster — already a TaskMaster', () => {
    beforeEach(() => {
        getMyTaskMasterMock.mockReset();
        listCategoryMetadataMock.mockReset();
        listCategoryMetadataMock.mockResolvedValue([
            {
                id: 'carpentry',
                displayName: 'Carpentry',
                description: 'Wood construction and repair.',
            },
        ]);
        submitApplicationMock.mockReset();
    });

    test('blocks the application form and shows a message when the user is already a TaskMaster', async () => {
        getMyTaskMasterMock.mockResolvedValue({ id: 'tm-1', name: 'Alice' });

        renderWithUser('alice');

        await waitFor(() => {
            expect(screen.getByText(/you're already a taskmaster/i)).toBeInTheDocument();
        });

        // The application form must not be rendered.
        expect(screen.queryByText(/submit application/i)).not.toBeInTheDocument();
        expect(screen.queryByPlaceholderText(/e\.g\. john smith/i)).not.toBeInTheDocument();
    });

    test('allows the application form when the user is not yet a TaskMaster', async () => {
        getMyTaskMasterMock.mockResolvedValue(null);

        renderWithUser('bob');

        await waitFor(() => {
            expect(screen.getByText(/apply to become a taskmaster/i)).toBeInTheDocument();
        });

        expect(screen.queryByText(/you're already a taskmaster/i)).not.toBeInTheDocument();
    });

    test('submits city and state as a canonical location', async () => {
        getMyTaskMasterMock.mockResolvedValue(null);
        submitApplicationMock.mockResolvedValue({ id: 'app-1' });

        renderWithUser('bob');

        await screen.findByText(/apply to become a taskmaster/i);
        fireEvent.change(screen.getByPlaceholderText(/e\.g\. john smith/i), { target: { value: 'Bob Smith' } });
        fireEvent.change(screen.getByPlaceholderText(/e\.g\. 30/i), { target: { value: '30' } });
        fireEvent.change(screen.getByLabelText(/city/i), { target: { value: ' Chicago ' } });
        fireEvent.change(screen.getByLabelText(/state/i), { target: { value: 'IL' } });
        fireEvent.change(screen.getByPlaceholderText(/e\.g\. 45\.00/i), { target: { value: '50' } });
        fireEvent.click(await screen.findByRole('checkbox', { name: /carpentry/i }));
        fireEvent.click(screen.getByRole('button', { name: /submit application/i }));

        await waitFor(() => {
            expect(submitApplicationMock).toHaveBeenCalledWith(
                expect.objectContaining({
                    location: 'Chicago, IL',
                    jobCategories: ['carpentry'],
                })
            );
        });
    });

    test('prevents submission until a catalog category is selected', async () => {
        getMyTaskMasterMock.mockResolvedValue(null);

        renderWithUser('bob');

        await screen.findByRole('checkbox', { name: /carpentry/i });

        expect(screen.getByRole('button', { name: /submit application/i })).toBeDisabled();
        expect(submitApplicationMock).not.toHaveBeenCalled();
    });
});
