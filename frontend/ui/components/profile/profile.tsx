import * as React from 'react';

import styled from 'styled-components';

import './profile.css';

import UserProfileServices from '../../api/userProfileServices';
import { UserProfile, UserSection, FieldType } from '../../api/user-type';

import Section from './section/section';
import profileReducer, { UPDATE, REPLACE_ALL, SET_SUBMIT, StateInterface } from './profileReducer';



export default function({ username }) {    

    if(!username) {
        return <div> You need to login first to view your profile</div>
    }

    const initUserValue = {
        user: null,
        shouldSubmitForm: false
    };

    let [userData, dispatchUserData] = React.useReducer<StateInterface, {type: string, payload: any}>(profileReducer,initUserValue);

    const userProfileService = new UserProfileServices();   
   
    React.useEffect(() => {      

        const submitForm = async (user) => {            
            try {
                await userProfileService.updateUserProfile(user);            
            } catch (e) {
                alert(e.response?.data?.message);
                throw e;
            } finally {
                dispatchUserData({ 
                    type: SET_SUBMIT,
                    payload: {
                        shouldSubmitForm: false
                    }
                })
            }      
        }

        if(userData.shouldSubmitForm) {
            submitForm(userData.user);   
        } else {
            userProfileService.getUserProfile(username)
                            .then(
                                user => dispatchUserData( {
                                    type: REPLACE_ALL,
                                    payload: {
                                        user,
                                        shouldSubmitForm: false
                                    }
                                })
                            );                           
        }      

    },[username, userData.shouldSubmitForm]);


    if(!userData.user) {
        return <div> Loading User Profile </div>
    }

    let sections = createUserSections(userData.user);

    return (
        <div className="profile">
            <BreadCrumb> 
                <li> <StyledLink> My Account </StyledLink> </li>
                <li> <StyledLink> Personal Settings </StyledLink></li>
            </BreadCrumb>

            <div className="profile__main">
                <div className="profile__list"> 
                    <h2> Account Info </h2>
                    <ul>
                        <li> <StyledLink> My Personal Settings </StyledLink> </li>
                    </ul>
                </div>                
                   
                <div className="profile__settings">
                    {
                         sections.map(
                            (userProfile, index) => (
                                    <Section userProfile={userProfile} key={`${userProfile.header}.${index}`} 
                                            onSave={ 
                                                (values) => {
                                                    dispatchUserData({
                                                        type: UPDATE, 
                                                        payload: values
                                                    });
                                                    dispatchUserData({
                                                        type: SET_SUBMIT,
                                                        payload: {
                                                            shouldSubmitForm: true
                                                        }
                                                    })                                                   
                                                }

                                            }/>                                
                            ) 
                        )
                    }
                </div>
                 
            </div>
        </div>
    );
}


function createUserSections(user : UserProfile) : UserSection[] {

    let basicInfoSection = createBasicInfoSection(user);
    let contactInfoSection = createContactSection(user);
    let addressSection = createAddressSection(user.address || {});    

    return [ basicInfoSection, contactInfoSection, addressSection];

}

function createAddressSection(address: any) : UserSection {
    return {
        header: "Address",
        path: "address",
        data: [
            {
                label: "Address Line",
                value: address.addressLine,
                name: "addressLine",
                type: FieldType.TEXT
            },
            {
                label: "Country",
                value: address.country,
                name: "country",
                type: FieldType.DROPDOWN
            },
            {
                label: "Postal Code",
                value: address.postalCode,
                name: "postalCode",
                type: FieldType.TEXT
            },
            {
                label: "City",
                value: address.city,
                name: "city",
                type: FieldType.TEXT
            },
        ]
    }

}

function createContactSection(user: UserProfile) : UserSection {
    return {
        header: "Contact Info",
        data: [
            {
                label: "Email",
                value: user.email,
                name: "email",
                type: FieldType.EMAIL
            },
            {
                label: "Phone Number",
                value: user.phoneNumber,
                name: "phoneNumber",
                type: FieldType.TEXT
            },          
        ]
    }
}

function createBasicInfoSection(user: UserProfile) : UserSection {
    return {
        header: "Personal Information",
        data: [
            {
                label: "First Name",
                value: user.firstName,
                name: "firstName",
                type: FieldType.TEXT    
            },
            {
                label: "Last Name",
                value: user.lastName,
                name: "lastName",
                type: FieldType.TEXT    
            },
            {
                label: "Gender",
                value: user.gender,
                name: "gender",
                type: FieldType.DROPDOWN    
            },          
        ]
    }
}


const BreadCrumb = styled.ul`
    & {
        list-styled: none;
        padding-inline-start: 0px;
    }  
    
    & > li {
        display: inline;                      
    }

    & > li+li:before {
        content: "\003e";
        padding: 0.5em;  
    }

    & > li a {
        color: #0275d8;
        text-decoration: none;
    }

    & > li a:hover {
        color: #01447e;
        text-decoration: underline;
    }      
`;

const StyledLink = styled.a`
    cursor: pointer
`;