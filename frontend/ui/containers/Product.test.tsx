import { act, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

jest.mock('../api/taskMasterServices', () => ({
    TaskMasterServices: {
        getTaskMasterById: jest.fn(),
        listCategoryMetadata: jest.fn(),
    },
}));

import { TaskMasterServices } from '../api/taskMasterServices';
import Product from './Product';

const getTaskMasterByIdMock = TaskMasterServices.getTaskMasterById as jest.Mock;
const listCategoryMetadataMock = TaskMasterServices.listCategoryMetadata as jest.Mock;

describe('Product profile loading', () => {
    beforeEach(() => {
        getTaskMasterByIdMock.mockReset();
        listCategoryMetadataMock.mockReset();
        listCategoryMetadataMock.mockResolvedValue([
            {
                id: 'carpentry',
                displayName: 'Carpentry',
                description: 'Wood construction and repair.',
            },
        ]);
    });

    test('does not render booking controls until the profile has loaded', async () => {
        let resolveTaskMaster: (value: {
            id: string;
            name: string;
            age: number;
            location: string;
            rating: number;
            jobCategories: string[];
            hourlyRateUsd: number;
        }) => void = () => {};
        const taskMasterRequest = new Promise<{
            id: string;
            name: string;
            age: number;
            location: string;
            rating: number;
            jobCategories: string[];
            hourlyRateUsd: number;
        }>(resolve => {
            resolveTaskMaster = resolve;
        });
        getTaskMasterByIdMock.mockReturnValue(taskMasterRequest);

        render(
            <MemoryRouter initialEntries={['/product/taskmaster-1']}>
                <Routes>
                    <Route path="/product/:id" element={<Product />} />
                </Routes>
            </MemoryRouter>
        );

        expect(screen.queryByRole('link', { name: /book now/i })).not.toBeInTheDocument();

        await act(async () => {
            resolveTaskMaster({
                id: 'taskmaster-1',
                name: 'Alice Smith',
                age: 35,
                location: 'Boston, MA',
                rating: 5,
                jobCategories: ['carpentry'],
                hourlyRateUsd: 60,
            });
            await taskMasterRequest;
        });

        expect(await screen.findByRole('link', { name: /book now/i }))
            .toHaveAttribute('href', '/booking/taskmaster-1');
    });
});
