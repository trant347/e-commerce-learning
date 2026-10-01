package com.bookstore.productsevice.category.dto;

import com.bookstore.productsevice.model.Category;

import java.util.List;

public record CategoryResponse(
        String id,
        String displayName,
        String description,
        List<String> aliases) {

    public CategoryResponse(String id, String displayName, String description) {
        this(id, displayName, description, List.of());
    }

    public static CategoryResponse from(Category category) {
        return new CategoryResponse(
                category.getId(),
                category.getDisplayName(),
                category.getDescription(),
                List.copyOf(category.getAliases()));
    }
}
