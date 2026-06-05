import * as React from 'react';

import styled from 'styled-components';

import './profile.css';

import UserProfileServices from '../../api/userProfileServices';
import Auth from '../../api/authenticationStorage';
import { UserProfile, UserSection, FieldType } from '../../api/user-type';

import Section from './section/section';
import Dialog from '../dialog/dialog';
import profileReducer, { UPDATE, REPLACE_ALL, SET_SUBMIT, StateInterface } from './profileReducer';



export default function({ username }) {    

    if(!username) {
        return <div> You need to login first to view your profile</div>
    }

    const initUserValue = {
        user: null,
        shouldSubmitForm: false
    };

    let [userData, dispatchUserData] = React.useReducer(profileReducer, initUserValue);

    const userProfileService = new UserProfileServices();   

    const [showDeleteConfirm, setShowDeleteConfirm] = React.useState(false);
    const [errorMessage, setErrorMessage] = React.useState<string | null>(null);

    const handleDeleteProfile = async () => {
        setShowDeleteConfirm(false);
        try {
            await userProfileService.deleteUser(username);
            Auth.deauthenticateUser();
            window.location.href = '/';
        } catch (e) {
            setErrorMessage(e.response?.data?.error || 'Failed to delete profile.');
        }
    };
   
    React.useEffect(() => {      

        const submitForm = async (user) => {            
            try {
                await userProfileService.updateUserProfile(user);            
            } catch (e) {
                setErrorMessage(e.response?.data?.message || 'Failed to update profile.');
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
                    <DeleteButton onClick={() => setShowDeleteConfirm(true)}>
                        Delete Profile
                    </DeleteButton>
                </div>
                 
            </div>

            {showDeleteConfirm && (
                <Dialog
                    title="Delete Profile"
                    message="Are you sure you want to delete your profile? This action cannot be undone and all your data will be permanently removed."
                    variant="confirm"
                    confirmLabel="Delete"
                    onConfirm={handleDeleteProfile}
                    onClose={() => setShowDeleteConfirm(false)}
                />
            )}

            {errorMessage && (
                <Dialog
                    title="Error"
                    message={errorMessage}
                    variant="alert"
                    onClose={() => setErrorMessage(null)}
                />
            )}
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

const DeleteButton = styled.button`
    margin-top: 2em;
    padding: 0.6em 1.5em;
    background-color: #d9534f;
    color: white;
    border: none;
    border-radius: 4px;
    cursor: pointer;
    font-size: 1em;

    &:hover {
        background-color: #c9302c;
    }
`;

