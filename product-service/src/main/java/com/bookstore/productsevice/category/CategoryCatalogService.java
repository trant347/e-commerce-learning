package com.bookstore.productsevice.category;

import com.bookstore.productsevice.category.dto.CategoryResponse;
import com.fasterxml.jackson.annotation.JsonPropertyOrder;
import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.springframework.stereotype.Service;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Comparator;
import java.util.HexFormat;
import java.util.List;

@Service
public class CategoryCatalogService {

    private final CategoryService categoryService;
    // Catalog hashes must not depend on application-wide Jackson formatting settings.
    private final ObjectMapper canonicalMapper = new ObjectMapper();

    public CategoryCatalogService(CategoryService categoryService) {
        this.categoryService = categoryService;
    }

    public CatalogSnapshot getCatalog() {
        List<CategoryResponse> categories = categoryService.getCategories().stream()
                .map(CategoryResponse::from)
                .map(category -> new CategoryResponse(
                        category.id(),
                        category.displayName(),
                        category.description(),
                        category.aliases().stream().sorted().toList()))
                .sorted(Comparator.comparing(CategoryResponse::id))
                .toList();

        String version = "sha256:" + sha256(serializeValue(categories).getBytes(StandardCharsets.UTF_8));
        return new CatalogSnapshot(1, version, categories);
    }

    public String serialize(CatalogSnapshot snapshot) {
        return serializeValue(snapshot);
    }

    private String serializeValue(Object value) {
        try {
            return canonicalMapper.writeValueAsString(value);
        } catch (JsonProcessingException exception) {
            throw new IllegalStateException("Category catalog serialization failed.", exception);
        }
    }

    private String sha256(byte[] bytes) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("SHA-256 is required for category catalog versions.", exception);
        }
    }

    @JsonPropertyOrder({"schemaVersion", "catalogVersion", "categories"})
    public record CatalogSnapshot(int schemaVersion, String catalogVersion, List<CategoryResponse> categories) {
        public CatalogSnapshot {
            categories = List.copyOf(categories);
        }
    }
}
