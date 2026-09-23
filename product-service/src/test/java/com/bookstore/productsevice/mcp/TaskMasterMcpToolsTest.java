package com.bookstore.productsevice.mcp;

import com.bookstore.productsevice.location.LocationValidationException;
import com.bookstore.productsevice.services.ProductCacheService;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.Test;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
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
