import axios, {AxiosPromise} from 'axios';

const AuthServices : AuthServicesInterface = {
    login: ({username, password}) => {
        return axios.post("/user/authenticate", {username, password});
    },
    signup: ({email, username, password}) => {
        return axios.post("/user/register",{email, username, password} )
                    .then(resp => resp)
                    .catch(error => Promise.reject(error.response));
    }
}

export default AuthServices;

export interface AuthServicesInterface {
    login({username, password}:{username:string, password:string}) : AxiosPromise<void>,
    signup({email, username, password}:{email:string, username:string, password:string}) : AxiosPromise<void>
}