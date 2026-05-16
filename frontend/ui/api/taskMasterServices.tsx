import axios from 'axios';

import { TaskMaster } from '../common/interfaces';
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
    }
};