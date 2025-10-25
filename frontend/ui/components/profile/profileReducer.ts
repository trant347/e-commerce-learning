import { UserSection, UserProfile } from "api/user-type";
import UserProfileServiceImpl from "api/userProfileServices";

export const UPDATE = "UPDATE";
export const REPLACE_ALL = "REPLACE_ALL";
export const SET_SUBMIT = "SET_SUBMIT";

export interface StateInterface  {
    user: UserProfile,
    shouldSubmitForm: boolean
}

const reducer = (state:  StateInterface , action : { type: string, payload: any}) : StateInterface => {
    switch (action.type) {
        case UPDATE :            
            const propertyToBeReplaced = buildProperties(action.payload);

            return {
                user: {
                    ...state.user,
                    ...propertyToBeReplaced
                },
                shouldSubmitForm: state.shouldSubmitForm
            }
        case SET_SUBMIT: 
            return {
                user: state.user,
                shouldSubmitForm: action.payload.shouldSubmitForm
            }
            
        case REPLACE_ALL:
            return action.payload;
        default:
            return state;

    }
}


function buildProperties(section: UserSection) {

    let { header, data, path = "" } = section;

   

    let properties = {};
    let cur = properties;
    if( path != "" ) {
        properties[path] = {};
        cur = properties[path];
    }
    
    data.forEach(field => {
        cur[field.name] = field.value;
    });

    return properties;



}

export default reducer;