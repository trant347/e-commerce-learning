import * as React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';

jest.mock('./Home', () => ({
    __esModule: true,
    default: () => <div>Home page</div>,
}));

jest.mock('../context/ApplicationBadgeProvider', () => ({
    __esModule: true,
    default: ({ children }) => <>{children}</>,
}));

jest.mock('../components/page-header', () => ({
    __esModule: true,
    default: function TestHeader() {
        const { setUsername } = React.useContext(UserContext);
        return (
            <>
                <button onClick={() => {
                    Auth.authenticateUser('test-token', 'alice');
                    setUsername('alice');
                }}>Log in</button>
                <button onClick={() => setUsername(null)}>Log out</button>
            </>
        );
    },
}));

jest.mock('../api/aiAssistantServices', () => ({
    __esModule: true,
    default: { getConfig: jest.fn(), chat: jest.fn() },
}));

import App from './App';
import Auth from '../api/authenticationStorage';
import AiAssistantServices from '../api/aiAssistantServices';
import UserContext from '../context/userContext';

beforeEach(() => {
    localStorage.clear();
    jest.clearAllMocks();
    window.history.replaceState({}, '', '/');
    (AiAssistantServices.getConfig as jest.Mock).mockResolvedValue({ historyMessageLimit: 5 });
});

test('hides the chat panel and launcher from visitors, then shows chat after login', async () => {
    render(<App />);
    await screen.findByText('Home page');

    expect(screen.queryByText('AI Assistant')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Chat' })).not.toBeInTheDocument();
    expect(AiAssistantServices.getConfig).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Log in' }));

    expect(await screen.findByText('AI Assistant')).toBeInTheDocument();
    expect(AiAssistantServices.getConfig).toHaveBeenCalledTimes(1);
});

test.each([false, true])('removes chat on logout when minimized is %s', async (minimized) => {
    Auth.authenticateUser('test-token', 'alice');
    render(<App />);
    expect(await screen.findByText('AI Assistant')).toBeInTheDocument();

    if (minimized) {
        fireEvent.click(screen.getByRole('button', { name: 'Minimize chat' }));
        expect(screen.getByRole('button', { name: 'Chat' })).toBeInTheDocument();
    }

    fireEvent.click(screen.getByRole('button', { name: 'Log out' }));

    expect(screen.queryByText('AI Assistant')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Chat' })).not.toBeInTheDocument();
    expect(Auth.isUserAuthenticated()).toBe(false);
});
