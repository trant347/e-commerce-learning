import * as React from 'react';
import 'bootstrap/dist/css/bootstrap.css';

import ProductBox from '../components/product-box/product-box';
import ProductPage from '../components/product-page';

import LoginPage from '../components/login/login';

import Modal from '../components/modal/modal';



export default { title: 'Button' };
export const box = () => <ProductBox quantity={1} priceUsd={1000} name={"Story Book"} ></ProductBox>;
export const page = () =>  <ProductPage name={"Vintage Typewriter"} priceUsd={20} description={"This typewriter looks good in your living room."} />;
export const login = () =>  <LoginPage></LoginPage>;
export const modalLoginPage =  () => {
        const ModalWithLogin = Modal(LoginPage);
        return <ModalWithLogin></ModalWithLogin>
    };