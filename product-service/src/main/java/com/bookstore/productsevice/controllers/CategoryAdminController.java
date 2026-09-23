package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.CategoryValidationException;
import com.bookstore.productsevice.category.dto.CategoryResponse;
import com.bookstore.productsevice.category.dto.CreateCategoryRequest;
import com.bookstore.productsevice.category.dto.UpdateCategoryRequest;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;
import java.util.Map;

@RestController
@RequestMapping("/products/admin/categories")
public class CategoryAdminController {

    private final CategoryService categoryService;

    public CategoryAdminController(CategoryService categoryService) {
        this.categoryService = categoryService;
    }

    @GetMapping
    public ResponseEntity<?> listCategories(HttpServletRequest request) {
        ResponseEntity<?> authorizationError = getAuthorizationError(request);
        if (authorizationError != null) {
            return authorizationError;
        }

        List<CategoryResponse> categories = categoryService.getCategories().stream()
                .map(CategoryResponse::from)
                .toList();
        return ResponseEntity.ok(categories);
    }

    @PostMapping
    public ResponseEntity<?> createCategory(@RequestBody CreateCategoryRequest body,
                                            HttpServletRequest request) {
        ResponseEntity<?> authorizationError = getAuthorizationError(request);
        if (authorizationError != null) {
            return authorizationError;
        }
        if (body == null) {
            throw missingRequestBody();
        }

        CategoryResponse category = CategoryResponse.from(categoryService.createCategory(
                body.id(),
                body.displayName(),
                body.description(),
                getUsername(request)));
        return ResponseEntity.status(HttpStatus.CREATED).body(category);
    }

    @PutMapping("/{id}")
    public ResponseEntity<?> updateCategory(@PathVariable String id,
                                            @RequestBody UpdateCategoryRequest body,
                                            HttpServletRequest request) {
        ResponseEntity<?> authorizationError = getAuthorizationError(request);
        if (authorizationError != null) {
            return authorizationError;
        }
        if (body == null) {
            throw missingRequestBody();
        }

        CategoryResponse category = CategoryResponse.from(categoryService.updateCategory(
                id,
                body.displayName(),
                body.description(),
                getUsername(request)));
        return ResponseEntity.ok(category);
    }

    private ResponseEntity<?> getAuthorizationError(HttpServletRequest request) {
        Object username = request.getAttribute("authenticatedUsername");
        Object authorities = request.getAttribute("authenticatedAuthorities");
        if (username == null || !(authorities instanceof List<?> authorityList)) {
            return ResponseEntity.status(HttpStatus.UNAUTHORIZED)
                    .body(Map.of(
                            "error", "unauthorized",
                            "message", "Authentication is required."));
        }

        boolean isAdmin = authorityList.stream()
                .anyMatch(authority -> "ROLE_ADMIN".equals(authority.toString()));
        if (!isAdmin) {
            return ResponseEntity.status(HttpStatus.FORBIDDEN)
                    .body(Map.of(
                            "error", "forbidden",
                            "message", "Admin access required."));
        }
        return null;
    }

    private String getUsername(HttpServletRequest request) {
        return request.getAttribute("authenticatedUsername").toString();
    }

    private CategoryValidationException missingRequestBody() {
        return new CategoryValidationException(
                "invalid_category",
                "Category request body is required.",
                Map.of("request", "is required"));
    }
}
