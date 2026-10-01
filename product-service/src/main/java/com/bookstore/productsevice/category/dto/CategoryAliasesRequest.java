package com.bookstore.productsevice.category.dto;

import com.bookstore.productsevice.category.CategoryValidationException;
import com.fasterxml.jackson.annotation.JsonSetter;
import com.fasterxml.jackson.databind.JsonNode;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

public abstract class CategoryAliasesRequest {

    private boolean aliasesProvided;
    private JsonNode aliases;

    @JsonSetter("aliases")
    public void setAliases(JsonNode aliases) {
        this.aliasesProvided = true;
        this.aliases = aliases;
    }

    // Omission preserves aliases on updates; an explicit null is invalid.
    public List<String> aliases() {
        if (!aliasesProvided) {
            return null;
        }
        if (aliases == null || !aliases.isArray()) {
            throw invalidAliases();
        }
        List<String> values = new ArrayList<>();
        for (JsonNode alias : aliases) {
            if (!alias.isTextual()) {
                throw invalidAliases();
            }
            values.add(alias.textValue());
        }
        return values;
    }

    private CategoryValidationException invalidAliases() {
        return new CategoryValidationException(
                "invalid_category",
                "Category aliases must be an array of strings.",
                Map.of("aliases", "must be an array of strings"));
    }
}
