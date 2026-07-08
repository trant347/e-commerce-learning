package com.bookstore.productsevice.errorhandler.advice;


import com.bookstore.productsevice.errorhandler.ErrorDetail;
import com.bookstore.productsevice.errorhandler.ErrorStatus;
import com.bookstore.productsevice.errorhandler.Error;
import com.bookstore.productsevice.errorhandler.LocalizedMessage;
import com.bookstore.productsevice.errorhandler.MessageLocalizer;
import com.bookstore.productsevice.storage.InvalidFileUploadException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.core.annotation.AnnotationUtils;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.util.ReflectionUtils;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.annotation.ControllerAdvice;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.multipart.MaxUploadSizeExceededException;

import jakarta.servlet.http.HttpServletRequest;
import java.lang.reflect.Method;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.stream.Collectors;

@ControllerAdvice
public class ErrorAdvice {

    private static final Logger log = LoggerFactory.getLogger(ErrorAdvice.class);

    @Autowired
    private MessageLocalizer messageLocalizer;

    Error buildErrorMessage(HttpServletRequest request, Throwable exception) {
        ErrorStatus errorStatus = AnnotationUtils.findAnnotation(exception.getClass(), ErrorStatus.class);

        Locale locale = request.getLocale();
        Object errorDetail =  null;

        if(errorStatus != null && !errorStatus.errorDetailMethod().isEmpty()) {

            Method errorDetailMethod = ReflectionUtils
                    .findMethod(exception.getClass(), errorStatus.errorDetailMethod());
            errorDetail = ReflectionUtils.invokeMethod(errorDetailMethod, exception);
        } else {
            return new Error.Builder().withErrorSummary("Internal errors. Please check the logs").withStatus(HttpStatus.INTERNAL_SERVER_ERROR).build();
        }

        LocalizedMessage localizedErrorSummary  = messageLocalizer.getLocalized(request.getLocale(), errorStatus.bundle(), errorStatus.errorCode(),errorStatus.errorSummary());
        LocalizedMessage localizedErrorDetail = null;
        if( errorDetail != null && errorDetail instanceof ErrorDetail) {
            localizedErrorDetail  = messageLocalizer.getLocalized(request.getLocale(), errorStatus.bundle(), ((ErrorDetail) errorDetail).errorCode(), ((ErrorDetail) errorDetail).errorMessage(),((ErrorDetail) errorDetail).params());
        }

        return new Error.Builder().withErrorDetail(localizedErrorDetail).withErrorCode(errorStatus.errorCode()).withErrorSummary(errorStatus.errorSummary())
                    .withStatus(errorStatus.httpStatus()).withLocalizedErrorSummary(localizedErrorSummary).build();
    }

    @ExceptionHandler(MethodArgumentNotValidException.class)
    public ResponseEntity<Map<String, Object>> handleValidationError(MethodArgumentNotValidException ex) {
        Map<String, String> fieldErrors = ex.getBindingResult().getFieldErrors().stream()
                .collect(Collectors.toMap(
                        fe -> fe.getField(),
                        fe -> fe.getDefaultMessage() == null ? "invalid" : fe.getDefaultMessage(),
                        (a, b) -> a));
        Map<String, Object> body = Map.of(
                "error", "Validation failed",
                "fieldErrors", fieldErrors);
        return new ResponseEntity<>(body, HttpStatus.BAD_REQUEST);
    }

    @ExceptionHandler(InvalidFileUploadException.class)
    public ResponseEntity<Map<String, Object>> handleInvalidFileUpload(InvalidFileUploadException ex) {
        Map<String, Object> body = Map.of("error", ex.getMessage());
        return new ResponseEntity<>(body, HttpStatus.BAD_REQUEST);
    }

    @ExceptionHandler(MaxUploadSizeExceededException.class)
    public ResponseEntity<Map<String, Object>> handleMaxUploadSizeExceeded(MaxUploadSizeExceededException ex) {
        Map<String, Object> body = Map.of("error", "Uploaded file exceeds the maximum allowed size of 2MB");
        return new ResponseEntity<>(body, HttpStatus.BAD_REQUEST);
    }

    @ExceptionHandler
    public ResponseEntity<Error> handleError(HttpServletRequest request, Throwable exception) {
        log.error("Unhandled exception while processing {} {}", request.getMethod(), request.getRequestURI(), exception);
        Error errorDetail = buildErrorMessage(request,exception);
        return new ResponseEntity<>(errorDetail, Optional.of(errorDetail.getStatus()).orElse(HttpStatus.INTERNAL_SERVER_ERROR));
    }
}
