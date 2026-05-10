import * as React from 'react';
import 'bootstrap/dist/css/bootstrap.css';

import ProductBox from '../components/product-box/product-box';
import ProductPage from '../components/product-page';

import LoginPage from '../components/login/login';

import Modal from '../components/modal/modal';



export default { title: 'Button' };
export const box = () => <ProductBox quantity={1} hourlyRateUsd={75} name={"John Smith"} location={"New York, NY"} rating={4.9} jobCategories={["plumbing"]} ></ProductBox>;
export const page = () => <ProductPage name={"Jane Doe"} hourlyRateUsd={50} description={"Professional cleaner with 5 years of experience."} location={"Austin, TX"} rating={4.7} jobCategories={["cleaning"]} />;
export const login = () =>  <LoginPage></LoginPage>;
export const modalLoginPage =  () => {
        const ModalWithLogin = Modal(LoginPage);
        return <ModalWithLogin></ModalWithLogin>
    };