import * as React from 'react';
import { Route, Routes, BrowserRouter } from 'react-router-dom';

import PageHeader from '../components/page-header';
import ChatOverlay from '../components/chat-overlay/chat-overlay';

import './App.css';

import UserContext from '../context/userContext';
import ApplicationBadgeProvider from '../context/ApplicationBadgeProvider';

import Auth from '../api/authenticationStorage';

const LoadingComponent = () => <h3>please wait...</h3>;

const AsyncHome = React.lazy(() => import('./Home'));
const AsyncProduct = React.lazy(() => import('./Product'));
const AsyncSignin = React.lazy(() => import('../components/login/login'));
const AsyncRegistration = React.lazy(() => import('../components/signup/signup'));
const AsyncProfile = React.lazy(() => import('../components/profile/profile'));
const AsyncCalendar = React.lazy(() => import('./Booking'));
const AsyncNewTaskMaster = React.lazy(() => import('../components/new-task-master/NewTaskMaster'));
const AsyncApplyForTaskMaster = React.lazy(() => import('../components/apply-for-task-master/ApplyForTaskMaster'));
const AsyncApplicationReview = React.lazy(() => import('../components/application-review/ApplicationReview'));
const AsyncAdminApplicationsList = React.lazy(() => import('../components/admin-applications/AdminApplicationsList'));
const AsyncIncomingBookings = React.lazy(() => import('./IncomingBookings'));

export interface IAppContext {
    username: string
}

export default class App extends React.Component<{}, IAppContext> {

    constructor(props) {
        super(props);
        this.state = {
            username: Auth.getUser()
        }
    }

    setUsername(username) {
        if (!username) {
            Auth.deauthenticateUser();
        }
        this.setState({ username });
    }

    render() {
        return (
            <UserContext.Provider value={{ username: this.state.username, setUsername: this.setUsername.bind(this) }}>
                <ApplicationBadgeProvider>
                    <BrowserRouter>
                        <div className="App">
                            <PageHeader />
                            <React.Suspense fallback={<LoadingComponent />}>
                                <Routes>
                                    <Route path="/" element={<><AsyncHome /><ChatOverlay /></>} />
                                    <Route path="/product/:id" element={<AsyncProduct />} />
                                    <Route path="/register" element={<AsyncRegistration />} />
                                    <Route path="/signin" element={<AsyncSignin />} />
                                    <Route path="/profile" element={<AsyncProfile username={Auth.getUser()} />} />
                                    <Route path="/booking" element={<AsyncCalendar events={[]} />} />
                                    <Route path="/booking/:id" element={<AsyncCalendar events={[]} />} />
                                    <Route path="/admin/new-taskmaster" element={<AsyncNewTaskMaster />} />
                                    <Route path="/apply" element={<AsyncApplyForTaskMaster />} />
                                    <Route path="/admin/applications/:id" element={<AsyncApplicationReview />} />
                                    <Route path="/admin/applications" element={<AsyncAdminApplicationsList />} />
                                    <Route path="/bookings/incoming" element={<AsyncIncomingBookings />} />
                                </Routes>
                            </React.Suspense>
                        </div>
                    </BrowserRouter>
                </ApplicationBadgeProvider>
            </UserContext.Provider>
        );
    }
}
