package com.bookstore.authentication.exceptions;

import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.ResponseStatus;

@ResponseStatus(code = HttpStatus.BAD_REQUEST, reason = "Username is not available")
public class UsernameNotAvailableException extends Exception {
    public UsernameNotAvailableException(String username) {
        super(username+" is already taken. Please select another one");
    }
}
