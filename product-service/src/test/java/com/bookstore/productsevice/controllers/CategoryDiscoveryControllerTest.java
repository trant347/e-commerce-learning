package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.dto.CategoryResponse;
import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.services.ProductCacheService;
import org.junit.Before;
import org.junit.Test;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.test.util.ReflectionTestUtils;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.when;

public class CategoryDiscoveryControllerTest {

    private ProductCacheService productCacheService;
    private CategoryService categoryService;
    private TaskMasterController controller;

    @Before
    public void setUp() {
        productCacheService = mock(ProductCacheService.class);
        categoryService = mock(CategoryService.class);
        controller = new TaskMasterController();
        ReflectionTestUtils.setField(controller, "productCacheService", productCacheService);
        ReflectionTestUtils.setField(controller, "categoryService", categoryService);
    }

    @Test
    public void getCategories_returnsCanonicalCatalogIds() {
        when(productCacheService.getCategories())
                .thenReturn(List.of("carpentry", "furniture-assembly"));

        ResponseEntity<List<String>> response = controller.getCategories();

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody())
                .containsExactly("carpentry", "furniture-assembly");
    }

    @Test
    public void getCategoryMetadata_returnsOnlyPublicFields() {
        when(categoryService.getCategories()).thenReturn(List.of(
                new Category()
                        .setId("carpentry")
                        .setDisplayName("Carpentry")
                        .setDescription("Wood construction and repair.")
                        .setNormalizedDisplayName("carpentry")
                        .setCreatedBy("system-seed")));

        ResponseEntity<List<CategoryResponse>> response = controller.getCategoryMetadata();

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody()).containsExactly(new CategoryResponse(
                "carpentry",
                "Carpentry",
                "Wood construction and repair."));
    }
}
