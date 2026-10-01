package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.errorhandler.advice.ErrorAdvice;
import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.repository.CategoryRepository;
import com.bookstore.productsevice.services.ProductCacheService;
import org.junit.Before;
import org.junit.Test;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.request.MockHttpServletRequestBuilder;
import org.springframework.test.web.servlet.setup.MockMvcBuilders;

import java.util.List;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.*;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.*;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.*;

public class CategoryAliasesControllerTest {

    private CategoryRepository repository;
    private ProductCacheService cache;
    private MockMvc mockMvc;

    @Before
    public void setUp() {
        repository = mock(CategoryRepository.class);
        cache = mock(ProductCacheService.class);
        when(repository.save(any(Category.class))).thenAnswer(invocation -> invocation.getArgument(0));
        mockMvc = MockMvcBuilders.standaloneSetup(
                        new CategoryAdminController(new CategoryService(repository), cache))
                .setControllerAdvice(new ErrorAdvice()).build();
    }

    @Test
    public void createWithAliases_normalizesPersistsAndReturnsPublicMetadata() throws Exception {
        mockMvc.perform(admin(post("/products/admin/categories")).content("""
                        {"id":"carpentry","displayName":"Carpentry","description":"Woodwork",
                         "aliases":[" Wooden   Cupboard Repair ","WOODWORKING"],"createdBy":"untrusted"}
                        """))
                .andExpect(status().isCreated())
                .andExpect(jsonPath("$.aliases[0]").value("wooden cupboard repair"))
                .andExpect(jsonPath("$.aliases[1]").value("woodworking"))
                .andExpect(jsonPath("$.createdBy").doesNotExist())
                .andExpect(jsonPath("$.normalizedDisplayName").doesNotExist());

        var saved = org.mockito.ArgumentCaptor.forClass(Category.class);
        verify(repository).save(saved.capture());
        assertThat(saved.getValue().getCreatedBy()).isEqualTo("catalog-admin");
        verify(cache).evictCategoryCatalog();
    }

    @Test
    public void createWithoutAliases_returnsEmptyArray() throws Exception {
        mockMvc.perform(admin(post("/products/admin/categories")).content("""
                        {"id":"carpentry","displayName":"Carpentry","description":"Woodwork"}
                        """))
                .andExpect(status().isCreated())
                .andExpect(jsonPath("$.aliases").isArray())
                .andExpect(jsonPath("$.aliases").isEmpty());
    }

    @Test
    public void update_distinguishesOmissionReplacementAndClearing() throws Exception {
        Category existing = category().setAliases(List.of("woodworking"));
        when(repository.findById("carpentry")).thenReturn(Optional.of(existing));

        mockMvc.perform(admin(put("/products/admin/categories/carpentry")).content("""
                        {"displayName":"Carpentry","description":"Updated woodwork","id":"replacement"}
                        """))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.id").value("carpentry"))
                .andExpect(jsonPath("$.aliases[0]").value("woodworking"));
        mockMvc.perform(admin(put("/products/admin/categories/carpentry")).content("""
                        {"displayName":"Carpentry","description":"Updated woodwork","aliases":[" Cabinet Repair "]}
                        """))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.aliases[0]").value("cabinet repair"));
        mockMvc.perform(admin(put("/products/admin/categories/carpentry")).content("""
                        {"displayName":"Carpentry","description":"Updated woodwork","aliases":[]}
                        """))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.aliases").isEmpty());
        verify(cache, times(3)).evictCategoryCatalog();
    }

    @Test
    public void invalidAliasShapesAndValues_returnFieldErrorsWithoutSavingOrEvicting() throws Exception {
        when(repository.findById("carpentry")).thenReturn(Optional.of(category()));
        for (String aliases : List.of("null", "\"woodworking\"", "42", "{}", "[42]", "[true]",
                "[{}]", "[[]]", "[null]", "[\"\"]", "[\"woodworking\",\"WOODWORKING\"]")) {
            String createBody = """
                    {"id":"carpentry","displayName":"Carpentry","description":"Woodwork","aliases":%s}
                    """.formatted(aliases);
            String updateBody = """
                    {"displayName":"Carpentry","description":"Woodwork","aliases":%s}
                    """.formatted(aliases);
            mockMvc.perform(admin(post("/products/admin/categories")).content(createBody))
                    .andExpect(status().isBadRequest())
                    .andExpect(jsonPath("$.error").value("invalid_category"))
                    .andExpect(jsonPath("$.fieldErrors.aliases").isString());
            mockMvc.perform(admin(put("/products/admin/categories/carpentry")).content(updateBody))
                    .andExpect(status().isBadRequest())
                    .andExpect(jsonPath("$.error").value("invalid_category"))
                    .andExpect(jsonPath("$.fieldErrors.aliases").isString());
        }
        verify(repository, never()).save(any());
        verifyNoInteractions(cache);
    }

    @Test
    public void legacyDocumentsWithoutAliases_returnEmptyArrays() throws Exception {
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(
                category(), category().setId("joinery").setAliases(null)));

        mockMvc.perform(admin(get("/products/admin/categories")))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$[0].aliases").isEmpty())
                .andExpect(jsonPath("$[1].aliases").isEmpty());
    }

    @Test
    public void nonAdminCannotPublishAliases() throws Exception {
        mockMvc.perform(post("/products/admin/categories")
                        .requestAttr("authenticatedUsername", "alice")
                        .requestAttr("authenticatedAuthorities", List.of("ROLE_USER"))
                        .contentType(MediaType.APPLICATION_JSON).content("""
                                {"id":"carpentry","displayName":"Carpentry","description":"Woodwork",
                                 "aliases":["woodworking"]}
                                """))
                .andExpect(status().isForbidden());
        mockMvc.perform(put("/products/admin/categories/carpentry")
                        .requestAttr("authenticatedUsername", "alice")
                        .requestAttr("authenticatedAuthorities", List.of("ROLE_USER"))
                        .contentType(MediaType.APPLICATION_JSON).content("""
                                {"displayName":"Carpentry","description":"Woodwork","aliases":["woodworking"]}
                                """))
                .andExpect(status().isForbidden());
        verifyNoInteractions(repository, cache);
    }

    private MockHttpServletRequestBuilder admin(MockHttpServletRequestBuilder request) {
        return request.requestAttr("authenticatedUsername", "catalog-admin")
                .requestAttr("authenticatedAuthorities", List.of("ROLE_ADMIN"))
                .contentType(MediaType.APPLICATION_JSON);
    }

    private Category category() {
        return new Category().setId("carpentry").setDisplayName("Carpentry")
                .setNormalizedDisplayName("carpentry").setDescription("Woodwork");
    }
}
