import * as React from 'react';
import { useState, useContext } from 'react';
import { useNavigate, Navigate } from 'react-router-dom';

import UserContext from '../../context/userContext';
import { TaskMasterServices } from '../../api/taskMasterServices';

import './new-task-master.css';

interface FormState {
    name: string;
    age: string;
    location: string;
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
        location: '',
        description: '',
        hourlyRateUsd: '',
        photo: '',
    });

    const [categories, setCategories] = useState<string[]>([]);
    const [categoryInput, setCategoryInput] = useState('');
    const [submitting, setSubmitting] = useState(false);
    const [feedback, setFeedback] = useState<{ type: 'success' | 'error'; message: string } | null>(null);

    // Guard: redirect non-admin users away
    if (username !== 'admin') {
        return <Navigate to="/" replace />;
    }

    const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
        setForm({ ...form, [e.target.name]: e.target.value });
    };

    const addCategory = () => {
        const incoming = categoryInput
            .split(',')
            .map(c => c.trim().toLowerCase())
            .filter(c => c.length > 0);
        const merged = Array.from(new Set([...categories, ...incoming]));
        setCategories(merged);
        setCategoryInput('');
    };

    const handleCategoryKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            addCategory();
        }
    };

    const removeCategory = (cat: string) => {
        setCategories(categories.filter(c => c !== cat));
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setFeedback(null);

        if (categories.length === 0) {
            setFeedback({ type: 'error', message: 'Please add at least one category.' });
            return;
        }

        setSubmitting(true);
        try {
            const created = await TaskMasterServices.createTaskMaster({
                name: form.name,
                age: parseInt(form.age, 10),
                location: form.location,
                description: form.description,
                hourlyRateUsd: parseFloat(form.hourlyRateUsd),
                photo: form.photo || null,
                rating: 0,
                jobCategories: categories,
            });

            navigate(`/product/${created.id}`);
        } catch (err) {
            setFeedback({ type: 'error', message: 'Failed to create TaskMaster. Please try again.' });
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
                    <label>Location *</label>
                    <input type="text" name="location" value={form.location} onChange={handleChange} required placeholder="e.g. New York, NY" />
                </div>

                <div className="user-input-row">
                    <label>Description</label>
                    <textarea name="description" value={form.description} onChange={handleChange} placeholder="Brief bio or service description..." />
                </div>

                <div className="user-input-row">
                    <label>Categories * <small>(press Enter or click + to add)</small></label>
                    <div className="category-input-row">
                        <input
                            type="text"
                            value={categoryInput}
                            onChange={e => setCategoryInput(e.target.value)}
                            onKeyDown={handleCategoryKeyDown}
                            placeholder="e.g. tutoring"
                        />
                        <button type="button" onClick={addCategory}>+ Add</button>
                    </div>
                    {categories.length > 0 && (
                        <div className="category-tags">
                            {categories.map(cat => (
                                <span key={cat} className="category-tag">
                                    {cat}
                                    <button type="button" onClick={() => removeCategory(cat)} aria-label={`Remove ${cat}`}>×</button>
                                </span>
                            ))}
                        </div>
                    )}
                </div>

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

                <button type="submit" className="submit-btn" disabled={submitting}>
                    {submitting ? 'Saving...' : 'Create TaskMaster'}
                </button>
            </form>
        </div>
    );
}
