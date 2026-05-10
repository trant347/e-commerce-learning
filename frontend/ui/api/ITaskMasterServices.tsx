import { TaskMaster } from '../common/interfaces';


export default interface ITaskMasterServices {

    getAllTaskMasters() : Promise<TaskMaster[]>,

    getTaskMasterById(id: string) : Promise<TaskMaster>,

    getTaskMastersAtPage(pageIndex: number, limit: number): Promise<TaskMaster[]>;
}