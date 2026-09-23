package com.bookstore.productsevice.category;

import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.repository.CategoryRepository;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.stereotype.Service;

import java.time.Instant;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.function.Supplier;
import java.util.regex.Pattern;
import java.util.stream.Collectors;

@Service
public class CategoryService {

    static final int MAX_ID_LENGTH = 64;
    static final int MAX_DISPLAY_NAME_LENGTH = 80;
    static final int MAX_DESCRIPTION_LENGTH = 500;

    private static final Pattern CATEGORY_ID_PATTERN =
            Pattern.compile("^[a-z0-9]+(?:-[a-z0-9]+)*$");
    private static final Pattern WHITESPACE_PATTERN = Pattern.compile("\\s+");

    private final CategoryRepository categoryRepository;
    private final Supplier<Instant> now;

    @Autowired
    public CategoryService(CategoryRepository categoryRepository) {
        this(categoryRepository, Instant::now);
    }

    CategoryService(CategoryRepository categoryRepository, Supplier<Instant> now) {
        this.categoryRepository = categoryRepository;
        this.now = now;
    }

    public List<Category> getCategories() {
        return categoryRepository.findAllByOrderByDisplayNameAsc();
    }

    public Category getCategory(String id) {
        String normalizedId = normalizeId(id);
        return categoryRepository.findById(normalizedId)
                .orElseThrow(() -> new CategoryNotFoundException(normalizedId));
    }

    public Category createCategory(String id,
                                   String displayName,
                                   String description,
                                   String actor) {
        String normalizedId = normalizeId(id);
        String normalizedDisplayName = normalizeDisplayName(displayName);
        String validatedDescription = normalizeDescription(description);
        String validatedActor = normalizeActor(actor);

        ensureUniqueId(normalizedId);
        ensureUniqueDisplayName(normalizedDisplayName, displayName);

        Instant timestamp = now.get();
        Category category = new Category()
                .setId(normalizedId)
                .setDisplayName(normalizeWhitespace(displayName))
                .setNormalizedDisplayName(normalizedDisplayName)
                .setDescription(validatedDescription)
                .setCreatedAt(timestamp)
                .setCreatedBy(validatedActor)
                .setUpdatedAt(timestamp)
                .setUpdatedBy(validatedActor);

        return saveWithConflictTranslation(category, true);
    }

    public Category updateCategory(String id,
                                   String displayName,
                                   String description,
                                   String actor) {
        Category category = getCategory(id);
        String normalizedDisplayName = normalizeDisplayName(displayName);
        String validatedDescription = normalizeDescription(description);
        String validatedActor = normalizeActor(actor);

        if (!normalizedDisplayName.equals(category.getNormalizedDisplayName())) {
            ensureUniqueDisplayName(normalizedDisplayName, displayName);
        }

        category.setDisplayName(normalizeWhitespace(displayName))
                .setNormalizedDisplayName(normalizedDisplayName)
                .setDescription(validatedDescription)
                .setUpdatedAt(now.get())
                .setUpdatedBy(validatedActor);

        return saveWithConflictTranslation(category, false);
    }

    public List<String> normalizeAndValidateCategoryIds(Collection<String> categoryIds) {
        if (categoryIds == null || categoryIds.isEmpty()) {
            throw validationError(
                    "invalid_category",
                    "At least one category is required.",
                    "jobCategories",
                    "must contain at least one category");
        }

        Set<String> normalizedIds = new LinkedHashSet<>();
        for (String categoryId : categoryIds) {
            try {
                normalizedIds.add(normalizeId(categoryId));
            } catch (CategoryValidationException exception) {
                String invalidValue = categoryId == null ? "null" : "'" + categoryId + "'";
                throw validationError(
                        "invalid_category",
                        "Invalid category ID in jobCategories: " + invalidValue + ".",
                        "jobCategories",
                        "contains invalid category ID: " + invalidValue);
            }
        }

        Set<String> existingIds = categoryRepository.findAllById(normalizedIds).stream()
                .map(Category::getId)
                .collect(Collectors.toSet());
        List<String> unknownIds = normalizedIds.stream()
                .filter(id -> !existingIds.contains(id))
                .toList();

        if (!unknownIds.isEmpty()) {
            String joinedIds = String.join(", ", unknownIds);
            throw validationError(
                    "unknown_category",
                    "Unknown categories: " + joinedIds + ".",
                    "jobCategories",
                    "unknown categories: " + joinedIds);
        }

        return new ArrayList<>(normalizedIds);
    }

