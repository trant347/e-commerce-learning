package com.bookstore.productsevice.mcp;

import com.bookstore.productsevice.location.LocationValidationException;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.services.ProductCacheService;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.Test;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.ArgumentMatchers.isNull;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class TaskMasterMcpToolsTest {

    @Test
    public void getCategories_returnsCanonicalCatalogIds() {
        ProductCacheService cacheService = mock(ProductCacheService.class);
        when(cacheService.getCategories())
                .thenReturn(List.of("carpentry", "furniture-assembly"));

        TaskMasterMcpTools tools = new TaskMasterMcpTools(cacheService, new ObjectMapper());

        String result = tools.getCategories();

        assertThat(result).isEqualTo("[\"carpentry\",\"furniture-assembly\"]");
    }

    @Test
    public void searchTaskMasters_singleCategoryPreservesArrayResponseAndExactFilter()
            throws Exception {
        ProductCacheService cacheService = mock(ProductCacheService.class);
        TaskMaster taskMaster = new TaskMaster()
                .setId("tm-1")
                .setName("Alice")
                .setJobCategories(new String[]{"carpentry"});
        when(cacheService.searchWithFilters(
                "carpentry",
                null,
                null,
                null,
                null,
                10))
                .thenReturn(List.of(taskMaster));
        ObjectMapper objectMapper = new ObjectMapper();
        TaskMasterMcpTools tools = new TaskMasterMcpTools(cacheService, objectMapper);

        String result = tools.searchTaskMasters(
                " carpentry ",
                null,
                null,
                null,
                null);

        JsonNode response = objectMapper.readTree(result);
        assertThat(response.isArray()).isTrue();
        assertThat(response).hasSize(1);
        assertThat(response.get(0).get("id").asText()).isEqualTo("tm-1");
        assertThat(response.get(0).get("jobCategories").get(0).asText())
                .isEqualTo("carpentry");
        verify(cacheService).searchWithFilters(
                eq("carpentry"),
                isNull(),
                isNull(),
                isNull(),
                isNull(),
                eq(10));
    }

    @Test
    public void searchTaskMasters_invalidLocation_returnsMachineReadableError() {
        ProductCacheService cacheService = mock(ProductCacheService.class);
        when(cacheService.searchWithFilters(
                eq("carpentry"),
                eq("Chicago, ZZ"),
                any(),
                any(),
                any(),
                eq(10)))
                .thenThrow(new LocationValidationException("Unsupported state."));

        TaskMasterMcpTools tools = new TaskMasterMcpTools(cacheService, new ObjectMapper());

        String result = tools.searchTaskMasters(
                "carpentry",
                "Chicago, ZZ",
                null,
                null,
                null);

        assertThat(result)
                .contains("\"error\":\"invalid_location\"")
                .contains("\"message\":\"Unsupported state.\"");
    }
}
