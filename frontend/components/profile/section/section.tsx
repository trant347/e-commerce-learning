import * as React from 'react';

import styled from 'styled-components';

import { Formik, Field, FieldArray } from 'formik';
import { FieldType, UserSection } from '../../../api/user-type';
import Dropdown from '../../dropdown/dropdown';
import { Lookup } from 'api/lookupServices';
import { Input, Button } from 'semantic-ui-react';



const StyledHeader = styled.h2 `
    background-color: #f7f7f2;    
    display: flex;
    padding-top: 10px;
    padding-bottom: 10px;
`;

const StyledHeaderEditText = styled.a`
    font-size: 0.5em;
    flex: 1;
    text-align: right;
    cursor: pointer;
    margin-right: 1em;
`
const StyledHeaderLabel = styled.label`
    flex: 9;
    text-align: left;
`

const StyledLink = styled.a`
    cursor: pointer
`;

const StyledButtonSection = styled.div`
    display: flex;
    
    & > button {
        margin-right: 1em;
        min-width: 6em;
    }
`;

export interface SectionProps {
    userProfile: UserSection,
    onSave ?(values : UserSection): void
}


export default function({ userProfile, onSave } : SectionProps) : JSX.Element {

    let [editMode, setEditMode] = React.useState(false);
    
    return (

        <Formik
            initialValues = {userProfile}
            onSubmit={ values => {
                onSave(values);
                setEditMode(false);
            }}
            enableReinitialize= { true }
        >
            {
                ({ handleSubmit, values, resetForm, setFieldValue }) => (
                    <form className="profile__section" 
                          onSubmit={
                              (event) => {
                                  event.preventDefault();
                                  handleSubmit();
                              }
                          }> 
                        <StyledHeader>
                            <StyledHeaderLabel> {userProfile.header} </StyledHeaderLabel>
                            <StyledHeaderEditText onClick={() => setEditMode(!editMode)}> Edit </StyledHeaderEditText>
                        </StyledHeader>

                        <FieldArray name="data">
                            {
                                ({}) => {
                                    return values.data.map(
                                        ({ value, name, label, type}, index) => {
                                        return (
                                                <div className="profile__field" key={`${values.header}.${index}`}>
                                                    <div className="profile__fieldname"> {label} </div>
                                                    {                                                                                  
                                                         getFieldComponent({ value, name, label, type},`data.${index}.value`, setFieldValue, editMode) 
                                                    }
                                                </div>
                                        );
                                        }
                                    )
                                }
                            }

                        </FieldArray>
                        
                        {
                            editMode && <StyledButtonSection>
                                            <Button type="Submit"> Save </Button>
                                            <Button 
                                                onClick= { 
                                                    () => {
                                                        resetForm();
                                                        setEditMode(false);
                                                    }
                                                }
                                            > 
                                                Cancel 
                                            </Button>
                                        </StyledButtonSection>
                        }
                    </form>
                )
            }
        </Formik>       
    );

}

function getFieldComponent({ value, name, label, type}, fieldName, setFieldValue, editMode) : JSX.Element {

    switch(type) {
        case FieldType.DROPDOWN:
            if(!editMode) {
                return <div className="profile__fieldvalue"> {(value as Lookup)?.label} </div>
            }
            return <Dropdown 
                        isAsync={true} 
                        groupId={name} 
                        value={value?.code} 
                        onChange={
                            option => setFieldValue(fieldName,option)                            
                        }
                        />
                            
        default:
            if(!editMode) {
                return <div className="profile__fieldvalue"> {value} </div>
            }
            return (
                <Input 
                    value={value} 
                    onChange= { 
                        (e, { value }) => {
                            setFieldValue(fieldName, value);
                        }
                    }
                    type='text'
                />
            );
    }

}