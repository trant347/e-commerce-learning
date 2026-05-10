import * as React from 'react';
import ProductBox from "../components/product-box/product-box";

import {TaskMaster} from "../common/interfaces";
import {TaskMasterServices} from "../api/taskMasterServices";

export const ITEMS_PER_PAGE = 6;

export interface TaskMasterState {
    taskMasters: TaskMaster[], 
    fetchingData: boolean, 
    pageNumber: number
}

export interface Action {
    type: string,
    payload: any
}

export default function Home({ history }) {


    const initialStates = {
        taskMasters: [],
        fetchingData: false,
        pageNumber: 0
    };

    const [state, dispatch] = React.useReducer<TaskMasterState, Action>(taskMasterReducer, initialStates);  

    React.useEffect(() => {
        try {
            const fetchTaskMasters = async () => {
                let taskMasters = await TaskMasterServices.getTaskMastersAtPage(state.pageNumber - 1, ITEMS_PER_PAGE);
                dispatch({ type: UPDATE_FETCHING_STATUS, payload: false});
                dispatch({ type: UPDATE_TASK_MASTERS, payload: taskMasters });              
            }            
            if(state.fetchingData) {
                fetchTaskMasters();      
            }   
        }catch (e) {
            throw Error("Failed to retrieve task masters");
        }
    }, [state]);

    const bottomBoundaryRef = React.useRef(null);

    const scrollObserver = React.useCallback(node => {
            new IntersectionObserver(entries => {
                entries.forEach(en => {
                    if(en.isIntersecting) {
                        dispatch({ type: INCREASE_PAGE_INDEX, payload: 1 });
                        dispatch({ type: UPDATE_FETCHING_STATUS, payload: true});
                    }
                })
            }).observe(node);
        },
        [dispatch],
    );

    React.useEffect(() => {
        if(bottomBoundaryRef.current) {
            scrollObserver(bottomBoundaryRef.current);
        }
    }, [bottomBoundaryRef, scrollObserver]);

    const openTaskMaster = (taskMaster: TaskMaster) => {
        history.push(`/product/${taskMaster.id}`);
    }  

    

    return (
        <>
            <div className="App-content ui">
            {
                state.taskMasters.map(
                    (taskMaster, index) => <ProductBox key={index} {...taskMaster} quantity={1} openProduct={() => openTaskMaster(taskMaster)}/>
                )
            }
            </div>
            <div id='page-bottom-boundary' style={{ border: '1px solid red' }} ref={bottomBoundaryRef}></div>
        </>
    );
    
}

export const UPDATE_TASK_MASTERS = "UPDATE_TASK_MASTERS";
export const UPDATE_FETCHING_STATUS = "UPDATE_FETCHING_STATUS";
export const INCREASE_PAGE_INDEX = "INCREASE_PAGE_INDEX";

export const taskMasterReducer = ( state : TaskMasterState, action: Action ) => {
    switch (action.type) {
        case "UPDATE_TASK_MASTERS": {
            return {
                ...state,
                taskMasters: [
                    ...state.taskMasters,
                    ...action.payload
                ]
            }
        }
        case "UPDATE_FETCHING_STATUS": {
            return {
                ...state,
                fetchingData: action.payload
            }
        }
        case INCREASE_PAGE_INDEX: {
            return {
                ...state,
                pageNumber: state.pageNumber + action.payload
            }
        }
        default:
            return state;
    }
}