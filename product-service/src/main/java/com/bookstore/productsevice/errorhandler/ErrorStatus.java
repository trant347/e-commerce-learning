package com.bookstore.productsevice.errorhandler;


import org.springframework.http.HttpStatus;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

@Target( {ElementType.TYPE} )
@Retention(RetentionPolicy.RUNTIME)
public @interface ErrorStatus {

    String errorCode();

    String errorSummary() default "";

    HttpStatus httpStatus();

    String errorDetailMethod() default "";

    String bundle() default "";

}
