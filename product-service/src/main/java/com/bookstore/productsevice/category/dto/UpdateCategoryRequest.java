package com.bookstore.productsevice.category.dto;

import com.fasterxml.jackson.annotation.JsonCreator;
import com.fasterxml.jackson.annotation.JsonProperty;

public final class UpdateCategoryRequest extends CategoryAliasesRequest {

    private final String displayName;
    private final String description;

    @JsonCreator
    public UpdateCategoryRequest(
            @JsonProperty("displayName") String displayName,
            @JsonProperty("description") String description) {
        this.displayName = displayName;
        this.description = description;
    }

    public String displayName() {
        return displayName;
    }

    public String description() {
        return description;
    }
}
