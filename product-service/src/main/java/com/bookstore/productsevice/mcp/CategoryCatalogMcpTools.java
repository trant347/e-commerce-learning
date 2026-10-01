package com.bookstore.productsevice.mcp;

import com.bookstore.productsevice.category.CategoryCatalogService;
import com.bookstore.productsevice.category.CategoryCatalogUnavailableException;
import org.springframework.ai.tool.annotation.Tool;

public class CategoryCatalogMcpTools {

    private final CategoryCatalogService catalogService;

    public CategoryCatalogMcpTools(CategoryCatalogService catalogService) {
        this.catalogService = catalogService;
    }

    @Tool(name = "get_category_catalog",
            description = "Returns the product-owned category catalog with schemaVersion, catalogVersion, "
                    + "and canonical IDs, display names, descriptions and aliases. "
                    + "Use this metadata to interpret service requests; it contains no provider search results.")
    public String getCategoryCatalog() {
        try {
            return catalogService.serialize(catalogService.getCatalog());
        } catch (CategoryCatalogUnavailableException exception) {
            return "{\"error\":\"category_catalog_unavailable\","
                    + "\"message\":\"Category catalog is temporarily unavailable.\"}";
        }
    }
}
