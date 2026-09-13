# MCP Tool Discovery Architecture Specification

## Overview
Decouple AI tool definitions from the AI assistant service using the **Model Context Protocol (MCP)**. Each domain service owns its own tool definitions and exposes them via an MCP server. The AI assistant discovers and invokes tools dynamically at runtime, eliminating the need to modify the AI assistant when domain services change.

## Problem
Previously, the AI assistant service contained hardcoded tool implementations (`SearchTaskMastersTool`, `GetBookingsTool`) that directly called other services via REST. Any change to a domain service's API, parameters, or behavior required a corresponding change in the AI assistant. This tight coupling violated the principle of service autonomy.

## Goals
- Each service **owns its tool definitions** (name, description, parameter schema, execution logic)
- AI assistant **discovers tools dynamically** at startup via MCP
- Adding, modifying, or removing tools in a domain service requires **zero changes** to the AI assistant
- Maintain the existing LLM tool-calling loop — only the tool registry changes

## Architecture

```
                          MCP (SSE)
┌─────────────────────┐ ◄──────────────── ┌─────────────────────────┐
│  ai-assistant-service│                  │   product-service        │
│  (.NET 8)            │   tools/list     │   (Spring Boot 3.5)      │
│                      │ ────────────────►│                          │
│  McpToolDiscovery    │                  │   MCP Server (SSE)       │
│  Service connects    │   tools/call     │   ├─ search_task_masters │
│  via SSE on startup  │ ────────────────►│   ├─ get_task_master_by_id│
│                      │                  │   └─ get_categories      │
│  ToolRegistry        │ ◄────────────── │                          │
│  ├─ get_bookings     │   JSON result    │   TaskMasterMcpTools.java│
│  │  (local tool)     │                  │   (owns tool definitions)│
│  ├─ search_task_masters│                └─────────────────────────┘
│  │  (MCP remote)     │
│  ├─ get_task_master_by_id│               ┌─────────────────────────┐
│  │  (MCP remote)     │                  │   calendar-service       │
│  └─ get_categories   │   REST (local)   │   (.NET 8)               │
│     (MCP remote)     │ ────────────────►│                          │
│                      │                  │   (No MCP yet — uses     │
│  Ollama LLM          │                  │    local GetBookingsTool)│
│  ├─ receives tool    │                  └─────────────────────────┘
│  │  definitions      │
│  └─ calls tools      │
└─────────────────────┘
```

## MCP Protocol Flow

```
Startup:

  ai-assistant         product-service
       │                      │
       │──── GET /sse ───────►│  (SSE connection established)
       │◄──── endpoint event ─│  (server sends message URL)
       │                      │
       │──── tools/list ─────►│  (JSON-RPC over POST /mcp/message)
       │◄──── tool schemas ───│  (name, description, inputSchema)
       │                      │
       │  Register tools in   │
       │  ToolRegistry        │
       │                      │

Runtime (LLM calls a tool):

  User ──► Ollama LLM ──► tool_call: search_task_masters(category="tutoring")
                                │
                          ToolRegistry.ExecuteAsync()
                                │
                          McpRemoteTool.ExecuteAsync()
                                │
                          tools/call ──► product-service MCP
                                │            │
                                │       executes locally via
                                │       ProductCacheService
                                │            │
                          ◄── JSON result ───┘
                                │
                          LLM receives result, generates answer
```

## Product Service — MCP Server

### Dependencies
```xml
<!-- pom.xml -->
<dependency>
    <groupId>org.springframework.ai</groupId>
    <artifactId>spring-ai-starter-mcp-server-webmvc</artifactId>
    <version>1.0.0</version>
</dependency>
```

### Configuration
```yaml
# application.yml
spring.ai.mcp.server:
  name: product-service-mcp
  version: 1.0.0
```

