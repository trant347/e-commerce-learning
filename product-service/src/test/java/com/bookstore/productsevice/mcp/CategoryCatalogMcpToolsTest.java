package com.bookstore.productsevice.mcp;

import com.bookstore.productsevice.category.CategoryCatalogService;
import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.dto.UpdateCategoryRequest;
import com.bookstore.productsevice.controllers.CategoryAdminController;
import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.repository.CategoryRepository;
import com.bookstore.productsevice.services.ProductCacheService;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.Test;
import org.springframework.ai.tool.ToolCallback;
import org.springframework.dao.DataAccessResourceFailureException;
import org.springframework.mock.web.MockHttpServletRequest;

import java.util.Arrays;
import java.util.List;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.*;

public class CategoryCatalogMcpToolsTest {

    private final ObjectMapper mapper = new ObjectMapper();

    @Test
    public void explicitRegistration_exposesRichCatalogWithoutChangingLegacyToolShapes() throws Exception {
        CategoryRepository repository = mock(CategoryRepository.class);
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(
                new Category().setId("carpentry").setDisplayName("Carpentry")
                        .setDescription("Woodwork").setAliases(List.of("woodworking"))));
        ProductCacheService cache = mock(ProductCacheService.class);
        when(cache.getCategories()).thenReturn(List.of("carpentry"));
        McpToolsConfig config = new McpToolsConfig();
        TaskMasterMcpTools legacy = config.taskMasterMcpTools(cache, mapper);
        CategoryCatalogMcpTools rich = config.categoryCatalogMcpTools(
                new CategoryCatalogService(new CategoryService(repository)));

        ToolCallback[] callbacks = config.taskMasterToolCallbackProvider(legacy, rich).getToolCallbacks();

        assertThat(callbacks).extracting(callback -> callback.getToolDefinition().name())
                .containsExactlyInAnyOrder("search_task_masters", "get_task_master_by_id",
                        "get_categories", "get_category_catalog");
        ToolCallback catalog = Arrays.stream(callbacks)
                .filter(callback -> callback.getToolDefinition().name().equals("get_category_catalog"))
                .findFirst().orElseThrow();
        JsonNode schema = mapper.readTree(catalog.getToolDefinition().inputSchema());
        assertThat(schema.get("properties")).isEmpty();
        JsonNode response = parseCallbackResult(catalog.call("{}"));
        assertThat(response.get("schemaVersion").asInt()).isEqualTo(1);
        assertThat(response.get("catalogVersion").asText()).matches("sha256:[0-9a-f]{64}");
        assertThat(response.get("categories").get(0).get("aliases").get(0).asText()).isEqualTo("woodworking");
        assertThat(legacy.getCategories()).isEqualTo("[\"carpentry\"]");
        verify(repository).findAllByOrderByDisplayNameAsc();
    }

    @Test
    public void adminMetadataUpdate_changesNextRichSnapshotWithoutChangingLegacyIds() throws Exception {
        Category category = new Category().setId("carpentry").setDisplayName("Carpentry")
                .setNormalizedDisplayName("carpentry").setDescription("Woodwork")
                .setAliases(List.of("woodworking"));
        CategoryRepository repository = mock(CategoryRepository.class);
        when(repository.findById("carpentry")).thenReturn(Optional.of(category));
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(category));
        when(repository.save(any(Category.class))).thenAnswer(invocation -> invocation.getArgument(0));
        CategoryService categoryService = new CategoryService(repository);
        ProductCacheService cache = mock(ProductCacheService.class);
        when(cache.getCategories()).thenAnswer(invocation -> categoryService.getCategories().stream()
                .map(Category::getId).toList());
        CategoryCatalogMcpTools rich = new CategoryCatalogMcpTools(new CategoryCatalogService(categoryService));
        TaskMasterMcpTools legacy = new TaskMasterMcpTools(cache, mapper);
        JsonNode before = mapper.readTree(rich.getCategoryCatalog());
        String legacyBefore = legacy.getCategories();
        UpdateCategoryRequest update = new UpdateCategoryRequest("Carpentry", "Cabinet repairs and woodwork");
        update.setAliases(mapper.readTree("[\" Cabinet Repair \", \"Woodworking\"]"));
        MockHttpServletRequest request = new MockHttpServletRequest();
        request.setAttribute("authenticatedUsername", "catalog-admin");
        request.setAttribute("authenticatedAuthorities", List.of("ROLE_ADMIN"));

        new CategoryAdminController(categoryService, cache).updateCategory("carpentry", update, request);

        JsonNode after = mapper.readTree(rich.getCategoryCatalog());
        assertThat(after.get("catalogVersion").asText()).isNotEqualTo(before.get("catalogVersion").asText());
        assertThat(after.get("categories").get(0).get("description").asText())
                .isEqualTo("Cabinet repairs and woodwork");
        assertThat(after.get("categories").get(0).get("aliases"))
                .isEqualTo(mapper.readTree("[\"cabinet repair\",\"woodworking\"]"));
        assertThat(legacy.getCategories()).isEqualTo(legacyBefore).isEqualTo("[\"carpentry\"]");
        verify(repository).save(category);
        verify(cache).evictCategoryCatalog();
    }

    @Test
    public void unavailableCatalog_returnsSafeErrorInsteadOfAnEmptySnapshot() throws Exception {
        CategoryRepository repository = mock(CategoryRepository.class);
        when(repository.findAllByOrderByDisplayNameAsc())
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));
        CategoryCatalogMcpTools tools = new CategoryCatalogMcpTools(
                new CategoryCatalogService(new CategoryService(repository)));

        JsonNode response = mapper.readTree(tools.getCategoryCatalog());

        assertThat(response.get("error").asText()).isEqualTo("category_catalog_unavailable");
        assertThat(response.get("message").asText()).isEqualTo("Category catalog is temporarily unavailable.");
        assertThat(response.has("categories")).isFalse();
        assertThat(response.toString()).doesNotContain("private-storage-sentinel");
    }

    @Test
    public void legacyCatalogFailure_alsoReturnsASafeErrorEnvelope() throws Exception {
        CategoryRepository repository = mock(CategoryRepository.class);
        when(repository.findAllByOrderByDisplayNameAsc())
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));
        CategoryService categoryService = new CategoryService(repository);
        ProductCacheService cache = mock(ProductCacheService.class);
        when(cache.getCategories()).thenAnswer(invocation -> categoryService.getCategories());
        TaskMasterMcpTools tools = new TaskMasterMcpTools(cache, mapper);

        JsonNode response = mapper.readTree(tools.getCategories());

        assertThat(response.get("error").asText()).isEqualTo("category_catalog_unavailable");
        assertThat(response.toString()).doesNotContain("private-storage-sentinel");
    }

    private JsonNode parseCallbackResult(String result) throws Exception {
        JsonNode parsed = mapper.readTree(result);
        return parsed.isTextual() ? mapper.readTree(parsed.asText()) : parsed;
    }
}
