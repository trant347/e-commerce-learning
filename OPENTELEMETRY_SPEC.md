# OpenTelemetry Observability Specification

## Overview
Add distributed tracing and metrics collection across all 7 microservices using OpenTelemetry, with Jaeger for trace visualization and Prometheus for metrics storage.

## Goals
- End-to-end distributed tracing across all services (Java, .NET, Node.js)
- Performance metrics: request rates, latency histograms (P50/P95/P99), error rates
- Bottleneck identification in cross-service request chains
- Kafka message flow tracing (producer → consumer)
- Centralized observability dashboards

## Architecture

```
┌─────────────┐   ┌─────────────────┐   ┌──────────────────┐
│  frontend    │   │ product-service │   │ auth-service     │
│  (Node.js)   │   │ (Spring Boot)   │   │ (Spring Boot)    │
└──────┬───────┘   └───────┬─────────┘   └───────┬──────────┘
       │                   │                     │
       │    OTLP/gRPC      │    OTLP/gRPC        │   OTLP/gRPC
       ▼                   ▼                     ▼
┌──────────────────────────────────────────────────────────────┐
│                   OpenTelemetry Collector                     │
│  Receivers: OTLP (gRPC :4317, HTTP :4318)                   │
│  Processors: batch                                           │
│  Exporters: otlp/jaeger, prometheus                         │
└───────────┬──────────────────────────────┬───────────────────┘
            │                              │
            ▼                              ▼
     ┌─────────────┐              ┌─────────────────┐
     │   Jaeger     │              │   Prometheus     │
     │  (port 16686)│              │   (port 9090)    │
     └─────────────┘              └──────────────────┘

┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
│ calendar-service │  │ notification-svc │  │  worker-service  │
│ (.NET 8)         │  │ (.NET 8)         │  │  (.NET 8)        │
└───────┬──────────┘  └───────┬──────────┘  └───────┬──────────┘
        │                     │                     │
        └─────────────────────┼─────────────────────┘
                              │  OTLP/gRPC
                              ▼
                   OpenTelemetry Collector (same)

┌──────────────────┐
│ ai-assistant-svc │
│ (.NET 8)         │──── OTLP/gRPC ───► OTel Collector (same)
└──────────────────┘
```

### Why an OTel Collector?
- **Decouples** services from backend specifics — services only know about OTLP
- **Centralized** configuration for sampling, batching, and export
- **Easy to swap** backends later (e.g., switch from Jaeger to Tempo) without touching any service code
- **Reduces load** on backends via batching and filtering

## Infrastructure Components

### 1. OpenTelemetry Collector
- **Image**: `otel/opentelemetry-collector-contrib:latest`
- **Ports**: 4317 (gRPC), 4318 (HTTP), 8889 (Prometheus metrics endpoint)
- **Config file**: `otel/otel-collector-config.yml`

```yaml
# otel/otel-collector-config.yml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:
    timeout: 5s
    send_batch_size: 1024

exporters:
  otlp/jaeger:
    endpoint: jaeger:4317
    tls:
      insecure: true
  prometheus:
    endpoint: 0.0.0.0:8889
    namespace: otel

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlp/jaeger]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheus]
```

### 2. Jaeger (All-in-One)
- **Image**: `jaegertracing/all-in-one:latest`
- **Ports**: 16686 (UI), 4317 (OTLP gRPC receiver)
- **Environment**: `COLLECTOR_OTLP_ENABLED=true`
- **Access**: http://localhost:16686

### 3. Prometheus
- **Image**: `prom/prometheus:latest`
- **Port**: 9090
- **Config file**: `otel/prometheus.yml`

```yaml
# otel/prometheus.yml
global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: 'otel-collector'
    static_configs:
      - targets: ['otel-collector:8889']
```

## Service Instrumentation

### Java Services (authorization-service, product-service)

**Approach**: Spring Boot 3.x native Micrometer Tracing with OpenTelemetry bridge. This is the idiomatic Spring Boot approach — no Java agent needed.

#### Maven Dependencies (add to both pom.xml files)

