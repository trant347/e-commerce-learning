import axios from 'axios';

import {
    CreateCategoryRequest,
    UpdateCategoryRequest,
} from '../common/interfaces';
import { TaskMasterServices } from './taskMasterServices';

jest.mock('axios', () => ({
    get: jest.fn(),
    post: jest.fn(),
    put: jest.fn(),
}));

const getMock = axios.get as jest.Mock;
const postMock = axios.post as jest.Mock;
const putMock = axios.put as jest.Mock;

describe('TaskMasterServices category administration', () => {
    beforeEach(() => {
        getMock.mockReset();
        postMock.mockReset();
        putMock.mockReset();
        localStorage.setItem('token', 'admin-token');
    });

    test('lists categories through the admin endpoint with bearer authentication', async () => {
        getMock.mockResolvedValue({ data: [] });

        await TaskMasterServices.listAdminCategories();

        expect(getMock).toHaveBeenCalledWith(
            '/products/admin/categories',
            { headers: { 'Authorization': 'Bearer admin-token' } }
        );
    });

    test('creates a category with the complete creation payload', async () => {
        const request: CreateCategoryRequest = {
            id: 'appliance-repair',
            displayName: 'Appliance Repair',
            description: 'Household appliance diagnosis and repair.',
        };
        postMock.mockResolvedValue({ data: request });

        await TaskMasterServices.createCategory(request);

        expect(postMock).toHaveBeenCalledWith(
            '/products/admin/categories',
            request,
            {
                headers: {
                    'Authorization': 'Bearer admin-token',
                    'Content-Type': 'application/json',
                },
            }
        );
    });

    test('updates category metadata without sending an ID', async () => {
        const request: UpdateCategoryRequest = {
            displayName: 'Fine Carpentry',
            description: 'Detailed wood construction and repair.',
        };
        putMock.mockResolvedValue({ data: { id: 'carpentry', ...request } });

        await TaskMasterServices.updateCategory('carpentry', request);

        expect(putMock).toHaveBeenCalledWith(
            '/products/admin/categories/carpentry',
            request,
            {
                headers: {
                    'Authorization': 'Bearer admin-token',
                    'Content-Type': 'application/json',
                },
            }
        );
        expect(request).not.toHaveProperty('id');
    });
});
