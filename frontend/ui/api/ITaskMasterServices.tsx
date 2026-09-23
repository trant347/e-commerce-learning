import {
    CategoryMetadata,
    CreateCategoryRequest,
    TaskMaster,
    TaskMasterApplication,
    SubmitApplicationRequest,
    UpdateCategoryRequest,
} from '../common/interfaces';


export default interface ITaskMasterServices {

    getAllTaskMasters() : Promise<TaskMaster[]>,

    getTaskMasterById(id: string) : Promise<TaskMaster>,

    getMyTaskMaster(): Promise<TaskMaster | null>;

    getTaskMastersAtPage(pageIndex: number, limit: number): Promise<TaskMaster[]>;

    createTaskMaster(taskMaster: Omit<TaskMaster, 'id'>): Promise<TaskMaster>;

    submitApplication(application: SubmitApplicationRequest): Promise<TaskMasterApplication>;

    listApplications(status?: string): Promise<TaskMasterApplication[]>;

    getApplication(id: string): Promise<TaskMasterApplication>;

    acceptApplication(id: string): Promise<TaskMasterApplication>;

    declineApplication(id: string, reason?: string): Promise<TaskMasterApplication>;

    getUnviewedCount(): Promise<number>;

    markApplicationViewed(id: string): Promise<void>;

    listCategoryMetadata(): Promise<CategoryMetadata[]>;

    listAdminCategories(): Promise<CategoryMetadata[]>;

    createCategory(category: CreateCategoryRequest): Promise<CategoryMetadata>;

    updateCategory(id: string, category: UpdateCategoryRequest): Promise<CategoryMetadata>;
}