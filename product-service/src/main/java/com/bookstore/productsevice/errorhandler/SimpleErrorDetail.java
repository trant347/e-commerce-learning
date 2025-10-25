package com.bookstore.productsevice.errorhandler;

import java.io.Serializable;
import java.text.MessageFormat;

public class SimpleErrorDetail implements ErrorDetail {

    private String defaultMessage;
    private Serializable[] params;
    private String errorCode;

    public SimpleErrorDetail(String errorCode, String defaultMessage, Serializable[] params) {
        this.defaultMessage = defaultMessage;
        this.params = params;
        this.errorCode = errorCode;
    }
    @Override
    public String errorMessage() {
        return MessageFormat.format(this.defaultMessage,params);
    }

    @Override
    public String errorCode() {
        return this.errorCode;
    }

    @Override
    public Serializable[] params(){
        return params;
    }
}
