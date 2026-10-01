package com.bookstore.productsevice.services;

import com.bookstore.productsevice.category.CategoryCatalogUnavailableException;
import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.CategoryValidationException;
import com.bookstore.productsevice.controllers.TaskMasterController;
import com.bookstore.productsevice.errorhandler.advice.ErrorAdvice;
import com.bookstore.productsevice.location.LocationNormalizer;
import com.bookstore.productsevice.location.LocationSearchCriteria;
import com.bookstore.productsevice.mcp.TaskMasterMcpTools;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.CategoryRepository;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.fasterxml.jackson.databind.ObjectMapper;
import io.micrometer.core.instrument.simple.SimpleMeterRegistry;
import org.junit.Before;
import org.junit.Test;
import org.springframework.dao.DataAccessResourceFailureException;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.PageRequest;
import org.springframework.data.redis.core.RedisTemplate;
import org.springframework.data.redis.core.ValueOperations;
import org.springframework.test.util.ReflectionTestUtils;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.setup.MockMvcBuilders;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.Assert.assertThrows;
import static org.mockito.ArgumentMatchers.*;
import static org.mockito.Mockito.*;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.*;

public class CategorySearchValidationTest {

    private CategoryRepository categories;
    private TaskMasterRepository providers;
    private RedisTemplate<String, Object> redis;
    private ValueOperations<String, Object> values;
    private ProductCacheService cache;
    private TaskMasterMcpTools tools;
    private MockMvc mvc;
    private final ObjectMapper mapper = new ObjectMapper();

    @Before
    @SuppressWarnings("unchecked")
    public void setUp() {
        categories = mock(CategoryRepository.class);
        providers = mock(TaskMasterRepository.class);
        redis = mock(RedisTemplate.class);
        values = mock(ValueOperations.class);
        when(redis.opsForValue()).thenReturn(values);
        when(categories.existsById("plumbing")).thenReturn(true);
        CategoryService categoryService = new CategoryService(categories);
        cache = new ProductCacheService(redis, providers, categoryService,
                mapper, new SimpleMeterRegistry(), new LocationNormalizer());
        tools = new TaskMasterMcpTools(cache, mapper);
        TaskMasterController controller = new TaskMasterController();
        ReflectionTestUtils.setField(controller, "productCacheService", cache);
        ReflectionTestUtils.setField(controller, "categoryService", categoryService);
        mvc = MockMvcBuilders.standaloneSetup(controller).setControllerAdvice(new ErrorAdvice()).build();
    }

    @Test
    public void unknownCategory_isRejectedOnEveryCacheSurfaceEvenWithStaleCachedResults() {
        when(values.get(anyString())).thenReturn(List.of("stale-provider"));
        when(values.multiGet(anyCollection())).thenReturn(List.of(new TaskMaster().setId("stale-provider")));

        for (Runnable search : searches("unknown-service")) {
            CategoryValidationException exception = assertThrows(CategoryValidationException.class, search::run);
            assertThat(exception.getErrorCode()).isEqualTo("unknown_category");
            assertThat(exception.getFieldErrors()).containsKey("category");
        }

        verifyNoInteractions(redis, values, providers);
    }

    @Test
    public void malformedCategory_isRejectedBeforeCatalogRedisOrProviderReads() {
        for (String category : List.of("", " ", "two services", "plumbing,carpentry", "x", "x".repeat(65))) {
            for (Runnable search : searches(category)) {
                CategoryValidationException exception = assertThrows(CategoryValidationException.class, search::run);
                assertThat(exception.getErrorCode()).isEqualTo("invalid_category");
            }
        }
        assertThrows(CategoryValidationException.class, () -> cache.getByCategory(null));
        assertThrows(CategoryValidationException.class, () -> cache.getByCategoryLimited(null, 10));

        verifyNoInteractions(categories, redis, values, providers);
    }

    @Test
    public void validCategory_isCanonicalizedAndValidatedBeforeACacheHit() {
        String key = "products:filter:category:plumbing";
        when(values.get(key)).thenReturn(List.of("tm-1"));
        when(values.multiGet(List.of("products:item:tm-1"))).thenReturn(List.of(new TaskMaster().setId("tm-1")));

        List<TaskMaster> result = cache.getByCategory(" PLUMBING ");

        assertThat(result).extracting("id").containsExactly("tm-1");
        var order = inOrder(categories, redis, values);
        order.verify(categories).existsById("plumbing");
        order.verify(redis).opsForValue();
        order.verify(values).get(key);
        verifyNoInteractions(providers);
    }

