package com.bookstore.productsevice.errorhandler;

import java.io.Serializable;
import java.util.Locale;


public class LocalizedMessage implements Serializable {

    private transient Locale locale;
    private String message;

    private LocalizedMessage(){}

    public LocalizedMessage(Locale locale, String message) {
        this.locale = locale;
        this.message = message;
    }

    public Locale getLocale() {
        return locale;
    }

    public String getValue() {
        return message;
    }
}
