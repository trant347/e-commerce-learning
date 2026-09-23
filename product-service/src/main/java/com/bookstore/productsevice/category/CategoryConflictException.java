package com.bookstore.productsevice.category;

public class CategoryConflictException extends RuntimeException {

    private final String errorCode;
    private final String field;

    public CategoryConflictException(String errorCode, String message, String field) {
        super(message);
        this.errorCode = errorCode;
        this.field = field;
    }

    public String getErrorCode() {
        return errorCode;
    }

    public String getField() {
        return field;
    }
}
