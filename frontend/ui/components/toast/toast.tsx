import * as React from 'react';


import { Message } from 'semantic-ui-react';

import "./toast.css";

export interface ToastProps {

    visible: boolean;

    message: string;

    level: ToastLevel;   

    onClose?: () => void;

    [key: string]: any
}

export enum ToastLevel {

    info = "Info",
    error = "Error",
    success = "Success",
    warning = "Warning"

}

function ToastContainer(props : { message, level, backgroundColor, icon, visible, onClose? }) : JSX.Element {

    return (
        <div className="notification-toast" style={{backgroundColor: `${props.backgroundColor}`}}>
            <div className="notification-image">
                <i className={`${props.icon} icon`}>            
                </i>
            </div>
            <div className="notification-content">
                <p className="notification-title">{props.level}</p>
                <p className="notification-message">{props.message}</p>
            </div>
            <label className="notification-button" onClick={
                () => {                   
                    if(props.onClose)
                        props.onClose();
                }}> X </label>
        </div>
    )
}

export default function withToastContainer(props: ToastProps) : JSX.Element {
   let backgroundColor = "#03c6fc";
   let icon = "info circle";

   switch(props.level) {   
       case ToastLevel.info: 
            backgroundColor = "#03c6fc";
            icon = "info circle";
            break;
         
       case ToastLevel.error:
            backgroundColor = "#fc5303fc";
            icon = "exclamation circle";
            break;
       
       case ToastLevel.success:
            icon = "check circle";
            backgroundColor = "green";
            break;
       
        case ToastLevel.warning:
            icon = "exclamation triangle"
            backgroundColor = "yellow";
            break;       
   }

   return ToastContainer({ 
        message: props.message,
        level: props.level,
        icon,
        backgroundColor,
        visible: props.visible,
        onClose: props.onClose
   });

}
