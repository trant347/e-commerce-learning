package com.bookstore.productsevice.category;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

public class CategoryValidationException extends RuntimeException {

    private final String errorCode;
    private final Map<String, String> fieldErrors;

    public CategoryValidationException(String errorCode,
                                       String message,
                                       Map<String, String> fieldErrors) {
        super(message);
        this.errorCode = errorCode;
        this.fieldErrors = Collections.unmodifiableMap(new LinkedHashMap<>(fieldErrors));
    }

    public String getErrorCode() {
        return errorCode;
    }

    public Map<String, String> getFieldErrors() {
        return fieldErrors;
    }
}
