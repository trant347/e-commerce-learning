import * as React from 'react';

import {Route, Switch, BrowserRouter} from 'react-router-dom';

import * as loadable from 'react-loadable';

import PageHeader from '../components/page-header';

// import Home from './Home';
// import Product from './Product';

import './App.css';

import UserContext from '../context/userContext';

import Auth from '../api/authenticationStorage';


const LoadingComponent = () => <h3>please wait...</h3>;

const AsyncHome = loadable({
    loader: () => import('./Home'),
    loading: LoadingComponent
});

const AsyncProduct = loadable({
    loader: () => import('./Product'),
    loading: LoadingComponent
});

const AsyncSignin = loadable({
    loader: () => import('../components/login/login'),
    loading: LoadingComponent
});

const AsyncRegistration = loadable({
    loader: () => import('../components/signup/signup'),
    loading: LoadingComponent
});

const AsyncProfile = loadable({
    loader: () => import('../components/profile/profile'),
    loading: LoadingComponent
});

const AsyncCalendar = loadable({
    loader: () => import('./Booking'),
    loading: LoadingComponent
});

export interface IAppContext {
    username: string
}

export default class App extends React.Component<{},IAppContext>{

    constructor(props) {
        super(props);
        this.state = {
            username: Auth.getUser()
        }
    }

    setUsername(username) {
        if(!username) {
            Auth.deauthenticateUser();
        }
        this.setState({
            username
        })
    }

    render() {



        return (
            <UserContext.Provider value={{username: this.state.username,setUsername: this.setUsername.bind(this)}}>
                <BrowserRouter>
                    <div className="App">
                        <PageHeader/>

                        <Switch>
                            <Route exact path="/" component={AsyncHome}></Route>
                            <Route path="/product/:id" component={AsyncProduct}></Route>
                            <Route path="/register" component={AsyncRegistration}></Route>
                            <Route path="/signin" component={AsyncSignin}></Route>
                            <Route path="/profile" render={ (props) => <AsyncProfile {...props} username={ Auth.getUser() }></AsyncProfile>}></Route>
                            <Route path="/booking" render={(props) => <AsyncCalendar {...props} events={[]}/> }></Route>
                        </Switch>


                    </div>
                </BrowserRouter>
            </UserContext.Provider>
        );
    }
}
