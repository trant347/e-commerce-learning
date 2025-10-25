import * as React from 'react';
import { useState } from 'react';

import {Message} from 'semantic-ui-react';

import '../user-form.css';
import './login.css';

import AuthServices from '../../api/authServices';

import AuthStorage from '../../api/authenticationStorage';

export default function(props) {

    let [credentials, setCredentials] = useState({
        username: "",
        password: ""
    });

    let [errorMessage, setErrorMessage] = useState(null);


    const handleSubmit = async (event) => {
       event.preventDefault();
       try {
           const jwt = await AuthServices.login(credentials);

           AuthStorage.authenticateUser(jwt.data as any, credentials.username);

           props.callback && props.callback(credentials);
       } catch (e) {
            setErrorMessage("Wrong username or password");
       }

    };

    const handleChange = (event) => {
        setCredentials({...credentials,[event.target.name]:event.target.value});
    }

    return (

        <form className="login" onSubmit={handleSubmit}>
            <h1>Sign in</h1>
            <div className="user-input-row"> <label>Username </label> <input type="text" name="username" value={credentials.username} onChange={handleChange}/></div>
            <div className="user-input-row"> <label>Password </label> <input type="password" name="password" value={credentials.password} onChange={handleChange}/> </div>
            {
                errorMessage && <Message color='red'>{errorMessage}</Message>
            }
            <input type="submit" value="Submit"/>
        </form>
    );
}