package com.bookstore.productsevice.mcp;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.services.ProductCacheService;
import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.ai.tool.annotation.Tool;
import org.springframework.ai.tool.annotation.ToolParam;

import java.util.List;

/**
 * MCP tool definitions for the TaskMaster domain.
 * <p>
 * These tools are registered with the MCP server via {@link McpToolsConfig}
 * and exposed to MCP clients (e.g. the AI assistant service) over SSE.
 * <p>
 * This class is NOT a Spring {@code @Component} — it is instantiated explicitly
 * in the config class so that its {@code @Tool}-annotated methods are wrapped
 * into {@code ToolCallback}s by {@code ToolCallbacks.from(...)}.
 */
public class TaskMasterMcpTools {

    private static final Logger log = LoggerFactory.getLogger(TaskMasterMcpTools.class);

    private final ProductCacheService productCacheService;
    private final ObjectMapper objectMapper;

    public TaskMasterMcpTools(ProductCacheService productCacheService,
                              ObjectMapper objectMapper) {
        this.productCacheService = productCacheService;
        this.objectMapper = objectMapper;
    }

    @Tool(name = "search_task_masters",
            description = "Search for task masters (service providers) in the marketplace. "
                    + "Use this when the user asks about available professionals, skills, pricing, location, or ratings. "
                    + "Optionally filter by category (e.g. plumbing, cleaning, tutoring) or location.")
    public String searchTaskMasters(
            @ToolParam(description = "Job category to filter by, e.g. 'plumbing', 'cleaning', 'tutoring'. "
                    + "Leave empty to return all.", required = false)
            String category,
            @ToolParam(description = "City or region to filter by, e.g. 'New York, NY'. "
                    + "Leave empty to search all locations.", required = false)
            String location) {

        log.info("[MCP] search_task_masters called: category='{}', location='{}'", category, location);

        List<TaskMaster> results;

        if (category != null && !category.isBlank()) {
            results = productCacheService.getByCategory(category);
        } else if (location != null && !location.isBlank()) {
            results = productCacheService.getByLocation(location);
        } else {
            results = productCacheService.getPage(0, 20);
        }

        log.info("[MCP] search_task_masters returning {} results", results.size());
        return toJson(results);
    }

    @Tool(name = "get_task_master_by_id",
            description = "Retrieve a specific task master profile by its unique ID. "
                    + "Use this when the user asks about a specific professional.")
    public String getTaskMasterById(
            @ToolParam(description = "The unique ID of the task master to retrieve.")
            String id) {

        log.info("[MCP] get_task_master_by_id called: id='{}'", id);

        TaskMaster taskMaster = productCacheService.getItemById(id);
        if (taskMaster == null) {
            return "{\"error\": \"Task master not found with id: " + id + "\"}";
        }

        return toJson(taskMaster);
    }

    @Tool(name = "get_categories",
            description = "Returns all available job categories in the TaskMaster marketplace. "
                    + "Use this to discover what types of services are offered.")
    public String getCategories() {
        log.info("[MCP] get_categories called");

        List<String> categories = productCacheService.getCategories();

        log.info("[MCP] get_categories returning {} categories", categories.size());
        return toJson(categories);
    }

    private String toJson(Object value) {
        try {
            return objectMapper.writeValueAsString(value);
        } catch (JsonProcessingException e) {
            log.error("[MCP] JSON serialization failed", e);
            return "{\"error\": \"Failed to serialize result\"}";
        }
    }
}
