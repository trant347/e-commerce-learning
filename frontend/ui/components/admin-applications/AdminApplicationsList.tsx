import * as React from 'react';
import { useState, useEffect, useContext } from 'react';
import { useHistory } from 'react-router-dom';

import UserContext from '../../context/userContext';
import { TaskMasterServices } from '../../api/taskMasterServices';
import { TaskMasterApplication } from '../../common/interfaces';

import '../new-task-master/new-task-master.css';
import './admin-applications.css';

type FilterStatus = 'ALL' | 'PENDING' | 'ACCEPTED' | 'DECLINED';

export default function AdminApplicationsList() {
    const { username } = useContext(UserContext);
    const history = useHistory();

    const [applications, setApplications] = useState<TaskMasterApplication[]>([]);
    const [filter, setFilter] = useState<FilterStatus>('PENDING');
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    // Admin-only page
    if (username !== 'admin') {
        history.replace('/');
        return null;
    }

    useEffect(() => {
        setLoading(true);
        setError(null);
        const statusParam = filter === 'ALL' ? undefined : filter;
        TaskMasterServices.listApplications(statusParam)
            .then(data => {
                setApplications(data);
                setLoading(false);
            })
            .catch(() => {
                setError('Failed to load applications.');
                setLoading(false);
            });
    }, [filter]);

    const statusColor: Record<string, string> = {
        PENDING: '#f0a500',
        ACCEPTED: '#21ba45',
        DECLINED: '#db2828',
    };

    return (
        <div className="new-taskmaster-page">
            <h1><i className="tasks icon" /> TaskMaster Applications</h1>

            <div className="filter-tabs">
                {(['PENDING', 'ALL', 'ACCEPTED', 'DECLINED'] as FilterStatus[]).map(s => (
                    <button
                        key={s}
                        className={`filter-tab${filter === s ? ' active' : ''}`}
                        onClick={() => setFilter(s)}
                    >
                        {s === 'ALL' ? 'All' : s.charAt(0) + s.slice(1).toLowerCase()}
                    </button>
                ))}
            </div>

            {loading && <p className="loading-text">Loading...</p>}
            {error && <div className="form-feedback error">{error}</div>}

            {!loading && !error && applications.length === 0 && (
                <div className="empty-state">
                    <i className="inbox icon" style={{ fontSize: '2rem', color: '#bbb' }} />
                    <p>No {filter !== 'ALL' ? filter.toLowerCase() : ''} applications.</p>
                </div>
            )}

            {!loading && !error && applications.length > 0 && (
                <table className="applications-table">
                    <thead>
                        <tr>
                            <th>Applicant</th>
                            <th>Name</th>
                            <th>Location</th>
                            <th>Rate</th>
                            <th>Categories</th>
                            <th>Submitted</th>
                            <th>Status</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {applications.map(app => (
                            <tr key={app.id} className={app.status === 'PENDING' ? 'row-pending' : ''}>
                                <td>{app.applicantUsername}</td>
                                <td>{app.name}</td>
                                <td>{app.location}</td>
                                <td>${app.hourlyRateUsd}/hr</td>
                                <td>
                                    <div className="category-tags" style={{ flexWrap: 'wrap' }}>
                                        {app.jobCategories.slice(0, 3).map(c => (
                                            <span key={c} className="category-tag" style={{ fontSize: '0.75rem' }}>{c}</span>
                                        ))}
                                        {app.jobCategories.length > 3 && (
                                            <span className="category-tag" style={{ fontSize: '0.75rem' }}>+{app.jobCategories.length - 3}</span>
                                        )}
                                    </div>
                                </td>
                                <td style={{ whiteSpace: 'nowrap', fontSize: '0.85rem' }}>
                                    {new Date(app.submittedAt).toLocaleDateString()}
                                </td>
                                <td>
                                    <span
                                        className="status-badge-sm"
                                        style={{ backgroundColor: statusColor[app.status] || '#999' }}
                                    >
                                        {app.status}
                                    </span>
                                </td>
                                <td>
                                    <button
                                        className="review-btn"
                                        onClick={() => history.push(`/admin/applications/${app.id}`)}
                                    >
                                        Review
                                    </button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </div>
    );
}
