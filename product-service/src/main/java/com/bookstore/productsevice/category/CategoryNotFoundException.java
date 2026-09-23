package com.bookstore.productsevice.category;

public class CategoryNotFoundException extends RuntimeException {

    private final String categoryId;

    public CategoryNotFoundException(String categoryId) {
        super("Category '" + categoryId + "' was not found.");
        this.categoryId = categoryId;
    }

    public String getCategoryId() {
        return categoryId;
    }
}
