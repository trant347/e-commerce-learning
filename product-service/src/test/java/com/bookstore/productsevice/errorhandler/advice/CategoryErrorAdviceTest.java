package com.bookstore.productsevice.errorhandler.advice;

import com.bookstore.productsevice.category.CategoryConflictException;
import com.bookstore.productsevice.category.CategoryNotFoundException;
import com.bookstore.productsevice.category.CategoryValidationException;
import org.junit.Before;
import org.junit.Test;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;

import java.util.Map;

import static org.assertj.core.api.Assertions.assertThat;

public class CategoryErrorAdviceTest {

    private ErrorAdvice advice;

    @Before
    public void setUp() {
        advice = new ErrorAdvice();
    }

    @Test
    public void categoryValidation_returnsMachineReadableBadRequest() {
        CategoryValidationException exception = new CategoryValidationException(
                "invalid_category",
                "Category ID is invalid.",
                Map.of("id", "must use a canonical slug"));

        ResponseEntity<Map<String, Object>> response =
                advice.handleCategoryValidation(exception);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.BAD_REQUEST);
        assertThat(response.getBody()).containsEntry("error", "invalid_category");
        assertThat(response.getBody()).containsEntry("message", "Category ID is invalid.");
        assertThat(response.getBody()).containsEntry(
                "fieldErrors",
                Map.of("id", "must use a canonical slug"));
    }

    @Test
    public void categoryConflict_returnsMachineReadableConflict() {
        CategoryConflictException exception = new CategoryConflictException(
                "duplicate_category_id",
                "Category 'carpentry' already exists.",
                "id");

        ResponseEntity<Map<String, Object>> response =
                advice.handleCategoryConflict(exception);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
        assertThat(response.getBody()).containsEntry("error", "duplicate_category_id");
        assertThat(response.getBody()).containsEntry(
                "fieldErrors",
                Map.of("id", "must be unique"));
    }

    @Test
    public void categoryNotFound_returnsMachineReadableNotFound() {
        ResponseEntity<Map<String, Object>> response =
                advice.handleCategoryNotFound(new CategoryNotFoundException("carpentry"));

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.NOT_FOUND);
        assertThat(response.getBody()).containsEntry("error", "category_not_found");
        assertThat(response.getBody()).containsEntry(
                "message",
                "Category 'carpentry' was not found.");
    }
}
