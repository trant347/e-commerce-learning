package com.bookstore.productsevice.storage;

import com.bookstore.productsevice.errorhandler.ErrorDetail;
import com.bookstore.productsevice.errorhandler.ErrorStatus;
import com.bookstore.productsevice.errorhandler.SimpleErrorDetail;
import com.bookstore.productsevice.exception.BundleProperties;
import org.springframework.http.HttpStatus;

@ErrorStatus(errorCode = BundleProperties.FILE_NOT_FOUND, bundle = BundleProperties.BUNDLE_NAME, httpStatus = HttpStatus.INTERNAL_SERVER_ERROR,
        errorDetailMethod = "errorDetail" )
public class StorageFileNotFoundException extends RuntimeException {

    private String fileName;
    public StorageFileNotFoundException(String fileName) {
        super();
        this.fileName = fileName;
    }

    public ErrorDetail errorDetail() {
        String defaultMessage = String.format("File %s can not be found",fileName);
        return new SimpleErrorDetail(BundleProperties.FILE_NOT_FOUND, defaultMessage, new String[]{fileName});
    }



}