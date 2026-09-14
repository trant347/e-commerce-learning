package com.bookstore.productsevice.location;

public class LocationValidationException extends IllegalArgumentException {

    public LocationValidationException(String message) {
        super(message);
    }
}
