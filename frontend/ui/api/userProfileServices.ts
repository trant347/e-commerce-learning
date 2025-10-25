
import { UserProfile, FieldType, UserSection } from "./user-type";

import Auth from './authenticationStorage';

import axios from 'axios';

export interface UserProfileService {
    getUserProfile(username:string) : Promise<UserProfile>,
    updateUserProfile(user: UserProfile) : Promise<any>
}


export default class UserProfileServiceImpl implements UserProfileService {
    
    getUserProfile(username: string) : Promise<UserProfile>{        
        return axios.get(`/user/${username}`).then(res => res.data);                    
    }

    updateUserProfile(user: UserProfile) : Promise<any> {
        return axios.put(
                    `/user/${user.username}`, 
                    user,
                    {
                        headers:  { Authorization: `Bearer ${Auth.getToken()}` }
                    }
                );
    }
}