```xml
<!-- Micrometer Tracing with OpenTelemetry bridge -->
<dependency>
    <groupId>io.micrometer</groupId>
    <artifactId>micrometer-tracing-bridge-otel</artifactId>
</dependency>

<!-- OTel OTLP exporter for traces -->
<dependency>
    <groupId>io.opentelemetry</groupId>
    <artifactId>opentelemetry-exporter-otlp</artifactId>
</dependency>

<!-- OTel SDK metrics for Prometheus-style metrics export -->
<dependency>
    <groupId>io.micrometer</groupId>
    <artifactId>micrometer-registry-otlp</artifactId>
</dependency>
```

#### application.yml additions

```yaml
management:
  tracing:
    sampling:
      probability: 1.0  # 100% in dev; reduce in production
  otlp:
    tracing:
      endpoint: ${OTEL_EXPORTER_OTLP_ENDPOINT:http://otel-collector:4318}/v1/traces
    metrics:
      export:
        enabled: true
        url: ${OTEL_EXPORTER_OTLP_ENDPOINT:http://otel-collector:4318}/v1/metrics
        step: 30s
```

#### What's auto-instrumented
- HTTP incoming requests (Spring MVC)
- MongoDB operations (Spring Data MongoDB)
- Kafka producer/consumer (Spring Kafka)
- RestTemplate / WebClient outbound calls
- Spring Security filter chain

#### Docker-compose environment additions

```yaml
environment:
  - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
  - OTEL_SERVICE_NAME=product-service  # or authorization-service
  - MANAGEMENT_TRACING_SAMPLING_PROBABILITY=1.0
```

---

### .NET Services (calendar-service, notification-service, worker-service, ai-assistant-service)

**Approach**: OpenTelemetry .NET SDK with auto-instrumentation for ASP.NET Core and HttpClient.

#### NuGet Packages (add to each .csproj)

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.*" />
```

#### Program.cs additions (same pattern for all 4 services)

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Add after builder creation, before builder.Build()
var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "calendar-service";
var otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)));
```

#### What's auto-instrumented
- ASP.NET Core HTTP requests (incoming)
- HttpClient calls (outgoing) — covers inter-service communication
- Runtime metrics (GC, thread pool, etc.)

#### What requires manual instrumentation (future enhancement)
- Kafka consumer/producer spans — Confluent.Kafka doesn't have an OTel auto-instrumentation package. Can add custom `ActivitySource` spans around Kafka produce/consume calls later.
- MongoDB — `MongoDB.Driver.Core.Extensions.DiagnosticSources` package can be added for MongoDB tracing

#### Docker-compose environment additions

```yaml
environment:
  - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
  - OTEL_SERVICE_NAME=calendar-service
```

---

### Node.js Frontend

**Approach**: `@opentelemetry/sdk-node` with auto-instrumentation, loaded before the app starts using `--require`.

#### npm Packages

```json
{
  "@opentelemetry/sdk-node": "^0.57.0",
  "@opentelemetry/auto-instrumentations-node": "^0.55.0",
  "@opentelemetry/exporter-trace-otlp-grpc": "^0.57.0",
  "@opentelemetry/exporter-metrics-otlp-grpc": "^0.57.0"
}
```

#### tracing.js (new file — loaded with --require before app.js)

```javascript
const { NodeSDK } = require('@opentelemetry/sdk-node');
const { getNodeAutoInstrumentations } = require('@opentelemetry/auto-instrumentations-node');
const { OTLPTraceExporter } = require('@opentelemetry/exporter-trace-otlp-grpc');
const { OTLPMetricExporter } = require('@opentelemetry/exporter-metrics-otlp-grpc');
const { PeriodicExportingMetricReader } = require('@opentelemetry/sdk-metrics');

const sdk = new NodeSDK({
  serviceName: process.env.OTEL_SERVICE_NAME || 'frontend',
  traceExporter: new OTLPTraceExporter({
    url: process.env.OTEL_EXPORTER_OTLP_ENDPOINT || 'http://otel-collector:4317',
  }),
  metricReader: new PeriodicExportingMetricReader({
    exporter: new OTLPMetricExporter({
      url: process.env.OTEL_EXPORTER_OTLP_ENDPOINT || 'http://otel-collector:4317',
    }),
    exportIntervalMillis: 30000,
  }),
  instrumentations: [
    getNodeAutoInstrumentations({
      '@opentelemetry/instrumentation-fs': { enabled: false },
    }),
  ],
});

sdk.start();

process.on('SIGTERM', () => {
  sdk.shutdown().then(() => process.exit(0));
});
```

