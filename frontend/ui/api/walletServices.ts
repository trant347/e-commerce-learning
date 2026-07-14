import axios from 'axios';

export interface Wallet {
    userId: string;
    balance: number;
    createdAt: string;
    updatedAt: string;
}

function authHeader() {
    const token = localStorage.getItem('token');
    return token ? { Authorization: `Bearer ${token}` } : {};
}

const BASE = '/payment-service/api/wallet';

/**
 * Client for payment-service's simulated wallet balance (see PAYMENT_SAGA_SPEC.md /
 * WalletSimulationPaymentGateway). Used to show the user's available balance in the header.
 */
export const WalletServices = {
    getBalance(username: string): Promise<Wallet | null> {
        return axios.get(`${BASE}/${encodeURIComponent(username)}`, { headers: authHeader() })
            .then(res => res.data)
            .catch(err => {
                if (err?.response?.status === 404) return null;
                throw err;
            });
    },
};