    @Test
    public void validCategoryWithNoProviders_isASuccessfulEmptySearch() throws Exception {
        when(providers.searchWithFilters("plumbing", null, null, null, null, 10)).thenReturn(List.of());
        when(providers.findAllByJobCategoriesContaining("plumbing")).thenReturn(List.of());

        assertThat(tools.searchTaskMasters(" PLUMBING ", null, null, null, null)).isEqualTo("[]");
        mvc.perform(get("/products").param("category", " PLUMBING "))
                .andExpect(status().isOk()).andExpect(content().json("[]"));

        verify(categories, times(2)).existsById("plumbing");
        verify(providers).searchWithFilters("plumbing", null, null, null, null, 10);
        verify(providers).findAllByJobCategoriesContaining("plumbing");
    }

    @Test
    public void catalogStorageFailure_doesNotUseProviderCachesOrBecomeAnUnknownCategory() {
        when(categories.existsById(anyString()))
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));
        when(values.get(anyString())).thenReturn(List.of("stale-provider"));

        for (Runnable search : searches("plumbing")) {
            CategoryCatalogUnavailableException exception = assertThrows(
                    CategoryCatalogUnavailableException.class, search::run);
            assertThat(exception.getMessage()).doesNotContain("private-storage-sentinel");
        }

        verifyNoInteractions(redis, values, providers);
    }

    @Test
    public void rest_invalidUnknownAndUnavailableCategoriesHaveDistinctStatuses() throws Exception {
        mvc.perform(get("/products").param("category", ""))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("invalid_category"))
                .andExpect(jsonPath("$.fieldErrors.category").isString());
        mvc.perform(get("/products").param("category", "unknown-service"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("unknown_category"));
        when(categories.existsById("plumbing"))
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));
        mvc.perform(get("/products").param("category", "plumbing"))
                .andExpect(status().isServiceUnavailable())
                .andExpect(jsonPath("$.error").value("category_catalog_unavailable"))
                .andExpect(content().string(org.hamcrest.Matchers.not(
                        org.hamcrest.Matchers.containsString("private-storage-sentinel"))));

        verifyNoInteractions(redis, values, providers);
    }

    @Test
    public void mcp_invalidUnknownAndUnavailableCategoriesReturnErrorEnvelopesNotProviders() throws Exception {
        for (String category : List.of("", " ", "plumbing,carpentry")) {
            var result = mapper.readTree(tools.searchTaskMasters(category, "Chicago", null, null, null));
            assertThat(result.get("error").asText()).isEqualTo("invalid_category");
            assertThat(result.get("message").isTextual()).isTrue();
            assertThat(result.has("id")).isFalse();
        }
        var unknown = mapper.readTree(tools.searchTaskMasters("woodworking", null, null, null, null));
        assertThat(unknown.get("error").asText()).isEqualTo("unknown_category");
        when(categories.existsById("plumbing"))
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));
        var unavailable = mapper.readTree(tools.searchTaskMasters("plumbing", null, null, null, null));
        assertThat(unavailable.get("error").asText()).isEqualTo("category_catalog_unavailable");
        assertThat(unavailable.toString()).doesNotContain("private-storage-sentinel");

        verifyNoInteractions(redis, values, providers);
    }

    @Test
    public void omittedCategory_preservesLocationOnlySearchAndUnfilteredBrowsing() throws Exception {
        LocationSearchCriteria city = new LocationSearchCriteria(LocationSearchCriteria.MatchMode.CITY, "chicago", null);
        when(providers.searchWithFilters(null, city, null, null, null, 10)).thenReturn(List.of());
        when(providers.findAll(PageRequest.of(0, 20))).thenReturn(Page.empty());

        assertThat(tools.searchTaskMasters(null, "Chicago", null, null, null)).isEqualTo("[]");
        mvc.perform(get("/products")).andExpect(status().isOk()).andExpect(content().json("[]"));

        verifyNoInteractions(categories);
        verify(providers).searchWithFilters(null, city, null, null, null, 10);
        verify(providers).findAll(PageRequest.of(0, 20));
    }

    @Test
    public void publicMetadataStorageFailure_isExplicitUnavailable() throws Exception {
        when(categories.findAllByOrderByDisplayNameAsc())
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));

        mvc.perform(get("/products/categories/metadata"))
                .andExpect(status().isServiceUnavailable())
                .andExpect(jsonPath("$.error").value("category_catalog_unavailable"));

        verifyNoInteractions(redis, values, providers);
    }

    private List<Runnable> searches(String category) {
        return List.of(
                () -> cache.getByCategory(category),
                () -> cache.getByCategoryLimited(category, 10),
                () -> cache.searchWithFilters(category, null, null, null, null, 10));
    }
}
