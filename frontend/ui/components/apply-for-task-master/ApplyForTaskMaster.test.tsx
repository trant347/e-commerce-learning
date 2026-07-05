import { render, waitFor, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router-dom';

import UserContext from '../../context/userContext';

// Mock the api service BEFORE importing the component under test.
jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        getMyTaskMaster: jest.fn(),
    },
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import ApplyForTaskMaster from './ApplyForTaskMaster';

const getMyTaskMasterMock = TaskMasterServices.getMyTaskMaster as jest.Mock;

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
});
