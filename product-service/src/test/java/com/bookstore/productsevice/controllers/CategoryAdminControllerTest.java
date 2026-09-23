package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.dto.CategoryResponse;
import com.bookstore.productsevice.category.dto.CreateCategoryRequest;
import com.bookstore.productsevice.category.dto.UpdateCategoryRequest;
import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.services.ProductCacheService;
import jakarta.servlet.http.HttpServletRequest;
import org.junit.Before;
import org.junit.Test;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.setup.MockMvcBuilders;
import com.bookstore.productsevice.errorhandler.advice.ErrorAdvice;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.verifyNoInteractions;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

public class CategoryAdminControllerTest {

    private CategoryService categoryService;
    private ProductCacheService productCacheService;
    private HttpServletRequest request;
    private CategoryAdminController controller;
    private MockMvc mockMvc;

    @Before
    public void setUp() {
        categoryService = mock(CategoryService.class);
        productCacheService = mock(ProductCacheService.class);
        request = mock(HttpServletRequest.class);
        controller = new CategoryAdminController(categoryService, productCacheService);
        mockMvc = MockMvcBuilders.standaloneSetup(controller)
                .setControllerAdvice(new ErrorAdvice())
                .build();
    }

    @Test
    public void listCategories_adminReceivesPublicMetadataOnly() {
        givenAdmin("catalog-admin");
        Category category = category("carpentry", "Carpentry", "Wood construction and repair.")
                .setNormalizedDisplayName("carpentry")
                .setCreatedBy("system-seed")
                .setUpdatedBy("system-seed");
        when(categoryService.getCategories()).thenReturn(List.of(category));

        ResponseEntity<?> response = controller.listCategories(request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody()).isEqualTo(List.of(new CategoryResponse(
                "carpentry",
                "Carpentry",
                "Wood construction and repair.")));
    }

    @Test
    public void createCategory_adminIdentityBecomesAuditActor() {
        givenAdmin("catalog-admin");
        CreateCategoryRequest body = new CreateCategoryRequest(
                "carpentry",
                "Carpentry",
                "Wood construction and repair.");
        when(categoryService.createCategory(
                "carpentry",
                "Carpentry",
                "Wood construction and repair.",
                "catalog-admin"))
                .thenReturn(category(
                        "carpentry",
                        "Carpentry",
                        "Wood construction and repair."));

        ResponseEntity<?> response = controller.createCategory(body, request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        assertThat(response.getBody()).isEqualTo(new CategoryResponse(
                "carpentry",
                "Carpentry",
                "Wood construction and repair."));
        verify(categoryService).createCategory(
                "carpentry",
                "Carpentry",
                "Wood construction and repair.",
                "catalog-admin");
        verify(productCacheService).evictCategoryCatalog();
    }

    @Test
    public void updateCategory_doesNotAcceptReplacementId() {
        givenAdmin("catalog-admin");
        UpdateCategoryRequest body = new UpdateCategoryRequest(
                "Fine Carpentry",
                "Custom wood construction.");
        when(categoryService.updateCategory(
                "carpentry",
                "Fine Carpentry",
                "Custom wood construction.",
                "catalog-admin"))
                .thenReturn(category(
                        "carpentry",
                        "Fine Carpentry",
                        "Custom wood construction."));

        ResponseEntity<?> response = controller.updateCategory("carpentry", body, request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody()).isEqualTo(new CategoryResponse(
                "carpentry",
                "Fine Carpentry",
                "Custom wood construction."));
        verify(productCacheService).evictCategoryCatalog();
    }

    @Test
    public void categoryMutation_nonAdminIsForbidden() {
        when(request.getAttribute("authenticatedUsername")).thenReturn("regular-user");
        when(request.getAttribute("authenticatedAuthorities")).thenReturn(List.of("ROLE_USER"));

        ResponseEntity<?> response = controller.createCategory(
                new CreateCategoryRequest("carpentry", "Carpentry", "Woodwork"),
                request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.FORBIDDEN);
        verify(categoryService, never())
                .createCategory(
                        org.mockito.ArgumentMatchers.any(),
                        org.mockito.ArgumentMatchers.any(),
                        org.mockito.ArgumentMatchers.any(),
                        org.mockito.ArgumentMatchers.any());
        verifyNoInteractions(productCacheService);
    }

    @Test
    public void categoryMutation_missingTrustedIdentityIsUnauthorized() {
        when(request.getAttribute("authenticatedAuthorities"))
                .thenReturn(List.of("ROLE_ADMIN"));

        ResponseEntity<?> response = controller.createCategory(
                new CreateCategoryRequest("carpentry", "Carpentry", "Woodwork"),
                request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        verify(categoryService, never())
                .createCategory(
                        org.mockito.ArgumentMatchers.any(),
                        org.mockito.ArgumentMatchers.any(),
                        org.mockito.ArgumentMatchers.any(),
                        org.mockito.ArgumentMatchers.any());
        verifyNoInteractions(productCacheService);
    }

    @Test
    public void createCategory_malformedJsonReturnsCategoryBadRequest() throws Exception {
        mockMvc.perform(post("/products/admin/categories")
                        .requestAttr("authenticatedUsername", "catalog-admin")
                        .requestAttr("authenticatedAuthorities", List.of("ROLE_ADMIN"))
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("invalid_category"))
                .andExpect(jsonPath("$.message").value("Request body is missing or malformed."));
    }

    @Test
    public void createCategory_jsonNullReturnsCategoryBadRequest() throws Exception {
        mockMvc.perform(post("/products/admin/categories")
                        .requestAttr("authenticatedUsername", "catalog-admin")
                        .requestAttr("authenticatedAuthorities", List.of("ROLE_ADMIN"))
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("null"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("invalid_category"))
                .andExpect(jsonPath("$.message").value("Request body is missing or malformed."));
    }

    private void givenAdmin(String username) {
        when(request.getAttribute("authenticatedUsername")).thenReturn(username);
        when(request.getAttribute("authenticatedAuthorities"))
                .thenReturn(List.of("ROLE_ADMIN"));
    }

    private Category category(String id, String displayName, String description) {
        return new Category()
                .setId(id)
                .setDisplayName(displayName)
                .setDescription(description);
    }
}
