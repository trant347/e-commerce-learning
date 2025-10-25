
import { UserProfile, FieldType, UserSection } from "./user-type";

import axios from 'axios';

export interface LookupService {
    getLookup(groupId:string) : Promise<Lookup[]>,
}

export interface Lookup {
    code: string,
    label: string,
    [key:string]: string
}

export default class LookupServiceImpl implements LookupService {    
    getLookup(groupId: string) : Promise<Lookup[]>{        
        return axios.get(`/user/lookup/${groupId}`).then(res => res.data);                    
    }    
}