package com.bookstore.productsevice.errorhandler;

import org.springframework.http.HttpStatus;

import java.io.Serializable;

public class Error implements Serializable {

    private HttpStatus status;
    private String errorCode;
    private String errorSummary;
    private Object errorDetail;
    private LocalizedMessage localizedErrorSummary;

    private Error(){

    }

    private Error(Builder builder){
        this.errorCode = builder.errorCode;
        this.errorDetail = builder.errorDetail;
        this.errorSummary = builder.errorSummary;
        this.localizedErrorSummary = builder.localizedErrorSummary;
        this.status = builder.status;
    }

    public static class Builder {

        private HttpStatus status;
        private String errorCode;
        private String errorSummary;
        private Object errorDetail;
        private LocalizedMessage localizedErrorSummary;


        public Builder withErrorCode(String errorCode) {
            this.errorCode = errorCode;
            return this;

        }

        public Builder withErrorSummary(String errorSummary) {
            this.errorSummary = errorSummary;
            return this;

        }

        public Builder withLocalizedErrorSummary(LocalizedMessage localizedErrorSummary) {
            this.localizedErrorSummary = localizedErrorSummary;
            return this;

        }

        public Builder withErrorDetail(Object errorDetail) {
            this.errorDetail = errorDetail;
            return this;
        }

        public Builder withStatus(HttpStatus httpStatus) {
            this.status = httpStatus;
            return this;
        }

        public Error build() {
            return new Error(this);
        }

    }

    public HttpStatus getStatus() {
        return status;
    }

    public void setStatus(HttpStatus status) {
        this.status = status;
    }

    public String getErrorCode() {
        return errorCode;
    }

    public void setErrorCode(String errorCode) {
        this.errorCode = errorCode;
    }

    public String getErrorSummary() {
        return errorSummary;
    }

    public void setErrorSummary(String errorSummary) {
        this.errorSummary = errorSummary;
    }

    public Object getErrorDetail() {
        return errorDetail;
    }

    public void setErrorDetail(Object errorDetail) {
        this.errorDetail = errorDetail;
    }

    public LocalizedMessage getLocalizedErrorSummary() {
        return localizedErrorSummary;
    }

    public void setLocalizedErrorSummary(LocalizedMessage localizedErrorSummary) {
        this.localizedErrorSummary = localizedErrorSummary;
    }
}
