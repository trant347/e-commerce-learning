import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import ApplicationBadgeContext from '../../context/applicationBadgeContext';
import UserContext from '../../context/userContext';

jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        acceptApplication: jest.fn(),
        declineApplication: jest.fn(),
        getApplication: jest.fn(),
        listCategoryMetadata: jest.fn(),
        markApplicationViewed: jest.fn(),
    },
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import ApplicationReview from './ApplicationReview';

const getApplicationMock = TaskMasterServices.getApplication as jest.Mock;
const listCategoryMetadataMock = TaskMasterServices.listCategoryMetadata as jest.Mock;
const markApplicationViewedMock = TaskMasterServices.markApplicationViewed as jest.Mock;

describe('ApplicationReview category labels', () => {
    beforeEach(() => {
        getApplicationMock.mockReset();
        listCategoryMetadataMock.mockReset();
        markApplicationViewedMock.mockReset();
        markApplicationViewedMock.mockResolvedValue(undefined);
    });

    test('shows catalog display names while retaining category IDs on the application', async () => {
        getApplicationMock.mockResolvedValue({
            id: 'application-1',
            applicantUsername: 'alice',
            name: 'Alice Smith',
            age: 35,
            location: 'Boston, MA',
            description: 'Experienced carpenter.',
            hourlyRateUsd: 60,
            jobCategories: ['carpentry'],
            status: 'PENDING',
            submittedAt: '2026-09-22T12:00:00Z',
            isViewedByAdmin: true,
        });
        listCategoryMetadataMock.mockResolvedValue([
            {
                id: 'carpentry',
                displayName: 'Fine Carpentry',
                description: 'Wood construction and repair.',
            },
        ]);

        render(
            <UserContext.Provider value={{ username: 'admin', setUsername: () => {} } as any}>
                <ApplicationBadgeContext.Provider value={{
                    unviewedApplicationCount: 0,
                    setUnviewedApplicationCount: () => {},
                    decrementUnviewedCount: () => {},
                }}>
                    <MemoryRouter initialEntries={['/admin/applications/application-1']}>
                        <Routes>
                            <Route
                                path="/admin/applications/:id"
                                element={<ApplicationReview />}
                            />
                        </Routes>
                    </MemoryRouter>
                </ApplicationBadgeContext.Provider>
            </UserContext.Provider>
        );

        expect(await screen.findByText('Fine Carpentry')).toBeInTheDocument();
        expect(getApplicationMock).toHaveBeenCalledWith('application-1');
    });
});