#### package.json change

```json
"scripts": {
  "start": "set NODE_ENV=production && node --require ./tracing.js ./bin/www"
}
```

#### Dockerfile change (for Docker)

```dockerfile
CMD ["node", "--require", "./tracing.js", "./bin/www"]
```

#### What's auto-instrumented
- Express route handlers (incoming HTTP)
- HTTP/HTTPS outbound requests (axios calls to backend services)
- DNS lookups

#### Docker-compose environment additions

```yaml
environment:
  - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
  - OTEL_SERVICE_NAME=frontend
```

---

## Docker-Compose Additions

```yaml
  # ─── Observability Stack ───
  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: ["--config=/etc/otelcol/config.yaml"]
    volumes:
      - ./otel/otel-collector-config.yml:/etc/otelcol/config.yaml
    ports:
      - "4317:4317"   # OTLP gRPC
      - "4318:4318"   # OTLP HTTP
      - "8889:8889"   # Prometheus metrics endpoint
    depends_on:
      - jaeger
    restart: unless-stopped

  jaeger:
    image: jaegertracing/all-in-one:latest
    environment:
      - COLLECTOR_OTLP_ENABLED=true
    ports:
      - "16686:16686"  # Jaeger UI
      - "4317"         # OTLP gRPC (internal only, used by otel-collector)
    restart: unless-stopped

  prometheus:
    image: prom/prometheus:latest
    volumes:
      - ./otel/prometheus.yml:/etc/prometheus/prometheus.yml
    ports:
      - "9090:9090"
    depends_on:
      - otel-collector
    restart: unless-stopped
```

## URLs

   - OTel Collector — central telemetry hub (ports 4317/4318)
   - Jaeger — trace UI at http://localhost:16686 (http://localhost:16686)
   - Prometheus — metrics at http://localhost:9090 (http://localhost:9090)

## Files Summary

| File | Action | Description |
|------|--------|-------------|
| `otel/otel-collector-config.yml` | **Create** | OTel Collector pipeline configuration |
| `otel/prometheus.yml` | **Create** | Prometheus scrape configuration |
| `docker-compose.yml` | **Modify** | Add 3 observability containers + env vars for all services |
| `authorization-service/pom.xml` | **Modify** | Add 3 Maven dependencies |
| `authorization-service/src/main/resources/application.yml` | **Modify** | Add tracing + metrics config |
| `product-service/pom.xml` | **Modify** | Add 3 Maven dependencies |
| `product-service/src/main/resources/application.yml` | **Modify** | Add tracing + metrics config |
| `calendar-service/calendar-service.csproj` | **Modify** | Add 4 NuGet packages |
| `calendar-service/Program.cs` | **Modify** | Add OTel setup (~10 lines) |
| `notification-service/notification-service.csproj` | **Modify** | Add 4 NuGet packages |
| `notification-service/Program.cs` | **Modify** | Add OTel setup (~10 lines) |
| `worker-service/worker-service.csproj` | **Modify** | Add 4 NuGet packages |
| `worker-service/Program.cs` | **Modify** | Add OTel setup (~10 lines) |
| `ai-assistant-service/ai-assistant-service.csproj` | **Modify** | Add 4 NuGet packages |
| `ai-assistant-service/Program.cs` | **Modify** | Add OTel setup (~10 lines) |
| `frontend/tracing.js` | **Create** | OTel auto-instrumentation bootstrap |
| `frontend/package.json` | **Modify** | Add 4 npm packages, update start script |
| `frontend/Dockerfile` | **Modify** | Add --require tracing.js to CMD |

## Future Enhancements (Not in Scope)
- Custom Kafka spans for .NET Confluent.Kafka (no auto-instrumentation exists yet)
- MongoDB tracing for .NET services (add `MongoDB.Driver.Core.Extensions.DiagnosticSources`)
- Custom business metrics (e.g., bookings per minute, task completions)
- Trace sampling strategies for production (tail-based sampling)
- Alerting rules in Prometheus
- Log correlation (attach trace IDs to structured logs)
