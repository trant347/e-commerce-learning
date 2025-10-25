package com.bookstore.authentication.exceptions;

import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.ResponseStatus;

@ResponseStatus(code = HttpStatus.BAD_REQUEST, reason = "Email does not have valid format")
public class InvalidEmailException extends Exception {
    public InvalidEmailException(String email) {
        super(email+ " does not have valid format");
    }
}
