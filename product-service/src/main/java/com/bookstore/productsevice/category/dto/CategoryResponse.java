package com.bookstore.productsevice.category.dto;

import com.bookstore.productsevice.model.Category;

public record CategoryResponse(
        String id,
        String displayName,
        String description) {

    public static CategoryResponse from(Category category) {
        return new CategoryResponse(
                category.getId(),
                category.getDisplayName(),
                category.getDescription());
    }
}
