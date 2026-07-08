package com.bookstore.productsevice.storage;

/**
 * Thrown when an uploaded file fails validation (too large, empty, or not an
 * accepted type) before it is ever written to storage. Handled explicitly in
 * ErrorAdvice to return a 400 with a plain, user-facing message.
 */
public class InvalidFileUploadException extends RuntimeException {

    public InvalidFileUploadException(String message) {
        super(message);
    }
}
