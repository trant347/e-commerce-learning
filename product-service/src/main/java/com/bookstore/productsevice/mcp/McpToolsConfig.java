package com.bookstore.productsevice.mcp;

import com.bookstore.productsevice.services.ProductCacheService;
import com.bookstore.productsevice.category.CategoryCatalogService;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.springframework.ai.support.ToolCallbacks;
import org.springframework.ai.tool.ToolCallbackProvider;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * Registers TaskMaster domain tools with the MCP server.
 * The {@link ToolCallbackProvider} bean is auto-detected by the
 * Spring AI MCP Server auto-configuration and published over SSE.
 */
@Configuration
public class McpToolsConfig {

    @Bean
    public TaskMasterMcpTools taskMasterMcpTools(ProductCacheService productCacheService,
                                                  ObjectMapper objectMapper) {
        return new TaskMasterMcpTools(productCacheService, objectMapper);
    }

    @Bean
    public CategoryCatalogMcpTools categoryCatalogMcpTools(CategoryCatalogService catalogService) {
        return new CategoryCatalogMcpTools(catalogService);
    }

    @Bean
    public ToolCallbackProvider taskMasterToolCallbackProvider(
            TaskMasterMcpTools tools, CategoryCatalogMcpTools catalogTools) {
        return ToolCallbackProvider.from(ToolCallbacks.from(tools, catalogTools));
    }
}
