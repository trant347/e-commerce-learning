import * as React from 'react';
import {FunctionComponent, useState} from "react";

import './modal.css';

export default function(WrappedComponent : FunctionComponent) : any {
    return (props) => {


       return (
           <div className="modal-bookstore">
               <div className="modal-bookstore-content fade">
                    <WrappedComponent {...props}></WrappedComponent>
                    <span className="close" onClick={props.callback}>&times;</span>
               </div>
            </div>
       )
    }
}