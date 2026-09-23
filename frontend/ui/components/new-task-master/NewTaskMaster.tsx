import * as React from 'react';
import { useState, useContext } from 'react';
import { useNavigate, Navigate } from 'react-router-dom';

import UserContext from '../../context/userContext';
import { TaskMasterServices } from '../../api/taskMasterServices';
import { formatUsLocation, US_STATES, validateUsCity } from '../../common/usStates';
import CategoryMultiSelect from '../category-multi-select/CategoryMultiSelect';

import './new-task-master.css';

interface FormState {
    name: string;
    age: string;
    city: string;
    stateCode: string;
    description: string;
    hourlyRateUsd: string;
    photo: string;
}

export default function NewTaskMaster() {
    const { username } = useContext(UserContext);
    const navigate = useNavigate();

    const [form, setForm] = useState<FormState>({
        name: '',
        age: '',
        city: '',
        stateCode: '',
        description: '',
        hourlyRateUsd: '',
        photo: '',
    });
    const [categories, setCategories] = useState<string[]>([]);
    const [submitting, setSubmitting] = useState(false);
    const [feedback, setFeedback] = useState<{ type: 'success' | 'error'; message: string } | null>(null);

    // Guard: redirect non-admin users away
    if (username !== 'admin') {
        return <Navigate to="/" replace />;
    }

    const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) => {
        setForm({ ...form, [e.target.name]: e.target.value });
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setFeedback(null);

        if (categories.length === 0) {
            setFeedback({ type: 'error', message: 'Please select at least one category.' });
            return;
        }

        const cityError = validateUsCity(form.city);
        if (cityError) {
            setFeedback({ type: 'error', message: cityError });
            return;
        }

        setSubmitting(true);
        try {
            const created = await TaskMasterServices.createTaskMaster({
                name: form.name,
                age: parseInt(form.age, 10),
                location: formatUsLocation(form.city, form.stateCode),
                description: form.description,
                hourlyRateUsd: parseFloat(form.hourlyRateUsd),
                photo: form.photo || null,
                rating: 0,
                jobCategories: categories,
            });

            navigate(`/product/${created.id}`);
        } catch (err: any) {
            const message = err?.response?.data?.message || 'Failed to create TaskMaster. Please try again.';
            setFeedback({ type: 'error', message });
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="new-taskmaster-page">
            <h1><i className="user plus icon" /> Add New TaskMaster</h1>

            <form onSubmit={handleSubmit}>
                <div className="user-input-row">
                    <label>Full Name *</label>
                    <input type="text" name="name" value={form.name} onChange={handleChange} required placeholder="e.g. John Smith" />
                </div>

                <div className="user-input-row">
                    <label>Age *</label>
                    <input type="number" name="age" value={form.age} onChange={handleChange} required min={18} max={99} placeholder="e.g. 30" />
                </div>

                <div className="user-input-row">
                    <label htmlFor="taskmaster-city">City *</label>
                    <input id="taskmaster-city" type="text" name="city" value={form.city} onChange={handleChange} required placeholder="e.g. Chicago" />
                </div>

                <div className="user-input-row">
                    <label htmlFor="taskmaster-state">State *</label>
                    <select id="taskmaster-state" name="stateCode" value={form.stateCode} onChange={handleChange} required>
                        <option value="">Select a state</option>
                        {US_STATES.map(([code, name]) => (
                            <option key={code} value={code}>{name} ({code})</option>
                        ))}
                    </select>
                </div>

                <div className="user-input-row">
                    <label>Description</label>
                    <textarea name="description" value={form.description} onChange={handleChange} placeholder="Brief bio or service description..." />
                </div>

                <CategoryMultiSelect
                    selectedIds={categories}
                    onChange={setCategories}
                    disabled={submitting}
                />

                <div className="user-input-row">
                    <label>Hourly Rate (USD) *</label>
                    <input type="number" name="hourlyRateUsd" value={form.hourlyRateUsd} onChange={handleChange} required min={1} step="0.01" placeholder="e.g. 45.00" />
                </div>

                <div className="user-input-row">
                    <label>Photo URL <small>(optional)</small></label>
                    <input type="text" name="photo" value={form.photo} onChange={handleChange} placeholder="https://example.com/photo.jpg" />
                </div>

                {feedback && (
                    <div className={`form-feedback ${feedback.type}`}>{feedback.message}</div>
                )}

                <button
                    type="submit"
                    className="submit-btn"
                    disabled={submitting || categories.length === 0}
                >
                    {submitting ? 'Saving...' : 'Create TaskMaster'}
                </button>
            </form>
        </div>
    );
}
