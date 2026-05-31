import * as React  from 'react';

import './page-header.css';
import './search-box.css';
import './cart-bar.css';
import Login from '../login/login';
import Modal from '../modal/modal';

import { useState, Fragment, useEffect } from "react";


import { Link, useNavigate } from 'react-router-dom';

import UserContext from '../../context/userContext';
import ApplicationBadgeContext from '../../context/applicationBadgeContext';

import { Popup, Menu, Label, Button } from 'semantic-ui-react';

import styled from 'styled-components';

import { NotificationBell } from '../notifications/NotificationBell';
import { useNotifications } from '../../hooks/useNotifications';
import { TaskMasterServices } from '../../api/taskMasterServices';

const StyledNav = styled.nav`
    padding-left: 10%;
    padding-right: 10%;
`;

const StyledCenteredDiv = styled.div`
    display: flex;
    align-items: center;
    width: 20%;
`;

const StyledLinkHovered = styled.a`
    &:hover {
        cursor: pointer;
        text-decoration: underline;
    }
`

export default function(props) {

        let tabs = ["For You", "Top Rated", "New TaskMasters"];

        let navigate = useNavigate();

        let topCategories = ["Plumbing","Cleaning","Electrical", "Tutoring", "Pet Care", "Moving"]

        let [arrowDirection,setArrowDirection] = useState<string>("down");


        let [isLoginPopupVisible, setLoginPopupVisibility] = useState<boolean>(false);

        let { username, setUsername } = React.useContext(UserContext);
        const { unviewedApplicationCount, setUnviewedApplicationCount } = React.useContext(ApplicationBadgeContext);

        // Use the notifications hook
        const { notifications, unreadCount, markAsRead, lastNotification } = useNotifications(username);

        const [isTaskMaster, setIsTaskMaster] = useState<boolean>(false);

        useEffect(() => {
            if (!username) { setIsTaskMaster(false); return; }
            TaskMasterServices.getMyTaskMaster()
                .then(tm => setIsTaskMaster(tm != null))
                .catch(() => setIsTaskMaster(false));
        }, [username]);

        // Increment badge in real-time when a new application arrives via SSE
        useEffect(() => {
            if (lastNotification?.type === 'TASKMASTER_APPLICATION_SUBMITTED') {
                setUnviewedApplicationCount(unviewedApplicationCount + 1);
            }
        }, [lastNotification]);

        const showLoginPopup = function():void {
            setLoginPopupVisibility(true);
        }

        let [language, setLanguage] = React.useState("English");
        const languages = ["English", "Spanish", "Chinese", "French", "German"];
        const ModalWithLogin = Modal(Login);

        return (
            <Fragment>
                <header className="site-header">
                    <StyledNav className="navbar">
                        <ul className="left-nav">                           
                            <li><a className="contact"> <i className="envelope icon"></i> Contact us </a></li>
                            <li><a className="help"> <i className="info circle icon"/>  Help</a></li>
                        </ul>


                        <ul className="right-nav">                           
                            <li> <a className="order_status"> <i className="compass icon"/>Order Status </a></li>
                            <li> <a> <i className="heart outline icon"/> Wishlist </a></li>
                            
                            {/* Notification Bell - always visible */}
                            {username && (
                                <li>
                                    <NotificationBell 
                                        notifications={notifications}
                                        unreadCount={unreadCount}
                                        onMarkAsRead={markAsRead}
                                    />
                                </li>
                            )}
                            
                            <li>

                                {
                                    username == null
                                    ?
                                        <SignInOrJoinPopUp showLoginPopup={showLoginPopup}/>
                                    :
                                    <Popup trigger={<a> <i className="user icon"></i> {username} </a>} hoverable>                                        
                                         <Menu vertical className="borderless" 
                                                style={{
                                                    border: "none",
                                                    boxShadow: "none"
                                                }}
                                            >
                                            <Menu.Item
                                                name='profile'                                             
                                                >
                                                 <StyledLinkHovered onClick={() => navigate('/profile')}> My Profile </StyledLinkHovered>                                       
                                            </Menu.Item>

                                            {isTaskMaster && (
                                                <Menu.Item name='incoming-bookings'>
                                                    <StyledLinkHovered onClick={() => navigate('/bookings/incoming')}>
                                                        <i className="calendar alternate icon" /> Booking Requests
                                                    </StyledLinkHovered>
                                                </Menu.Item>
                                            )}

                                            {username === 'admin' && (
                                                <Menu.Item name='new-taskmaster'>
                                                    <StyledLinkHovered onClick={() => navigate('/admin/new-taskmaster')}>
                                                        <i className="user plus icon" /> Add TaskMaster
                                                    </StyledLinkHovered>
                                                </Menu.Item>
                                            )}

                                            {username === 'admin' && (
                                                <Menu.Item name='applications'>
                                                    <StyledLinkHovered onClick={() => navigate('/admin/applications')}>
                                                        <i className="tasks icon" /> Applications
                                                        {unviewedApplicationCount > 0 && (
                                                            <Label circular color='red' size='mini' style={{ marginLeft: '6px' }}>
                                                                {unviewedApplicationCount}
                                                            </Label>
                                                        )}
                                                    </StyledLinkHovered>
                                                </Menu.Item>
                                            )}

                                            <Menu.Item
                                                name='logout'                                                                                              
                                                >                                               
                                                <StyledLinkHovered 
                                                    onClick={
                                                        () => {
                                                            setUsername(null);
                                                            navigate('/');
                                                        }
                                                    }> 
                                                    Log out 
                                                </StyledLinkHovered>
                                            </Menu.Item>
                                         </Menu>
                                    </Popup>                               
                                }
                            </li>
                        </ul>
                    </StyledNav>
                    <div className="search-area">
                        <StyledCenteredDiv>
                            <Link to="/" className="logo"> <i className="users icon"></i> TaskMaster Hub </Link>
                        </StyledCenteredDiv>
                        <div className="search-form">
                            <span className="search-dropdown">
                            <select>
                                <option>All Services</option>
                                <option>Home Repair</option>
                                <option>Personal Services</option>
                            </select>
                            </span>


                            <input className="search-input" type="text" name="search_input" placeholder="Search for services..."/>
                            <button>Search</button>
                        </div>                        
                    </div>
                    <StyledNav className="cart-bar">
                        <ul style={{paddingLeft:"0px"}}>
                            <li className="categories-dropdown"  onMouseLeave={() => setArrowDirection("down")}>
                                <div onMouseEnter={() => setArrowDirection("up")}>Browse by service <i className={`angle ${arrowDirection} icon`}/></div>

                                        <ul>
                                            <li><a>Popular Services</a></li>
                                            {
                                                topCategories.map((item,index) => {
                                                   return <li key={index}><a> {item}</a></li>
                                                })
                                            }
                                        </ul>
                            </li>
                            <li> Top Rated </li>
                            <li> Near Me </li>
                            <li>
                                {username && username !== 'admin'
                                    ? <Link to="/apply">New TaskMasters</Link>
                                    : <span>New TaskMasters</span>
                                }
                            </li>
                            <li> Best Value </li>
                        </ul>                    
                    </StyledNav>
                </header>


                {
                    isLoginPopupVisible &&  <ModalWithLogin
                        callback={({username}) => {
                            setUsername(username);
                            setLoginPopupVisibility(false);
                            navigate("/");
                        }}
                    />
                }

            </Fragment>
        );

}


function SignInOrJoinPopUp({ showLoginPopup }) : React.JSX.Element {
    return (
        <Popup 
            hoverable
            trigger={<a><i className="user icon"/>Sign In/Join</a>}
            on={["click","hover"]}
            content={
                <div style={{display:"flex", justifyContent:"center", alignItems:"center", flexDirection:"column"}}>
                    <Button style={{marginBottom:"5px"}} onClick={showLoginPopup}> Sign In </Button>
                    <div>
                        New Customer ? 
                        <Link to="/register"  style={{cursor:"pointer", marginLeft:"1em"}}>                           
                            Click here                          
                        </Link>
                    </div>
                </div>
            }
            >
        </Popup>
    );
}