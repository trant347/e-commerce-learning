import axios from 'axios';

import { TaskMaster, TaskMasterApplication, SubmitApplicationRequest } from '../common/interfaces';
import ITaskMasterServices from './ITaskMasterServices';


export const TaskMasterServices : ITaskMasterServices  = {

    getAllTaskMasters(): Promise<TaskMaster[]> {
        return axios.get("/products").then(res => res.data);
    },

    getTaskMasterById(id: string): Promise<TaskMaster>  {
        const token = localStorage.getItem('token');
        return axios.get(
            `/products/${id}`,
            { headers: {"Authorization" : `Bearer ${token}`} }
        ).then(res => res.data);
    },

    getMyTaskMaster(): Promise<TaskMaster | null> {
        const token = localStorage.getItem('token');
        return axios.get(
            '/products/me/taskmaster',
            { headers: { 'Authorization': `Bearer ${token}` } }
        ).then(res => res.data)
         .catch(err => {
             if (err?.response?.status === 404) return null;
             throw err;
         });
    },

    getTaskMastersAtPage(pageIndex: number, limit: number): Promise<TaskMaster[]> {
        return axios.get(`/products?page=${pageIndex}&limit=${limit}`).then(res => res.data);
    },

    createTaskMaster(taskMaster: Omit<TaskMaster, 'id'>): Promise<TaskMaster> {
        const token = localStorage.getItem('token');
        return axios.post(
            '/products',
            taskMaster,
            { headers: { 'Authorization': `Bearer ${token}`, 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    submitApplication(application: SubmitApplicationRequest): Promise<TaskMasterApplication> {
        const token = localStorage.getItem('token');
        return axios.post(
            '/products/applications',
            application,
            { headers: { 'Authorization': `Bearer ${token}`, 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    listApplications(status?: string): Promise<TaskMasterApplication[]> {
        const token = localStorage.getItem('token');
        const url = status ? `/products/applications?status=${status}` : '/products/applications';
        return axios.get(url, { headers: { 'Authorization': `Bearer ${token}` } }).then(res => res.data);
    },

    getApplication(id: string): Promise<TaskMasterApplication> {
        const token = localStorage.getItem('token');
        return axios.get(
            `/products/applications/${id}`,
            { headers: { 'Authorization': `Bearer ${token}` } }
        ).then(res => res.data);
    },

    acceptApplication(id: string): Promise<TaskMasterApplication> {
        const token = localStorage.getItem('token');
        return axios.put(
            `/products/applications/${id}/accept`,
            {},
            { headers: { 'Authorization': `Bearer ${token}` } }
        ).then(res => res.data);
    },

    declineApplication(id: string, reason?: string): Promise<TaskMasterApplication> {
        const token = localStorage.getItem('token');
        return axios.put(
            `/products/applications/${id}/decline`,
            reason ? { reason } : {},
            { headers: { 'Authorization': `Bearer ${token}`, 'Content-Type': 'application/json' } }
        ).then(res => res.data);
    },

    getUnviewedCount(): Promise<number> {
        const token = localStorage.getItem('token');
        return axios.get(
            '/products/applications/unviewed-count',
            { headers: { 'Authorization': `Bearer ${token}` } }
        ).then(res => res.data.count);
    },

    markApplicationViewed(id: string): Promise<void> {
        const token = localStorage.getItem('token');
        return axios.put(
            `/products/applications/${id}/view`,
            {},
            { headers: { 'Authorization': `Bearer ${token}` } }
        ).then(() => undefined);
    },
};