package com.bookstore.authentication.exceptions;

import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;

import java.util.Map;

/**
 * Returns a machine-readable code and a user-facing reason for registration
 * validation failures. Spring's default error body omits the reason, so clients
 * could not tell a taken username from a taken email.
 */
@RestControllerAdvice
public class RegistrationErrorAdvice {

    @ExceptionHandler(EmailNotAvailableException.class)
    public ResponseEntity<Map<String, String>> emailNotAvailable() {
        return badRequest("email_not_available", "This email is already registered.");
    }

    @ExceptionHandler(UsernameNotAvailableException.class)
    public ResponseEntity<Map<String, String>> usernameNotAvailable() {
        return badRequest("username_not_available", "This username is already taken.");
    }

    @ExceptionHandler(InvalidEmailException.class)
    public ResponseEntity<Map<String, String>> invalidEmail() {
        return badRequest("invalid_email", "Email does not have a valid format.");
    }

    private static ResponseEntity<Map<String, String>> badRequest(String error, String message) {
        return ResponseEntity.status(HttpStatus.BAD_REQUEST)
                .body(Map.of("error", error, "message", message));
    }
}
