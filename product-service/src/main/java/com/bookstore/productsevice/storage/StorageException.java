package com.bookstore.productsevice.storage;

import com.bookstore.productsevice.errorhandler.ErrorDetail;
import com.bookstore.productsevice.errorhandler.ErrorStatus;
import com.bookstore.productsevice.errorhandler.SimpleErrorDetail;
import com.bookstore.productsevice.exception.BundleProperties;
import org.springframework.http.HttpStatus;

@ErrorStatus(errorCode = BundleProperties.UNABLE_TO_READ_FILE, bundle = BundleProperties.BUNDLE_NAME, httpStatus = HttpStatus.INTERNAL_SERVER_ERROR,
        errorDetailMethod = "errorDetail" )
public class StorageException extends RuntimeException {
    public StorageException(String message) {
        super(message);
    }

    public StorageException(String message, Throwable e) {
        super(message, e);
    }

    public ErrorDetail errorDetail(){
        return new SimpleErrorDetail(BundleProperties.UNABLE_TO_READ_FILE, getMessage(),null);
    }
}