### Endpoints (auto-configured by Spring AI)
| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/sse` | GET | SSE stream — client connects here |
| `/mcp/message` | POST | JSON-RPC messages (tools/list, tools/call) |

### Tools Exposed
| Tool | Description | Parameters |
|------|-------------|------------|
| `search_task_masters` | Search marketplace by category/location | `category?`, `location?` |
| `get_task_master_by_id` | Get a specific profile | `id` |
| `get_categories` | List all job categories | _(none)_ |

### Key Files
- `mcp/TaskMasterMcpTools.java` — Tool methods annotated with `@Tool` / `@ToolParam`
- `mcp/McpToolsConfig.java` — Registers tools as `ToolCallbackProvider` bean

## AI Assistant Service — MCP Client

### Dependencies
```xml
<!-- .csproj -->
<PackageReference Include="ModelContextProtocol" Version="1.4.0" />
```

### Configuration
```json
// appsettings.json
"McpServers": [
  {
    "Name": "product-service",
    "Endpoint": "http://product-service:8080/sse"
  }
]
```

### Key Components

| Component | Purpose |
|-----------|---------|
| `McpServerConfig` | POCO for MCP endpoint configuration |
| `McpRemoteTool` | Implements `IToolDefinition`, wraps `McpClientTool` from SDK |
| `McpToolDiscoveryService` | `BackgroundService` — connects to MCP servers, discovers tools, registers them |
| `ToolRegistry` | Updated to `ConcurrentDictionary` — supports dynamic `Register()` at runtime |

### Discovery Flow
1. `McpToolDiscoveryService` reads `McpServers[]` from config
2. For each server, creates `HttpClientTransport` with `HttpTransportMode.Sse`
3. Calls `McpClient.CreateAsync(transport)` to establish SSE connection
4. Calls `client.ListToolsAsync()` to discover tools
5. Wraps each as `McpRemoteTool` and calls `ToolRegistry.Register(tool)`
6. Retries up to 10 times with backoff if the server isn't ready

### Tool Execution
`McpRemoteTool.ExecuteAsync()` converts string arguments to `Dictionary<string, object?>` and calls `McpClientTool.CallAsync()`, which sends a `tools/call` JSON-RPC request to the remote MCP server over the SSE connection.

## Files Changed

### Product Service (MCP Server)
| File | Status | Description |
|------|--------|-------------|
| `pom.xml` | Modified | Added `spring-ai-starter-mcp-server-webmvc` |
| `application.yml` | Modified | Added MCP server config |
| `mcp/TaskMasterMcpTools.java` | **New** | Tool definitions with `@Tool` annotations |
| `mcp/McpToolsConfig.java` | **New** | Registers tools as `ToolCallbackProvider` |

### AI Assistant Service (MCP Client)
| File | Status | Description |
|------|--------|-------------|
| `.csproj` | Modified | Added `ModelContextProtocol` 1.4.0 |
| `appsettings.json` | Modified | Added `McpServers`, removed Kafka/Redis/ProductService config |
| `Program.cs` | Modified | Removed old registrations, added `McpToolDiscoveryService` |
| `Services/Tools/ToolRegistry.cs` | Modified | `ConcurrentDictionary` + `Register()` method |
| `Services/Mcp/McpServerConfig.cs` | **New** | Config model |
| `Services/Mcp/McpRemoteTool.cs` | **New** | Wraps MCP tool as `IToolDefinition` |
| `Services/Mcp/McpToolDiscoveryService.cs` | **New** | Background service for MCP discovery |
| `Services/Tools/SearchTaskMastersTool.cs` | **Deleted** | Replaced by MCP tool from product-service |
| `Services/Clients/ProductApiClient.cs` | **Deleted** | No longer needed |
| `Services/Contracts/IProductApiClient.cs` | **Deleted** | No longer needed |
| `MessageQueue/CategoryRefreshConsumerWorker.cs` | **Deleted** | Categories now owned by product-service |

## Adding MCP to Another Service

To add MCP to calendar-service (or any other service), follow the same pattern:

1. **In the domain service** — add MCP server dependency, create tool class with `@Tool` annotations, register as `ToolCallbackProvider`
2. **In ai-assistant-service** — add the endpoint to `McpServers[]` in `appsettings.json`:
   ```json
   {
     "Name": "calendar-service",
     "Endpoint": "http://calendar-service:8080/sse"
   }
   ```
3. Remove the corresponding local tool (e.g., `GetBookingsTool`) once the MCP version is working
4. **No code changes needed in ai-assistant-service** — `McpToolDiscoveryService` discovers tools automatically

## Design Decisions

| Decision | Rationale |
|----------|-----------|
| SSE transport (not Streamable HTTP) | Spring AI 1.0.0 supports SSE; Streamable HTTP requires newer versions |
| `ConcurrentDictionary` in ToolRegistry | MCP tools are registered asynchronously after startup; thread safety required |
| Retry with backoff on discovery | Product-service may start after AI assistant in Docker Compose |
| Keep `GetBookingsTool` local | Calendar-service doesn't have MCP yet; migrate when ready |
| Remove Kafka category consumer | Categories are now part of the MCP tool schema, owned by product-service |
