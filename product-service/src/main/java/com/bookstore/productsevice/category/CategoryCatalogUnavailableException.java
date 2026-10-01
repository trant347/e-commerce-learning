package com.bookstore.productsevice.category;

public class CategoryCatalogUnavailableException extends RuntimeException {

    public CategoryCatalogUnavailableException(Throwable cause) {
        super("Category catalog is temporarily unavailable.", cause);
    }
}