    public String[] normalizeAndValidateCategoryIds(String[] categoryIds) {
        Collection<String> categories = categoryIds == null
                ? null
                : Arrays.asList(categoryIds);
        return normalizeAndValidateCategoryIds(categories).toArray(String[]::new);
    }

    public String normalizeId(String id) {
        if (id == null) {
            throw validationError(
                    "invalid_category",
                    "Category ID is required.",
                    "id",
                    "is required");
        }

        String normalizedId = id.trim().toLowerCase(Locale.ROOT);
        if (normalizedId.length() < 2 || normalizedId.length() > MAX_ID_LENGTH
                || !CATEGORY_ID_PATTERN.matcher(normalizedId).matches()) {
            throw validationError(
                    "invalid_category",
                    "Category ID must be 2-64 lowercase letters, numbers, or hyphen-separated segments.",
                    "id",
                    "must match ^[a-z0-9]+(?:-[a-z0-9]+)*$ and be 2-64 characters");
        }
        return normalizedId;
    }

    private String normalizeDisplayName(String displayName) {
        String normalizedWhitespace = normalizeWhitespace(displayName);
        if (normalizedWhitespace.length() < 2
                || normalizedWhitespace.length() > MAX_DISPLAY_NAME_LENGTH) {
            throw validationError(
                    "invalid_category",
                    "Category display name must be 2-80 characters.",
                    "displayName",
                    "must be 2-80 characters");
        }
        return normalizedWhitespace.toLowerCase(Locale.ROOT);
    }

    private String normalizeDescription(String description) {
        if (description == null) {
            throw validationError(
                    "invalid_category",
                    "Category description is required.",
                    "description",
                    "is required");
        }

        String normalizedDescription = description.trim();
        if (normalizedDescription.isEmpty()
                || normalizedDescription.length() > MAX_DESCRIPTION_LENGTH) {
            throw validationError(
                    "invalid_category",
                    "Category description must be 1-500 characters.",
                    "description",
                    "must be 1-500 characters");
        }
        return normalizedDescription;
    }

    private String normalizeActor(String actor) {
        if (actor == null || actor.isBlank()) {
            throw validationError(
                    "invalid_category",
                    "Category audit actor is required.",
                    "actor",
                    "is required");
        }
        return actor.trim();
    }

    private String normalizeWhitespace(String value) {
        if (value == null) {
            return "";
        }
        return WHITESPACE_PATTERN.matcher(value.trim()).replaceAll(" ");
    }

    private void ensureUniqueId(String id) {
        if (categoryRepository.existsById(id)) {
            throw duplicateId(id);
        }
    }

    private void ensureUniqueDisplayName(String normalizedDisplayName, String displayName) {
        if (categoryRepository.existsByNormalizedDisplayName(normalizedDisplayName)) {
            throw duplicateDisplayName(normalizeWhitespace(displayName));
        }
    }

    private Category saveWithConflictTranslation(Category category, boolean creating) {
        try {
            return categoryRepository.save(category);
        } catch (DuplicateKeyException exception) {
            if (creating && categoryRepository.existsById(category.getId())) {
                throw duplicateId(category.getId());
            }
            throw duplicateDisplayName(category.getDisplayName());
        }
    }

    private CategoryConflictException duplicateId(String id) {
        return new CategoryConflictException(
                "duplicate_category_id",
                "Category '" + id + "' already exists.",
                "id");
    }

    private CategoryConflictException duplicateDisplayName(String displayName) {
        return new CategoryConflictException(
                "duplicate_category_display_name",
                "Category display name '" + displayName + "' already exists.",
                "displayName");
    }

    private CategoryValidationException validationError(String errorCode,
                                                        String message,
                                                        String field,
                                                        String fieldMessage) {
        Map<String, String> fieldErrors = new LinkedHashMap<>();
        fieldErrors.put(field, fieldMessage);
        return new CategoryValidationException(errorCode, message, fieldErrors);
    }
}
