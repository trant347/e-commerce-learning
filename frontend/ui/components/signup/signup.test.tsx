import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';

jest.mock('../../api/authServices', () => ({
    __esModule: true,
    default: {
        signup: jest.fn(),
    },
}));

import AuthServices from '../../api/authServices';
import Signup from './signup';

const signupMock = AuthServices.signup as jest.Mock;

function fillAndSubmit(container: HTMLElement) {
    fireEvent.change(container.querySelector('input[name="username"]')!, { target: { value: 'Mary' } });
    fireEvent.change(container.querySelector('input[name="email"]')!, { target: { value: 'mary@gmail.com' } });
    fireEvent.change(container.querySelector('input[name="password"]')!, { target: { value: 'pass123' } });
    fireEvent.click(screen.getByRole('button', { name: /submit/i }));
}

describe('Signup', () => {
    beforeEach(() => signupMock.mockReset());

    it('shows the reason returned by the server', async () => {
        signupMock.mockRejectedValue({
            status: 400,
            data: { error: 'email_not_available', message: 'This email is already registered.' },
        });
        const { container } = render(<Signup />);

        fillAndSubmit(container);

        expect(await screen.findByText('This email is already registered.')).toBeInTheDocument();
    });

    it('shows a generic message when there is no response', async () => {
        signupMock.mockRejectedValue(undefined);
        const { container } = render(<Signup />);

        fillAndSubmit(container);

        expect(await screen.findByText('Failed to create a user')).toBeInTheDocument();
    });

    it('shows success after registration', async () => {
        signupMock.mockResolvedValue({ status: 200 });
        const { container } = render(<Signup />);

        fillAndSubmit(container);

        await waitFor(() => expect(signupMock).toHaveBeenCalledWith({
            username: 'Mary',
            email: 'mary@gmail.com',
            password: 'pass123',
        }));
        expect(await screen.findByText('Successfully created a user')).toBeInTheDocument();
    });
});
