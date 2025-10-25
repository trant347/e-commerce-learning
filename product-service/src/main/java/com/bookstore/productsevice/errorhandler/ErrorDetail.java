package com.bookstore.productsevice.errorhandler;

import java.io.Serializable;

public interface ErrorDetail {
    public String errorMessage();
    public String errorCode();
    public Serializable[] params();
}
