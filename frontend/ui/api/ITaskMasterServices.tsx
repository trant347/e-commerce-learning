import { TaskMaster, TaskMasterApplication, SubmitApplicationRequest } from '../common/interfaces';


export default interface ITaskMasterServices {

    getAllTaskMasters() : Promise<TaskMaster[]>,

    getTaskMasterById(id: string) : Promise<TaskMaster>,

    getTaskMastersAtPage(pageIndex: number, limit: number): Promise<TaskMaster[]>;

    createTaskMaster(taskMaster: Omit<TaskMaster, 'id'>): Promise<TaskMaster>;

    submitApplication(application: SubmitApplicationRequest): Promise<TaskMasterApplication>;

    listApplications(status?: string): Promise<TaskMasterApplication[]>;

    getApplication(id: string): Promise<TaskMasterApplication>;

    acceptApplication(id: string): Promise<TaskMasterApplication>;

    declineApplication(id: string, reason?: string): Promise<TaskMasterApplication>;

    getUnviewedCount(): Promise<number>;

    markApplicationViewed(id: string): Promise<void>;
}