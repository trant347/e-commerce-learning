import axios from 'axios';
import { FormEvent, useContext, useEffect, useRef, useState } from 'react';
import { Navigate } from 'react-router-dom';

import { TaskMasterServices } from '../../api/taskMasterServices';
import { CategoryMetadata } from '../../common/interfaces';
import UserContext from '../../context/userContext';

import '../new-task-master/new-task-master.css';
import './admin-categories.css';

interface CategoryForm {
    id: string;
    displayName: string;
    description: string;
}

const EMPTY_FORM: CategoryForm = {
    id: '',
    displayName: '',
    description: '',
};

function getCategoryErrorMessage(error: unknown, fallback: string): string {
    if (axios.isAxiosError<{ message?: string }>(error)) {
        return error.response?.data?.message || fallback;
    }
    return fallback;
}

export default function AdminCategories() {
    const { username } = useContext(UserContext);
    const loadRequestId = useRef(0);
    const [categories, setCategories] = useState<CategoryMetadata[]>([]);
    const [loading, setLoading] = useState(true);
    const [submitting, setSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [success, setSuccess] = useState<string | null>(null);
    const [createForm, setCreateForm] = useState<CategoryForm>(EMPTY_FORM);
    const [editingId, setEditingId] = useState<string | null>(null);
    const [editForm, setEditForm] = useState<Omit<CategoryForm, 'id'>>({
        displayName: '',
        description: '',
    });

    const loadCategories = async () => {
        const requestId = ++loadRequestId.current;
        setLoading(true);
        setError(null);
        try {
            const loadedCategories = await TaskMasterServices.listAdminCategories();
            if (requestId === loadRequestId.current) {
                setCategories(loadedCategories);
            }
        } catch {
            if (requestId === loadRequestId.current) {
                setError('Failed to load categories.');
            }
        } finally {
            if (requestId === loadRequestId.current) {
                setLoading(false);
            }
        }
    };

    useEffect(() => {
        if (username === 'admin') {
            void loadCategories();
        }
        return () => {
            loadRequestId.current++;
        };
    }, [username]);

    if (username !== 'admin') {
        return <Navigate to="/" replace />;
    }

    const handleCreate = async (event: FormEvent) => {
        event.preventDefault();
        setSubmitting(true);
        setError(null);
        setSuccess(null);
        try {
            await TaskMasterServices.createCategory(createForm);
            setCreateForm(EMPTY_FORM);
            setSuccess('Category created.');
            await loadCategories();
        } catch (error: unknown) {
            setError(getCategoryErrorMessage(error, 'Failed to create category.'));
        } finally {
            setSubmitting(false);
        }
    };

    const beginEdit = (category: CategoryMetadata) => {
        setEditingId(category.id);
        setEditForm({
            displayName: category.displayName,
            description: category.description,
        });
        setError(null);
        setSuccess(null);
    };

    const handleUpdate = async (event: FormEvent) => {
        event.preventDefault();
        if (!editingId) return;

        setSubmitting(true);
        setError(null);
        setSuccess(null);
        try {
            await TaskMasterServices.updateCategory(editingId, editForm);
            setEditingId(null);
            setSuccess('Category updated.');
            await loadCategories();
        } catch (error: unknown) {
            setError(getCategoryErrorMessage(error, 'Failed to update category.'));
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="new-taskmaster-page admin-categories-page">
            <h1><i className="tags icon" /> Manage Categories</h1>
            <p className="category-page-intro">
                Categories created here become the canonical services available to TaskMasters.
                Category IDs cannot be changed after creation.
            </p>

            <section className="category-panel">
                <h2>Create Category</h2>
                <form className="category-create-form" onSubmit={handleCreate}>
                    <div className="user-input-row">
                        <label htmlFor="category-id">Canonical ID *</label>
                        <input
                            id="category-id"
                            value={createForm.id}
                            onChange={event => setCreateForm({
                                ...createForm,
                                id: event.target.value,
                            })}
                            required
                            placeholder="e.g. appliance-repair"
                        />
                        <small>Lowercase letters, numbers, and hyphens only.</small>
                    </div>
                    <div className="user-input-row">
                        <label htmlFor="category-display-name">Display Name *</label>
                        <input
                            id="category-display-name"
                            value={createForm.displayName}
                            onChange={event => setCreateForm({
                                ...createForm,
                                displayName: event.target.value,
                            })}
                            required
                            placeholder="e.g. Appliance Repair"
                        />
                    </div>
                    <div className="user-input-row">
                        <label htmlFor="category-description">Description *</label>
                        <textarea
                            id="category-description"
                            value={createForm.description}
                            onChange={event => setCreateForm({
                                ...createForm,
                                description: event.target.value,
                            })}
                            required
                            maxLength={500}
                            placeholder="Describe the work included in this category."
                        />
                    </div>
                    <button className="submit-btn" type="submit" disabled={submitting}>
                        {submitting ? 'Saving...' : 'Create Category'}
                    </button>
                </form>
            </section>

            {error && <div className="form-feedback error">{error}</div>}
            {success && <div className="form-feedback success">{success}</div>}

            <section className="category-panel">
                <h2>Category Catalog</h2>
                {loading && <p className="loading-text">Loading...</p>}

                {!loading && categories.length === 0 && !error && (
                    <div className="empty-state">
                        <i className="tags icon" />
                        <p>No categories have been created.</p>
                    </div>
                )}

                {!loading && categories.length > 0 && (
                    <div className="category-table-wrapper">
                        <table className="categories-table">
                            <thead>
                                <tr>
                                    <th>ID</th>
                                    <th>Display Name</th>
                                    <th>Description</th>
                                    <th></th>
                                </tr>
                            </thead>
                            <tbody>
                                {categories.map(category => (
                                    <tr key={category.id}>
                                        <td><code>{category.id}</code></td>
                                        {editingId === category.id ? (
                                            <>
                                                <td colSpan={2}>
                                                    <form
                                                        id={`edit-category-${category.id}`}
                                                        className="category-edit-form"
                                                        onSubmit={handleUpdate}
                                                    >
                                                        <label htmlFor={`edit-name-${category.id}`}>
                                                            Display Name
                                                        </label>
                                                        <input
                                                            id={`edit-name-${category.id}`}
                                                            value={editForm.displayName}
                                                            onChange={event => setEditForm({
                                                                ...editForm,
                                                                displayName: event.target.value,
                                                            })}
                                                            required
                                                        />
                                                        <label htmlFor={`edit-description-${category.id}`}>
                                                            Description
                                                        </label>
                                                        <textarea
                                                            id={`edit-description-${category.id}`}
                                                            value={editForm.description}
                                                            onChange={event => setEditForm({
                                                                ...editForm,
                                                                description: event.target.value,
                                                            })}
                                                            required
                                                            maxLength={500}
                                                        />
                                                    </form>
                                                </td>
                                                <td className="category-actions">
                                                    <button
                                                        className="review-btn"
                                                        type="submit"
                                                        form={`edit-category-${category.id}`}
                                                        disabled={submitting}
                                                    >
                                                        Save
                                                    </button>
                                                    <button
                                                        className="category-cancel-btn"
                                                        type="button"
                                                        onClick={() => setEditingId(null)}
                                                        disabled={submitting}
                                                    >
                                                        Cancel
                                                    </button>
                                                </td>
                                            </>
                                        ) : (
                                            <>
                                                <td>{category.displayName}</td>
                                                <td>{category.description}</td>
                                                <td className="category-actions">
                                                    <button
                                                        className="review-btn"
                                                        type="button"
                                                        onClick={() => beginEdit(category)}
                                                    >
                                                        Edit
                                                    </button>
                                                </td>
                                            </>
                                        )}
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                )}
            </section>
        </div>
    );
}
