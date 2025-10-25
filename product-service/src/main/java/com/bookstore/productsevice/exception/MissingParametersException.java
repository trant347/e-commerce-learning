package com.bookstore.productsevice.exception;

import com.bookstore.productsevice.errorhandler.ErrorDetail;
import com.bookstore.productsevice.errorhandler.ErrorStatus;
import com.bookstore.productsevice.errorhandler.SimpleErrorDetail;
import org.springframework.http.HttpStatus;

@ErrorStatus(errorCode = BundleProperties.VALIDATION_FAIL, bundle = BundleProperties.BUNDLE_NAME, httpStatus = HttpStatus.BAD_REQUEST,
        errorDetailMethod = "errorDetail"
)
public class MissingParametersException extends Exception {

    private String[] params;
    public MissingParametersException(String[] params){
        super();
        this.params = params;
    }

    public ErrorDetail errorDetail(){
       String missingParams = String.join(",",params);
       String defaultMessage = String.format("Missing params %s",missingParams);
       return new SimpleErrorDetail(BundleProperties.MISSING_PARAM_EXCEPTION, defaultMessage,new String[] {missingParams});
    }
}
