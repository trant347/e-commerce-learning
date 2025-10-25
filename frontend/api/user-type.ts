export enum FieldType {
    TEXT = 'text',
    DROPDOWN = 'select',
    CHECKBOX = 'checkbox',
    TEXT_AREA = 'text-area',
    EMAIL='email',
    PASSWORD='password',  
}

export interface UserSection {
    header: string,
    allowMany?: boolean,
    path?: string,
    data: {
      
            label: string,
            value: string,
            name: string,
            type: FieldType        
    }[];
}

export interface UserProfile {
    "userId": string,
    "username": string,
    "firstName": string,
    "lastName": string,
    "gender": string,
    "email": string,
    "phoneNumber": string,
    "address": any,    
}