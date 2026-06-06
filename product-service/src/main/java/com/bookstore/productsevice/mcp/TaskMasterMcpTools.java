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
    private static final int MAX_RESULTS = 10;

    private final ProductCacheService productCacheService;
    private final ObjectMapper objectMapper;

    public TaskMasterMcpTools(ProductCacheService productCacheService,
                              ObjectMapper objectMapper) {
        this.productCacheService = productCacheService;
        this.objectMapper = objectMapper;
    }

    @Tool(name = "search_task_masters",
            description = "Search for task masters (service providers) in the marketplace. "
                    + "Returns up to 10 results. "
                    + "IMPORTANT: always call get_categories first to get the exact category list, "
                    + "then use a category value from that list. "
                    + "If the user's request does not match any category, do NOT call this tool. "
                    + "Instead, politely ask the user to clarify what service they need. "
                    + "When a user says 'less than X dollars', set maxRate=X. "
                    + "When a user says 'more than X dollars', set minRate=X.")
    public String searchTaskMasters(
            @ToolParam(description = "Job category to filter by. REQUIRED. Must be an exact value from get_categories.")
            String category,
            @ToolParam(description = "City or region to filter by, e.g. 'New York, NY'.",
                    required = false)
            String location,
            @ToolParam(description = "Minimum hourly rate in USD (inclusive). Numeric only, no $ sign. Example: 20",
                    required = false)
            String minRate,
            @ToolParam(description = "Maximum hourly rate in USD (inclusive). Numeric only, no $ sign. "
                    + "Use this for 'under', 'less than', 'cheaper than' queries. Example: 25",
                    required = false)
            String maxRate,
            @ToolParam(description = "Minimum rating threshold (0-5). Use for quality/top-rated queries.",
                    required = false)
            String minRating) {

        log.info("[MCP] search_task_masters called: category='{}', location='{}', minRate='{}', maxRate='{}', minRating='{}'",
                category, location, minRate, maxRate, minRating);

        String sanitizedCategory = sanitizeString(category);
        String sanitizedLocation = sanitizeString(location);
        Double minRateVal = parseDoubleOrNull(minRate);
        Double maxRateVal = parseDoubleOrNull(maxRate);
        Double minRatingVal = parseDoubleOrNull(minRating);

        if (sanitizedCategory == null) {
            return "{\"error\": \"Please specify what type of service you are looking for so I can help you find the right task master.\"}";
        }

        List<TaskMaster> results = productCacheService.searchWithFilters(
                sanitizedCategory, sanitizedLocation, minRateVal, maxRateVal, minRatingVal, MAX_RESULTS);

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

    private static String sanitizeString(String value) {
        if (value == null) return null;
        String trimmed = value.trim();
        if (trimmed.isEmpty() || trimmed.equalsIgnoreCase("null") || trimmed.equalsIgnoreCase("none")) {
            return null;
        }
        return trimmed;
    }

    private static Double parseDoubleOrNull(String value) {
        if (value == null || value.isBlank()) return null;
        String cleaned = value.trim().replaceAll("[^0-9.]", "");
        if (cleaned.isEmpty()) return null;
        try {
            return Double.parseDouble(cleaned);
        } catch (NumberFormatException e) {
            return null;
        }
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
