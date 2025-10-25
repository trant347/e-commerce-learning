import * as React from 'react';

import { Formik, Field, ErrorMessage, Form } from 'formik';
import { Button, Message } from 'semantic-ui-react';

import './signup.css';

import styled from 'styled-components';

import withToasterContainer, { ToastLevel } from '../toast/toast';

import { validateEmail, validatePassword } from './validate';

import AuthServices from '../../api/authServices';

export interface User {

    username: string;
    email: string;
    password: string;

}

const FormAny = Form as any;

const StyledInputDiv = styled.div`
        display: flex;
        justify-content:center;

        label {
            flex: 1
        }

        .input {
            flex: 2
        }
    `;

const initialUserValue : User = {
    email: "",
    password: "",
    username: ""
}    



const initialStatusValue = {
    level: ToastLevel.info,
    message: "", 
    visible: false
};

export default function() : JSX.Element {

    let [status , setStatus] = React.useState(initialStatusValue);

    return (   
        <>     
            <Formik
                initialValues={initialUserValue}
                onSubmit= { 
                        async (values, { setSubmitting }) => {  
                            try{
                                await register(values);        
                                setStatus({
                                        level: ToastLevel.success,
                                        message: "Successfully created a user",
                                        visible: true
                                });                                                                
                            } catch(e) {
                                setStatus({
                                    level: ToastLevel.error,
                                    message: e.data.message || "Failed to create a user",
                                    visible: true
                                });      
                            }
                            setSubmitting(false);
                        }
                }
                >
                {
                    ({isSubmitting, handleSubmit}) => (
                        <FormAny onSubmit={ (event) => {                         
                                handleSubmit(event);
                                event.preventDefault();
                            }}
                            className="form-signup">
                            <h1> Create account </h1>
                            <div className = "field-section">
                                <StyledInputDiv>
                                    <label htmlFor ="username"> Your username</label>                        
                                    <Field type="text" name="username" className="input"/>                           
                                </StyledInputDiv>
                            </div>
                            <div className = "field-section">
                                <StyledInputDiv>
                                    <label htmlFor ="email">  Email</label>     
                                    <Field type="email" name="email" validate={validateEmail} className="input"/>    
                                </StyledInputDiv>
                                <ErrorMessage name="email" component="div" className="error-message"/>                
                            </div>
                            <div className = "field-section">
                                <StyledInputDiv>
                                    <label htmlFor ="password">  Password</label>                            
                                    <Field type="password" name="password" validate={validatePassword} className="input"/>                           
                                </StyledInputDiv>
                                <ErrorMessage name="password" component="div" className="error-message"/>
                            </div>
                        
                            <Button type="Submit" disabled={isSubmitting}> Submit </Button>                      
                        </FormAny>
                    )
                }           
            </Formik>       
            {
                status.visible && withToasterContainer({
                    visible: true,
                    message: status.message,
                    level: status.level,
                    onClose: () => {
                        setStatus(initialStatusValue);
                    }
                })
            }
        </>
    )
}

function register(user : User) {
    return AuthServices.signup(user);
}