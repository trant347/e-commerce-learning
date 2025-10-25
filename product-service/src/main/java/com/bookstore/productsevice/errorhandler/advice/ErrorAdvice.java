package com.bookstore.productsevice.errorhandler.advice;


import com.bookstore.productsevice.errorhandler.ErrorDetail;
import com.bookstore.productsevice.errorhandler.ErrorStatus;
import com.bookstore.productsevice.errorhandler.Error;
import com.bookstore.productsevice.errorhandler.LocalizedMessage;
import com.bookstore.productsevice.errorhandler.MessageLocalizer;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.core.annotation.AnnotationUtils;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.util.ReflectionUtils;
import org.springframework.web.bind.annotation.ControllerAdvice;
import org.springframework.web.bind.annotation.ExceptionHandler;

import javax.servlet.http.HttpServletRequest;
import java.lang.reflect.Method;
import java.util.Locale;
import java.util.Optional;

@ControllerAdvice
public class ErrorAdvice {


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

    @ExceptionHandler
    public ResponseEntity<Error> handleError(HttpServletRequest request, Throwable exception) {
        Error errorDetail = buildErrorMessage(request,exception);
        return new ResponseEntity<>(errorDetail, Optional.of(errorDetail.getStatus()).orElse(HttpStatus.INTERNAL_SERVER_ERROR));
    }
}
