package com.bookstore.productsevice.category.dto;

public record CreateCategoryRequest(
        String id,
        String displayName,
        String description) {
}
