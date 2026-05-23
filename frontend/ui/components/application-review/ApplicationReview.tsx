import * as React from 'react';
import { useState, useEffect, useContext } from 'react';
import { useHistory, useParams } from 'react-router-dom';

import UserContext from '../../context/userContext';
import ApplicationBadgeContext from '../../context/applicationBadgeContext';
import { TaskMasterServices } from '../../api/taskMasterServices';
import { TaskMasterApplication } from '../../common/interfaces';

import '../new-task-master/new-task-master.css';
import './application-review.css';

interface RouteParams {
    id: string;
}

export default function ApplicationReview() {
    const { username } = useContext(UserContext);
    const { decrementUnviewedCount } = useContext(ApplicationBadgeContext);
    const history = useHistory();
    const { id } = useParams<RouteParams>();

    const [application, setApplication] = useState<TaskMasterApplication | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [declineReason, setDeclineReason] = useState('');
    const [acting, setActing] = useState(false);
    const [feedback, setFeedback] = useState<{ type: 'success' | 'error'; message: string } | null>(null);

    // Admin-only page
    if (username !== 'admin') {
        history.replace('/');
        return null;
    }

    useEffect(() => {
        TaskMasterServices.getApplication(id)
            .then(data => {
                setApplication(data);
                setLoading(false);
                if (!data.isViewedByAdmin) {
                    TaskMasterServices.markApplicationViewed(id)
                        .then(() => decrementUnviewedCount())
                        .catch(() => {/* silently ignore */});
                }
            })
            .catch(() => {
                setError('Failed to load application.');
                setLoading(false);
            });
    }, [id]);

    const handleAccept = async () => {
        if (!application) return;
        setActing(true);
        setFeedback(null);
        try {
            const updated = await TaskMasterServices.acceptApplication(id);
            setApplication(updated);
            setFeedback({ type: 'success', message: 'Application accepted. TaskMaster profile created.' });
            if (updated.createdTaskMasterId) {
                setTimeout(() => history.push(`/product/${updated.createdTaskMasterId}`), 1500);
            }
        } catch (err: any) {
            const msg = err?.response?.data?.error || err?.response?.data?.message || 'Failed to accept application.';
            setFeedback({ type: 'error', message: msg });
        } finally {
            setActing(false);
        }
    };

    const handleDecline = async () => {
        if (!application) return;
        setActing(true);
        setFeedback(null);
        try {
            const updated = await TaskMasterServices.declineApplication(id, declineReason || undefined);
            setApplication(updated);
            setFeedback({ type: 'success', message: 'Application declined.' });
        } catch (err: any) {
            const msg = err?.response?.data?.error || err?.response?.data?.message || 'Failed to decline application.';
            setFeedback({ type: 'error', message: msg });
        } finally {
            setActing(false);
        }
    };

    if (loading) {
        return <div className="new-taskmaster-page"><h3>Loading application...</h3></div>;
    }

    if (error || !application) {
        return <div className="new-taskmaster-page"><div className="form-feedback error">{error || 'Application not found.'}</div></div>;
    }

    const isPending = application.status === 'PENDING';

    const statusColor: Record<string, string> = {
        PENDING: '#f0a500',
        ACCEPTED: '#21ba45',
        DECLINED: '#db2828',
    };

    return (
        <div className="new-taskmaster-page">
            <div className="review-header">
                <button className="back-btn" onClick={() => history.goBack()}>
                    <i className="arrow left icon" /> Back
                </button>
                <h1><i className="file alternate icon" /> Application Review</h1>
                <span
                    className="status-badge"
                    style={{ backgroundColor: statusColor[application.status] || '#999' }}
                >
                    {application.status}
                </span>
            </div>

            <div className="review-meta">
                <span><strong>Applicant:</strong> {application.applicantUsername}</span>
                <span><strong>Submitted:</strong> {new Date(application.submittedAt).toLocaleString()}</span>
            </div>

            {/* Read-only fields */}
            <div className="user-input-row">
                <label>Full Name</label>
                <input type="text" readOnly value={application.name} />
            </div>

            <div className="user-input-row">
                <label>Age</label>
                <input type="number" readOnly value={application.age} />
            </div>

            <div className="user-input-row">
                <label>Location</label>
                <input type="text" readOnly value={application.location} />
            </div>

            <div className="user-input-row">
                <label>Description</label>
                <textarea readOnly value={application.description} />
            </div>

            <div className="user-input-row">
                <label>Hourly Rate (USD)</label>
                <input type="text" readOnly value={`$${application.hourlyRateUsd}`} />
            </div>

            <div className="user-input-row">
                <label>Categories</label>
                <div className="category-tags">
                    {application.jobCategories.map(cat => (
                        <span key={cat} className="category-tag">{cat}</span>
                    ))}
                </div>
            </div>

            {application.photo && (
                <div className="user-input-row">
                    <label>Photo</label>
                    <img src={application.photo} alt="Applicant" style={{ maxWidth: '180px', borderRadius: '8px' }} />
                </div>
            )}

            {application.declineReason && (
                <div className="user-input-row">
                    <label>Decline Reason</label>
                    <input type="text" readOnly value={application.declineReason} />
                </div>
            )}

            {feedback && (
                <div className={`form-feedback ${feedback.type}`}>{feedback.message}</div>
            )}

            {isPending && (
                <div className="review-actions">
                    <div className="user-input-row" style={{ marginBottom: '0.5rem' }}>
                        <label>Decline Reason <small>(optional)</small></label>
                        <input
                            type="text"
                            value={declineReason}
                            onChange={e => setDeclineReason(e.target.value)}
                            placeholder="Reason for declining (shown to applicant)..."
                        />
                    </div>
                    <div className="action-buttons">
                        <button
                            className="submit-btn accept-btn"
                            onClick={handleAccept}
                            disabled={acting}
                        >
                            {acting ? 'Processing...' : <><i className="check icon" /> Accept</>}
                        </button>
                        <button
                            className="submit-btn decline-btn"
                            onClick={handleDecline}
                            disabled={acting}
                        >
                            {acting ? 'Processing...' : <><i className="times icon" /> Decline</>}
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
}
