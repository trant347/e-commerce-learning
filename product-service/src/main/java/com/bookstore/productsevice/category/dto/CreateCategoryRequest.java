package com.bookstore.productsevice.category.dto;

import com.fasterxml.jackson.annotation.JsonCreator;
import com.fasterxml.jackson.annotation.JsonProperty;

public final class CreateCategoryRequest extends CategoryAliasesRequest {

    private final String id;
    private final String displayName;
    private final String description;

    @JsonCreator
    public CreateCategoryRequest(
            @JsonProperty("id") String id,
            @JsonProperty("displayName") String displayName,
            @JsonProperty("description") String description) {
        this.id = id;
        this.displayName = displayName;
        this.description = description;
    }

    public String id() {
        return id;
    }

    public String displayName() {
        return displayName;
    }

    public String description() {
        return description;
    }
}
