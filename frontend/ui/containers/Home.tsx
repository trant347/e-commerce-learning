import * as React from 'react';
import { useNavigate } from 'react-router-dom';
import ProductBox from "../components/product-box/product-box";

import {TaskMaster} from "../common/interfaces";
import {TaskMasterServices} from "../api/taskMasterServices";

export const ITEMS_PER_PAGE = 6;

export interface TaskMasterState {
    taskMasters: TaskMaster[], 
    fetchingData: boolean, 
    pageNumber: number,
    hasMore: boolean
}

export interface Action {
    type: string,
    payload: any
}

export default function Home() {

    const navigate = useNavigate();

    const initialStates = {
        taskMasters: [],
        fetchingData: false,
        pageNumber: 0,
        hasMore: true
    };

    const [state, dispatch] = React.useReducer(taskMasterReducer, initialStates);

    // Always-current ref so the IntersectionObserver (created once) can read latest hasMore.
    const hasMoreRef = React.useRef(state.hasMore);
    React.useEffect(() => { hasMoreRef.current = state.hasMore; }, [state.hasMore]);

    React.useEffect(() => {
        try {
            const fetchTaskMasters = async () => {
                let taskMasters = await TaskMasterServices.getTaskMastersAtPage(state.pageNumber - 1, ITEMS_PER_PAGE);
                dispatch({ type: UPDATE_FETCHING_STATUS, payload: false });
                dispatch({ type: UPDATE_TASK_MASTERS, payload: taskMasters });
                if (taskMasters.length < ITEMS_PER_PAGE) {
                    dispatch({ type: SET_HAS_MORE, payload: false });
                }
            }            
            if (state.fetchingData) {
                fetchTaskMasters();      
            }   
        } catch (e) {
            throw Error("Failed to retrieve task masters");
        }
    }, [state.fetchingData, state.pageNumber]);

    const bottomBoundaryRef = React.useRef(null);

    const scrollObserver = React.useCallback(node => {
            new IntersectionObserver(entries => {
                entries.forEach(en => {
                    if (en.isIntersecting && hasMoreRef.current) {
                        dispatch({ type: INCREASE_PAGE_INDEX, payload: 1 });
                        dispatch({ type: UPDATE_FETCHING_STATUS, payload: true });
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
        navigate(`/product/${taskMaster.id}`);
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
export const SET_HAS_MORE = "SET_HAS_MORE";

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
        case SET_HAS_MORE: {
            return {
                ...state,
                hasMore: action.payload
            }
        }
        default:
            return state;
    }
}