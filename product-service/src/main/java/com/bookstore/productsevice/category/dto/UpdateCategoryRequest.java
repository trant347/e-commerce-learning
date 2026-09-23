package com.bookstore.productsevice.category.dto;

public record UpdateCategoryRequest(
        String displayName,
        String description) {
}
