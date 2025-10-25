package com.bookstore.productsevice.exception;


import com.bookstore.productsevice.errorhandler.ErrorDetail;
import com.bookstore.productsevice.errorhandler.ErrorStatus;
import com.bookstore.productsevice.errorhandler.SimpleErrorDetail;
import org.springframework.http.HttpStatus;

@ErrorStatus(errorCode = BundleProperties.VALIDATION_FAIL, bundle = BundleProperties.BUNDLE_NAME, httpStatus = HttpStatus.BAD_REQUEST,
        errorDetailMethod = "errorDetail"
)
public class ItemNotFoundException extends  RuntimeException{

    private String param;
    public ItemNotFoundException(String param){
        super();
        this.param = param;
    }

    public ErrorDetail errorDetail(){
        String defaultMessage = String.format("The item with id %s can not be found.",param);
        return new SimpleErrorDetail(BundleProperties.ITEM_NOT_FOUND, defaultMessage,new String[] {param});
    }
}
