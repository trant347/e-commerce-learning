import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import UserContext from '../../context/userContext';

jest.mock('../../api/taskMasterServices', () => ({
    TaskMasterServices: {
        listAdminCategories: jest.fn(),
        createCategory: jest.fn(),
        updateCategory: jest.fn(),
    },
}));

import { TaskMasterServices } from '../../api/taskMasterServices';
import AdminCategories from './AdminCategories';

const listCategoriesMock = TaskMasterServices.listAdminCategories as jest.Mock;
const createCategoryMock = TaskMasterServices.createCategory as jest.Mock;
const updateCategoryMock = TaskMasterServices.updateCategory as jest.Mock;

const carpentry = {
    id: 'carpentry',
    displayName: 'Carpentry',
    description: 'Wood construction and repair.',
};

function renderPage(username: string | null = 'admin') {
    return render(
        <UserContext.Provider value={{ username, setUsername: () => {} } as any}>
            <MemoryRouter initialEntries={['/admin/categories']}>
                <Routes>
                    <Route path="/admin/categories" element={<AdminCategories />} />
                    <Route path="/" element={<div>Home page</div>} />
                </Routes>
            </MemoryRouter>
        </UserContext.Provider>
    );
}

describe('AdminCategories', () => {
    beforeEach(() => {
        listCategoriesMock.mockReset();
        createCategoryMock.mockReset();
        updateCategoryMock.mockReset();
    });

    test('redirects non-admin users without loading the catalog', async () => {
        renderPage('alice');

        expect(await screen.findByText('Home page')).toBeInTheDocument();
        expect(listCategoriesMock).not.toHaveBeenCalled();
    });

    test('loads and displays the canonical category catalog', async () => {
        listCategoriesMock.mockResolvedValue([{ ...carpentry, aliases: ['woodworking', 'cabinet repair'] }]);

        renderPage();

        expect(await screen.findByText('carpentry')).toBeInTheDocument();
        expect(screen.getByText('Carpentry')).toBeInTheDocument();
        expect(screen.getByText('Wood construction and repair.')).toBeInTheDocument();
        expect(screen.getByText('woodworking, cabinet repair')).toBeInTheDocument();
    });

    test('shows an explicit empty state', async () => {
        listCategoriesMock.mockResolvedValue([]);

        renderPage();

        expect(await screen.findByText(/no categories have been created/i))
            .toBeInTheDocument();
    });

    test('shows a catalog loading failure', async () => {
        listCategoriesMock.mockRejectedValue(new Error('network failure'));

        renderPage();

        expect(await screen.findByText('Failed to load categories.')).toBeInTheDocument();
    });

    test('creates a category and refreshes the catalog', async () => {
        listCategoriesMock
            .mockResolvedValueOnce([])
            .mockResolvedValueOnce([{
                id: 'appliance-repair',
                displayName: 'Appliance Repair',
                description: 'Household appliance diagnosis and repair.',
            }]);
        createCategoryMock.mockResolvedValue({
            id: 'appliance-repair',
            displayName: 'Appliance Repair',
            description: 'Household appliance diagnosis and repair.',
            aliases: [],
        });

        renderPage();
        await screen.findByText(/no categories have been created/i);

        fireEvent.change(screen.getByLabelText(/canonical id/i), {
            target: { value: 'appliance-repair' },
        });
        fireEvent.change(screen.getByLabelText(/^display name/i), {
            target: { value: 'Appliance Repair' },
        });
        fireEvent.change(screen.getByLabelText(/^description/i), {
            target: { value: 'Household appliance diagnosis and repair.' },
        });
        fireEvent.click(screen.getByRole('button', { name: /create category/i }));

        await waitFor(() => {
            expect(createCategoryMock).toHaveBeenCalledWith({
                id: 'appliance-repair',
                displayName: 'Appliance Repair',
                description: 'Household appliance diagnosis and repair.',
                aliases: [],
            });
        });
        expect(await screen.findByText('Category created.')).toBeInTheDocument();
        expect(listCategoriesMock).toHaveBeenCalledTimes(2);
    });

    test('ignores an older catalog load after the post-create refresh completes', async () => {
        let resolveInitialLoad: (categories: typeof carpentry[]) => void = () => {};
        const initialLoad = new Promise<typeof carpentry[]>(resolve => {
            resolveInitialLoad = resolve;
        });
        const newCategory = {
            id: 'appliance-repair',
            displayName: 'Appliance Repair',
            description: 'Household appliance diagnosis and repair.',
        };
        listCategoriesMock
            .mockReturnValueOnce(initialLoad)
            .mockResolvedValueOnce([newCategory]);
        createCategoryMock.mockResolvedValue(newCategory);

        renderPage();

        fireEvent.change(screen.getByLabelText(/canonical id/i), {
            target: { value: newCategory.id },
        });
        fireEvent.change(screen.getByLabelText(/^display name/i), {
            target: { value: newCategory.displayName },
        });
        fireEvent.change(screen.getByLabelText(/^description/i), {
            target: { value: newCategory.description },
        });
        fireEvent.click(screen.getByRole('button', { name: /create category/i }));

        expect(await screen.findByText(newCategory.id)).toBeInTheDocument();

        resolveInitialLoad([]);

        await waitFor(() => {
            expect(screen.getByText(newCategory.id)).toBeInTheDocument();
        });
    });

    test('edits category metadata without changing its ID', async () => {
        listCategoriesMock
            .mockResolvedValueOnce([carpentry])
            .mockResolvedValueOnce([{
                ...carpentry,
                displayName: 'Fine Carpentry',
            }]);
        updateCategoryMock.mockResolvedValue({
            ...carpentry,
            displayName: 'Fine Carpentry',
        });

        renderPage();
        await screen.findByText('carpentry');
        fireEvent.click(screen.getByRole('button', { name: /edit/i }));
        fireEvent.change(screen.getByLabelText('Display Name'), {
            target: { value: 'Fine Carpentry' },
        });
        fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => {
            expect(updateCategoryMock).toHaveBeenCalledWith('carpentry', {
                displayName: 'Fine Carpentry',
                description: 'Wood construction and repair.',
                aliases: [],
            });
        });
        expect(await screen.findByText('Category updated.')).toBeInTheDocument();
    });

    test('shows the server duplicate-category message', async () => {
        listCategoriesMock.mockResolvedValue([]);
        createCategoryMock.mockRejectedValue({
            isAxiosError: true,
            response: {
                data: {
                    message: "Category 'carpentry' already exists.",
                },
            },
        });

        renderPage();
        await screen.findByText(/no categories have been created/i);

        fireEvent.change(screen.getByLabelText(/canonical id/i), {
            target: { value: 'carpentry' },
        });
        fireEvent.change(screen.getByLabelText(/^display name/i), {
            target: { value: 'Carpentry' },
        });
        fireEvent.change(screen.getByLabelText(/^description/i), {
            target: { value: 'Wood construction and repair.' },
        });
        fireEvent.click(screen.getByRole('button', { name: /create category/i }));

        expect(await screen.findByText("Category 'carpentry' already exists."))
            .toBeInTheDocument();
    });

    test('creates aliases from separate lines without discarding punctuation or negation', async () => {
        listCategoriesMock.mockResolvedValue([]);
        createCategoryMock.mockResolvedValue(carpentry);
        renderPage();
        await screen.findByText(/no categories have been created/i);
        fireEvent.change(screen.getByLabelText(/canonical id/i), { target: { value: 'carpentry' } });
        fireEvent.change(screen.getByLabelText(/^display name/i), { target: { value: 'Carpentry' } });
        fireEvent.change(screen.getByLabelText(/^description/i), { target: { value: carpentry.description } });
        fireEvent.change(screen.getByLabelText('Aliases'), {
            target: { value: ' wooden cupboard repair \n\nnot assembly!\n' },
        });
        fireEvent.click(screen.getByRole('button', { name: /create category/i }));

        await waitFor(() => expect(createCategoryMock).toHaveBeenCalledWith({
            ...carpentry,
            aliases: ['wooden cupboard repair', 'not assembly!'],
        }));
        expect(screen.getByLabelText('Aliases')).toHaveValue('');
    });

    test.each([
        { name: 'replaces', value: ' cabinet repair \nwood construction', expected: ['cabinet repair', 'wood construction'] },
        { name: 'clears', value: '', expected: [] },
    ])('$name aliases on save', async ({ value, expected }) => {
        listCategoriesMock.mockResolvedValue([{ ...carpentry, aliases: ['woodworking'] }]);
        updateCategoryMock.mockResolvedValue(carpentry);
        renderPage();
        await screen.findByText('carpentry');
        fireEvent.click(screen.getByRole('button', { name: /edit/i }));
        const aliases = within(screen.getByRole('row', { name: /carpentry/i })).getByLabelText('Aliases');
        expect(aliases).toHaveValue('woodworking');
        fireEvent.change(aliases, { target: { value } });
        fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(updateCategoryMock).toHaveBeenCalledWith('carpentry', {
            displayName: carpentry.displayName,
            description: carpentry.description,
            aliases: expected,
        }));
    });

    test('preserves alias edits after a validation failure', async () => {
        listCategoriesMock.mockResolvedValue([{ ...carpentry, aliases: ['woodworking'] }]);
        updateCategoryMock.mockRejectedValue({
            isAxiosError: true,
            response: { data: { message: 'Invalid category aliases: must not contain duplicate normalized aliases.' } },
        });
        renderPage();
        await screen.findByText('carpentry');
        fireEvent.click(screen.getByRole('button', { name: /edit/i }));
        const aliases = within(screen.getByRole('row', { name: /carpentry/i })).getByLabelText('Aliases');
        fireEvent.change(aliases, { target: { value: 'woodworking\nWOODWORKING' } });
        fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

        expect(await screen.findByText(/must not contain duplicate normalized aliases/i)).toBeInTheDocument();
        expect(aliases).toHaveValue('woodworking\nWOODWORKING');
        expect(screen.getByRole('button', { name: /^save$/i })).toBeEnabled();
    });
});
