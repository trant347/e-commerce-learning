import { render, waitFor, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router-dom';

import UserContext from '../../context/userContext';
import ApplicationBadgeContext from '../../context/applicationBadgeContext';

// Mock the api service BEFORE importing the component under test.
jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        getMyTaskMaster: jest.fn(),
    },
}));

// The notification hook pulls in an EventSource-based SSE subscription that
// isn't relevant here; stub it out so the header renders deterministically.
jest.mock('../../hooks/useNotifications', () => ({
    useNotifications: () => ({
        notifications: [],
        unreadCount: 0,
        markAsRead: () => {},
        lastNotification: null,
    }),
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import PageHeader from './index';

const getMyTaskMasterMock = TaskMasterServices.getMyTaskMaster as jest.Mock;

function renderHeader(username: string | null) {
    return render(
        <UserContext.Provider value={{ username, setUsername: () => {} } as any}>
            <ApplicationBadgeContext.Provider value={{
                unviewedApplicationCount: 0,
                setUnviewedApplicationCount: () => {},
                decrementUnviewedCount: () => {},
            }}>
                <MemoryRouter>
                    <PageHeader />
                </MemoryRouter>
            </ApplicationBadgeContext.Provider>
        </UserContext.Provider>
    );
}

describe('PageHeader — Apply for TaskMaster link', () => {
    beforeEach(() => {
        getMyTaskMasterMock.mockReset();
    });

    test('is a disabled, non-clickable label when the user is already a TaskMaster', async () => {
        getMyTaskMasterMock.mockResolvedValue({ id: 'tm-1', name: 'Alice' });

        renderHeader('alice');

        await waitFor(() => {
            expect(getMyTaskMasterMock).toHaveBeenCalled();
        });

        await waitFor(() => {
            const label = screen.getByText('Apply for TaskMaster');
            expect(label.tagName).toBe('SPAN');
        });

        expect(screen.queryByRole('link', { name: /apply for taskmaster/i })).not.toBeInTheDocument();
    });

    test('is a clickable link when the user is not yet a TaskMaster', async () => {
        getMyTaskMasterMock.mockResolvedValue(null);

        renderHeader('bob');

        await waitFor(() => {
            expect(getMyTaskMasterMock).toHaveBeenCalled();
        });

        await waitFor(() => {
            expect(screen.getByRole('link', { name: /apply for taskmaster/i })).toBeInTheDocument();
        });
    });
});
