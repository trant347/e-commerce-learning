# Copilot instructions for TaskMaster Hub

## Build and run

This is a polyglot microservice repository. Run commands from the named service directory unless stated otherwise.

- Full local stack: copy `.env.example` to `.env`, provide non-placeholder credentials and a JWT secret of at least 32 characters, build the image-only services with their `build.bat` files (and build `frontend` with `docker build -t frontend .` from `frontend/`), then run `start-dockers.bat` or `docker compose up -d --build` from the repository root. The application is exposed at `http://localhost:3000`.
- Java 25 services (`authorization-service`, `product-service`): `mvn package`; use `mvn package -DskipTests` when only building the artifact/image.
- .NET 8 services (`calendar-service`, `notification-service`, `ai-assistant-service`) and shared contracts: `dotnet build <path-to-csproj>`.
- .NET 10 payment service: `dotnet build payment-service\payment-service.csproj`.
- Frontend BFF and React bundle: install dependencies in both `frontend/` and `frontend/ui/`, then run `npm run build` from `frontend/` (it delegates to webpack in `ui/` and writes the bundle to `frontend/public/`).
- E2E browser setup: from `e2e/`, run `npm install` and `npm run install:browsers`.

There is no repository-configured lint command. Do not invent one.

## Tests

CI is defined in `.github/workflows/test.yml`.

- Database ownership check from the root: `bash scripts/check-db-ownership.sh`.
- Java suite: `mvn -B --no-transfer-progress test`.
- Single Java class/method: `mvn -Dtest=ApplicationControllerTest test` or `mvn -Dtest=ApplicationControllerTest#methodName test`.
- .NET suite: run `dotnet test --nologo --verbosity normal` in the corresponding `*.Tests` directory. Payment tests require .NET 10; the other test projects target .NET 8.
- Single xUnit test: `dotnet test --filter "FullyQualifiedName~SagaStateServiceTests.EnqueueAsync_InsertsStartedSagaAndPendingRequestAsOneDocument"`.
- React/Jest suite from `frontend/ui/`: `npm test -- --ci --colors=false`.
- Single Jest file/test: `npm test -- PayBooking.test.tsx -t "funds an accepted booking"`.
- Playwright E2E from `e2e/` requires the real Docker Compose stack: `npm test`. Run one spec with `npx playwright test tests\booking-multihour.spec.ts`; select a test with `--grep "test name"`.

Playwright intentionally uses one worker because scenarios share cross-actor state and databases. Reuse the storage-state setup in `e2e/global-setup.ts`; create unique users for scenarios affected by the one-pending-application rule. Prefer polling/assertions over fixed sleeps, and stub AI network calls because Ollama output is nondeterministic.

## Architecture

- `frontend/` is an Express backend-for-frontend plus a React/TypeScript UI. Browser code calls stable BFF prefixes such as `/products`, `/user`, `/calendar-service`, `/payment-service`, `/api/notification`, and `/api/ai-assistant`; Express resolves services through Consul and rewrites the prefix before proxying. Keep browser-facing routing changes aligned across the React API module, Express route, and downstream controller.
- `authorization-service` (Spring Boot) owns users and issues shared-secret HS256 JWTs. Downstream services validate tokens locally. User registration events are published through Kafka so other services can initialize user-owned state.
- `product-service` (Spring Boot) owns TaskMaster profiles, applications, search/cache behavior, images, and the product-domain MCP server. It publishes application notifications and exposes MCP tools from `mcp/TaskMasterMcpTools.java`.
- `calendar-service` (.NET 8/MongoDB) owns bookings and orchestrates the booking-payment state machine. New payments are asynchronous escrow operations: it atomically persists saga state plus an embedded payment-request outbox, publishes to Kafka, consumes payment results, updates booking/escrow projections, and reconciles stuck sagas.
- `payment-service` (.NET 10/PostgreSQL/EF Core) owns wallets, escrow records, payment transactions, payment-method tokens, and the immutable double-entry ledger. It consumes payment requests and persists the financial transition plus a payment-result outbox in one database transaction. EF migrations are applied during startup.
- `payment-contracts/` is the shared versioned contract assembly used by calendar and payment services. Kafka payloads use `PaymentContractJson.SerializerOptions` (`JsonSerializerDefaults.Web`, nulls omitted).
- `notification-service` (.NET 8/MongoDB) consumes `notification-events`, persists notifications, and streams live updates to browsers with SSE.
- `ai-assistant-service` (.NET 8) calls Ollama and discovers domain tools dynamically from configured MCP servers. Domain tool schemas and execution belong in the domain service; do not reintroduce product API/tool duplication in the assistant.
- Docker Compose provides Consul, Kafka/Zookeeper, Redis, Ollama, PostgreSQL for payments, and MongoDB logical databases. Cross-service communication is through HTTP/MCP/Kafka, never direct access to another service's data.

## Repository-specific conventions

- Preserve service data ownership. Mongo-backed services use dedicated credentials and logical databases; payment data belongs in payment-service PostgreSQL. If topology/configuration changes, keep `docker-compose.yml`, `mongo-init/`, `.env.example`, and `scripts/check-db-ownership.sh` consistent.
- Payment commands and results are idempotent by `SagaId`; Kafka message keys must match the saga key. Use the versioned types in `Payment.Contracts.V1` and the shared serializer rather than creating look-alike DTOs or custom JSON options.
- Never split a financial state transition from its durable outbox write. Calendar's saga and command payload are one Mongo document; payment transaction, ledger/escrow mutation, and result outbox commit together in PostgreSQL. Retries must return/reconcile the existing result, not repeat money movement.
- Kafka consumers disable auto-commit. Commit only after successful processing or deliberate dead-letter handling; retryable failures rewind/leave the offset uncommitted. Preserve `traceparent` propagation and the existing `Kafka.Producer`/`Kafka.Consumer` activity sources.
- The double-entry journal is authoritative; wallet balances are projections. Add corrections as new/reversing entries, not updates to posted journal history. All approved transfers must remain balanced and carry stable idempotency/audit links.
- Register `MongoDbGuidSupport` at the top of calendar-service startup before any Mongo serialization. MongoDB.Driver 3.x requires the explicit standard GUID representation used by saga documents.
- Product MCP tools are registered explicitly through `McpToolsConfig`; `TaskMasterMcpTools` is intentionally not a component. When adding a domain tool, define it in the owning service and let `McpToolDiscoveryService` register it dynamically.
- Frontend authentication state uses the existing local-storage keys and bearer-token helpers. E2E storage state depends on those exact keys, so coordinate changes with `e2e/helpers.ts`/`global-setup.ts`.
- Root specification files contain valuable invariants, but some sections are marked proposed, superseded, or historical. Check each document's status and current implementation before treating it as executable behavior; `PAYMENT_SAGA_SPEC.md` documents both historical synchronous flow and the implemented asynchronous flow.
