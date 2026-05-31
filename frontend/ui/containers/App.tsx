import * as React from 'react';

import {Route, Switch, BrowserRouter} from 'react-router-dom';

import * as loadable from 'react-loadable';

import PageHeader from '../components/page-header';
import ChatOverlay from '../components/chat-overlay/chat-overlay';

// import Home from './Home';
// import Product from './Product';

import './App.css';

import UserContext from '../context/userContext';
import ApplicationBadgeProvider from '../context/ApplicationBadgeProvider';

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

const AsyncNewTaskMaster = loadable({
    loader: () => import('../components/new-task-master/NewTaskMaster'),
    loading: LoadingComponent
});

const AsyncApplyForTaskMaster = loadable({
    loader: () => import('../components/apply-for-task-master/ApplyForTaskMaster'),
    loading: LoadingComponent
});

const AsyncApplicationReview = loadable({
    loader: () => import('../components/application-review/ApplicationReview'),
    loading: LoadingComponent
});

const AsyncAdminApplicationsList = loadable({
    loader: () => import('../components/admin-applications/AdminApplicationsList'),
    loading: LoadingComponent
});

const AsyncIncomingBookings = loadable({
    loader: () => import('./IncomingBookings'),
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
                <ApplicationBadgeProvider>
                <BrowserRouter>
                    <div className="App">
                        <PageHeader/>

                        <Switch>
                            <Route
                                exact
                                path="/"
                                render={(props) => (
                                    <>
                                        <AsyncHome {...props}></AsyncHome>
                                        <ChatOverlay></ChatOverlay>
                                    </>
                                )}
                            ></Route>
                            <Route path="/product/:id" component={AsyncProduct}></Route>
                            <Route path="/register" component={AsyncRegistration}></Route>
                            <Route path="/signin" component={AsyncSignin}></Route>
                            <Route path="/profile" render={ (props) => <AsyncProfile {...props} username={ Auth.getUser() }></AsyncProfile>}></Route>
                            <Route path="/booking/:id?" render={(props) => <AsyncCalendar {...props} events={[]}/> }></Route>
                            <Route path="/admin/new-taskmaster" component={AsyncNewTaskMaster}></Route>
                            <Route path="/apply" component={AsyncApplyForTaskMaster}></Route>
                            <Route path="/admin/applications/:id" component={AsyncApplicationReview}></Route>
                            <Route path="/admin/applications" component={AsyncAdminApplicationsList}></Route>
                            <Route path="/bookings/incoming" component={AsyncIncomingBookings}></Route>
                        </Switch>


                    </div>
                </BrowserRouter>
                </ApplicationBadgeProvider>
            </UserContext.Provider>
        );
    }
}
