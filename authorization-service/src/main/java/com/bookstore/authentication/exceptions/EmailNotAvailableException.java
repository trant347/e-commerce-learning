package com.bookstore.authentication.exceptions;


import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.ResponseStatus;

@ResponseStatus(code = HttpStatus.BAD_REQUEST, reason = "Email is not available")
public class EmailNotAvailableException extends Exception{
    public EmailNotAvailableException(String email) {
        super(email+" is not available");
    }
}
