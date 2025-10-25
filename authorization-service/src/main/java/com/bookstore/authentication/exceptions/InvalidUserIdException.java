package com.bookstore.authentication.exceptions;

import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.ResponseStatus;

@ResponseStatus(code = HttpStatus.BAD_REQUEST, reason = "No user is associated with this id")
public class InvalidUserIdException extends Exception {
    public InvalidUserIdException(String s) {
        super(s);
    }
}
