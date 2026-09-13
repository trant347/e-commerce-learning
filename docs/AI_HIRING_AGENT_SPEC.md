# AI Hiring and Negotiation Agent - Specification

> Status: **Proposed**
>
> Scope: Evolve `ai-assistant-service` from a request/response tool-calling chatbot into a constrained agent that can help a requester find, negotiate with, and hire a TaskMaster.
>
> Review reconciliation: Updated against the current implementation and `code-reviews/ai-hiring-agent-spec-review.md`. Requirements below describe proposed work unless explicitly identified as implemented.

## 1. Overview

The current AI assistant can search for TaskMasters and answer questions using marketplace tools. It cannot reliably carry a hiring workflow across time, act as the authenticated user, negotiate through counteroffers, or safely perform state-changing operations.

The proposed hiring agent will:

1. Understand the user's service request and hiring constraints.
2. Search and compare suitable TaskMasters.
3. Check availability.
4. Prepare an offer for user approval.
5. Send and track the offer.
6. Handle TaskMaster counteroffers within user-defined limits.
7. Ask the user for approval when a decision exceeds those limits.
8. Convert an agreement into a booking.
9. Continue tracking the booking without automatically performing financial actions.

The agent is a **constrained workflow agent**, not an unrestricted autonomous actor. Domain services remain authoritative for validation and state transitions.

---

## 2. Current System

### 2.1 Current flow

```text
User
  |
  | POST /api/ai-assistant/chat
  v
ai-assistant-service
  |
  +-- Ollama chooses a tool
  |
  +-- Product-service MCP
  |     +-- search_task_masters
  |     +-- get_task_master_by_id
  |     +-- get_categories
  |
  +-- Local calendar REST wrapper
        +-- get_bookings
```

The LLM runs a tool-calling loop inside a single HTTP request. Product tools are discovered through MCP. Calendar access is still implemented as a local `GetBookingsTool`.

### 2.2 Current tool-calling chat limitations

| Limitation | Current behavior | Impact |
|---|---|---|
| Request-scoped execution | The tool loop ends when the chat HTTP request returns | The assistant cannot wait for a TaskMaster response and resume later |
| Artificially short chat context | Both the browser (`prior.slice(-2)`) and service (`MaxChatMemorySize = 2`) independently restrict history to two messages | Both limits must be addressed; conversation history still cannot be the authoritative record of hiring constraints or approvals |
| No durable agent state | The service has no workflow database or persisted plan | Restarts and new chat sessions lose negotiation progress |
| Fixed tool loop | A request is limited to five tool-calling rounds | It supports short lookups, not long-running business workflows |
| Request authentication implemented; tool identity propagation missing | Chat now validates the marketplace JWT and obtains username and roles through `ICurrentUserAccessor`; tool execution has no trusted identity parameter | Preserve AH-01 authentication and extend trusted identity into tool execution rather than trusting request-body or model-supplied identity |
| No delegated authorization | Remote MCP tools are discovered through application-lifetime connections without per-user authorization context | State-changing tools cannot determine which user authorized an action |
| Calendar access is read-only and incomplete | `get_bookings` only safely retrieves a booking when an ID is supplied | The assistant cannot list the caller's bookings, check availability, or create a booking |
| No hiring tools | Product MCP tools search and read TaskMaster data only | The assistant can recommend but cannot hire |
| No negotiation domain | A booking has one `OfferedRatePerHour`, followed by accept or decline | There is no counteroffer, revision history, expiration, or negotiation policy |
| No event-driven resumption | The assistant does not consume negotiation or booking response events | A TaskMaster response cannot wake or resume an agent run |
| No approval model | Tool execution is controlled mainly by the system prompt | A model mistake could trigger an unintended write once mutation tools are added |
| No action idempotency | There is no agent action ID or idempotency key | Retries could create duplicate offers or bookings |
| Lossy tool arguments | `ToolRegistry` flattens JSON values into strings and normalizes placeholders; `McpRemoteTool` forwards those strings | Hiring tools need schema-validated values without silent coercion of prices, versions, or times |
| No deterministic policy enforcement | Budget and approval boundaries are not enforced outside the LLM | Prompt instructions alone are insufficient for transactional actions |
| No auditable delegation record | The system does not record why an action was selected or who approved it | Disputes and operational debugging would be difficult |

The current assistant is therefore a **tool-using chatbot**, not an agent. It can reason and call tools during one response, but it cannot own a durable goal or safely continue work over time.

### 2.3 Chat context versus agent memory

The current `MaxChatMemorySize = 2` restriction and the independent `prior.slice(-2)` in `frontend/ui/components/chat-overlay/chat-overlay.tsx` must be replaced together with a configurable context strategy. Changing the server alone cannot restore history already discarded by the browser. A larger workstation can support a larger model and context window, allowing the assistant to include substantially more conversation history.

This improves:

- Follow-up question handling.
- Reference resolution such as "the second TaskMaster."
- Awareness of preferences discussed earlier in the conversation.
- Response consistency and natural conversational continuity.

It does not replace durable agent memory. Raw conversation history is unsuitable as the sole record of:

- Maximum authorized budget.
- Approved dates and duration.
- Whether the user approved a specific offer.
- Current negotiation and offer version.
- Actions already sent to a domain service.
- Idempotency keys.
- Pending TaskMaster responses.
- Workflow state after a restart or context-window truncation.

The design should use three memory layers:

| Layer | Purpose | Authority |
|---|---|---|
| Recent conversation context | Natural dialogue and reference resolution | Non-authoritative |
| Conversation summary | Compressed preferences and relevant prior discussion | Non-authoritative; may guide the model |
| Structured agent state | Goals, constraints, approvals, actions, negotiation IDs, and workflow status | Authoritative |

Conversation history should be selected by relevance and token budget rather than a fixed message count. The context builder may include recent messages, a rolling summary, relevant earlier messages, and the current structured agent state. Domain and policy decisions must always use structured state and current tool results.

For durable runs, persist messages as they arrive and reconstruct context server-side after reload or restart. For simple chat, coordinate bounded browser history submission with the server's context budget. Do not replace the two-message limit with an unbounded request payload.

---

## 3. Target Agent Definition

The hiring agent is a durable orchestrator that works toward a user-approved hiring goal.

An agent run has:

- A trusted requester identity.
- A defined goal.
- Structured constraints and permissions.
- A persisted execution state.
- A history of observations, decisions, approvals, and actions.
- A configurable context builder that can use the larger local-model context window without treating raw chat as authoritative state.
- The ability to pause while waiting for a user or TaskMaster.
- The ability to resume from an authenticated request or domain event.
- Deterministic safety checks around every state-changing action.

### 3.1 Example goal

> Find a plumber in Toronto who is available Saturday afternoon. Prefer a rating of at least 4.5. Offer up to $70/hour for two hours. You may negotiate automatically up to $75/hour, but ask me before confirming the hire.

The LLM extracts this into structured constraints:

```json
{
  "category": "plumbing",
  "location": "Toronto",
  "candidateTimeWindows": [
    {
      "start": "2026-08-22T16:00:00Z",
      "end": "2026-08-22T21:00:00Z"
    }
  ],
  "durationHours": 2,
  "preferredMaxRatePerHour": 70,
  "absoluteMaxRatePerHour": 75,
  "minimumRating": 4.5,
  "currency": "USD",
  "maxNegotiationRounds": 3,
  "requireApprovalBeforeInitialOffer": true,
  "requireApprovalBeforeFinalAgreement": true
}
```

The structured policy, rather than the prompt, determines what the agent is allowed to do.

---

## 4. Goals

- Let users move from a natural-language request to an agreed booking.
- Reuse product-service as the source of TaskMaster profiles and search.
- Keep calendar-service authoritative for availability, negotiation, and booking state.
- Expose calendar capabilities through MCP so the domain service owns its agent tool contracts.
- Support asynchronous TaskMaster responses and durable resumption.
- Require explicit user approval at important commitment boundaries.
- Enforce budget, time, identity, and authorization rules outside the LLM.
- Make every state-changing action idempotent and auditable.
- Preserve existing direct frontend booking flows.

## 5. Non-Goals

The first version will not:

- Allow the agent to spend money or fund escrow without explicit approval.
- Let the agent submit proof, release escrow, request refunds, or resolve disputes.
- Let the agent impersonate or automatically act for a TaskMaster.
- Allow free-form contracts, milestones, bidding auctions, or legal terms.
- Give MCP tools direct database access.
- Move business validation from calendar-service into the AI assistant.
- Guarantee fully autonomous hiring without user-defined limits.

---

## 6. Service Responsibilities

### 6.1 `ai-assistant-service`

The AI service owns agent orchestration, not booking truth.

It must:

- Authenticate the requester.
- Convert natural language into a structured hiring goal.
- Persist agent runs, constraints, approvals, and action history.
- Use the LLM to interpret requests, compare options, and compose messages.
- Use deterministic code to decide whether an action is permitted.
- Call product and calendar MCP tools.
- Pause when user approval or a TaskMaster response is required.
- Resume from notifications or subsequent user messages.
- Prevent duplicate tool execution with stable action IDs.
- Present current status and required next actions to the user.

### 6.2 `product-service`

Product-service remains authoritative for:

- TaskMaster profiles.
- Categories.
- Locations, ratings, and advertised hourly rates.
- Search and recommendation inputs.

Its existing read-only MCP tools remain available to the agent.

### 6.3 `calendar-service`

Calendar-service initially owns negotiation because agreement terms are tightly coupled to availability and booking creation.

It must:

- Expose availability and hiring operations through MCP.
- Store negotiations and immutable offer history.
- Validate both participants.
- Validate time slots, duration, price, status, and ownership.
- Apply optimistic concurrency to negotiation updates.
- Enforce valid state transitions.
- Create a booking from an agreement exactly once.
- Publish negotiation and booking events through Kafka.
- Continue supporting its existing REST endpoints and frontend.

MCP is an adapter over calendar-service application logic. MCP tools and REST controllers must call the same domain services.

### 6.4 `notification-service`

Notification-service continues to deliver user-facing events. It should support new event types for offers, counteroffers, agreements, expiration, and agent approval requests.

---

## 7. Target Architecture

```text
                             +----------------------+
                             |     product-service  |
                             | Product MCP tools    |
                             +----------^-----------+
                                        |
                                        | MCP
+----------+     authenticated chat      |
| Frontend | --------------------> +-----+------------------+
|          | <-------------------- | ai-assistant-service   |
+----------+   answer / approval   |                        |
                                   | Agent API              |
                                   | Agent orchestrator     |
                                   | Policy engine          |
                                   | Agent state store      |
                                   | MCP clients            |
                                   +-----+-------------^----+
                                         |             |
                               MCP       |             | Kafka events
                                         v             |
                                  +------+-------------+----+
                                  | calendar-service        |
                                  | Calendar/Hiring MCP     |
                                  | Negotiation domain      |
                                  | Booking domain          |
                                  +------+------------------+
                                         |
                                         | notification-events
                                         v
                                  +------+---------------+
                                  | notification-service |
                                  +----------------------+
```

---

## 8. Hiring Workflow

### 8.1 Primary flow

1. User describes the task, schedule, location, and budget.
2. Agent extracts a structured hiring goal.
3. Agent asks for missing required constraints.
4. Agent searches product-service for candidates.
5. Agent checks availability for suitable candidates.
6. Agent ranks candidates according to the user's constraints.
7. Agent prepares an offer draft.
8. User approves or edits the offer.
9. Agent sends the offer to the selected TaskMaster.
10. Agent pauses in `WAITING_FOR_TASKMASTER`.
11. TaskMaster accepts, declines, or counters through authenticated REST/UI endpoints; a TaskMaster-side agent is excluded from v1.
12. Calendar-service publishes an event.
13. Agent resumes and evaluates the response against the stored policy.
14. If permitted, the agent may send a revised offer.
15. Otherwise, it asks the user to approve, reject, or modify the counteroffer.
16. When both parties agree, calendar-service creates the booking exactly once.
17. The agent reports the confirmed booking and next required action.
18. Escrow funding remains an explicit user action.

### 8.2 Candidate selection

The agent may recommend one or more candidates, but must not invent marketplace data. Candidate selection must be based on current tool results.

The deterministic ranking input may include:

- Category match.
- Location match.
- Availability.
- Advertised hourly rate.
- Rating.
- User-stated preferences.

The agent must explain material trade-offs, such as a higher-rated TaskMaster costing more than the preferred budget.

### 8.3 Automatic negotiation

Automatic counteroffers are allowed only when all of the following are true:

- The user explicitly enabled automatic negotiation.
- The proposed rate does not exceed `AbsoluteMaxRatePerHour`.
- The proposed schedule fits an approved time window.
- The duration does not exceed the approved duration.
- The number of negotiation rounds is below `MaxNegotiationRounds`.
- No term other than supported price, schedule, duration, and message has changed.
- The next action does not create a booking or financial commitment requiring approval.

If any condition fails, the agent transitions to `WAITING_FOR_USER_APPROVAL`.

---

## 9. State Machines

### 9.1 Agent run

```text
CREATED
   |
   v
PLANNING
   |
   +-----------------------> WAITING_FOR_USER_INPUT
   |
   v
SEARCHING
   |
   v
PREPARING_OFFER
   |
   v
WAITING_FOR_USER_APPROVAL
   |
   v
SENDING_OFFER
   |
   v
WAITING_FOR_TASKMASTER
   |
   +---- counteroffer -----> EVALUATING_COUNTEROFFER
   |                              |
   |                              +--> WAITING_FOR_USER_APPROVAL
   |                              |
   |                              +--> SENDING_OFFER
   |
   +---- agreement --------> CREATING_BOOKING
   |                              |
   |                              v
   |                           COMPLETED
   |
   +---- decline/expiry ----> COMPLETED

Any non-terminal state may move to CANCELLED or FAILED.
```

### 9.2 Negotiation

```text
DRAFT
  |
  v
AWAITING_REQUESTER_APPROVAL
  |
  v
OFFERED <-------------------+
  |                         |
  +--> COUNTERED -----------+
  |
  +--> AGREED --> BOOKING_CREATED
  |
  +--> DECLINED
  |
  +--> EXPIRED
  |
  +--> CANCELLED
```

`BOOKING_CREATED`, `DECLINED`, `EXPIRED`, and `CANCELLED` are terminal.

---

## 10. Negotiation Data Model

Calendar-service should add a `Negotiation` aggregate.

| Field | Type | Purpose |
|---|---|---|
| `Id` | ObjectId/string | Negotiation identifier |
| `RequesterUsername` | string | Derived from authenticated identity |
| `TaskMasterId` | string | Selected TaskMaster |
| `TaskMasterUsername` | string | Authoritative profile owner |
| `Status` | enum/string | Current negotiation state |
| `CurrentOfferVersion` | int | Optimistic concurrency version |
| `CurrentOfferId` | string | Current active offer |
| `BookingId` | string? | Booking created from the agreement |
| `ExpiresAt` | DateTime | Offer expiration |
| `CreatedAt` | DateTime | Audit timestamp |
| `UpdatedAt` | DateTime | Audit timestamp |

Each negotiation contains or references immutable offer revisions:

| Field | Type | Purpose |
|---|---|---|
| `OfferId` | string | Unique offer revision |
| `Version` | int | Monotonically increasing revision |
| `ProposedBy` | `REQUESTER` or `TASKMASTER` | Party that proposed the revision |
| `SlotStart` | DateTime | UTC start time |
| `DurationHours` | int | Requested duration |
| `RatePerHour` | decimal | Proposed hourly rate |
| `Currency` | string | Three-letter currency code |
| `Message` | string? | Optional negotiation message |
| `CreatedAt` | DateTime | Proposal time |
| `ExpiresAt` | DateTime | Revision expiration |

Offer history must never be overwritten. The current state is a projection over immutable revisions.

### 10.1 Agent data model

`ai-assistant-service` needs its own persistent store for agent state. This is an intentional change from its current stateless design.

Minimum entities:

- `AgentRun`
- `HiringGoal`
- `AgentAction`
- `ApprovalRequest`
- `AgentEventCheckpoint`

An `AgentAction` records:

- Stable `ActionId`.
- Agent run ID.
- Tool name.
- Sanitized arguments.
- Policy decision.
- Approval ID, when required.
- Attempt count.
- Result reference.
- Status and timestamps.

Sensitive credentials and bearer tokens must never be stored in prompts, action arguments, or logs.

### 10.2 Agent store deployment and ownership

There is currently no durable store wired into `ai-assistant-service`. Its existing Compose `Redis__ConnectionString` setting is not evidence of implemented persistence.

AH-11 must record the store choice and durability/atomicity strategy before implementation. The AI service must own its database and use dedicated credentials; it must not store agent state in another service's database.

If MongoDB is selected, delivery must include:

- A dedicated AI Mongo host, logical database, service credentials, and persistent volume in `docker-compose.yml`, following the current per-service deployment pattern.
- A corresponding bootstrap script in `mongo-init/` and non-secret configuration placeholders in `.env.example`.
- Both the `OWN_HOST` mapping and `ALL_HOSTS` list in `scripts/check-db-ownership.sh`, so the new service cannot reference other Mongo hosts and existing services cannot reference its host.
- Bootstrap/upgrade instructions for existing volumes, required indexes, and restart recovery.

If a different store is selected, document and implement equivalent provisioning, credential isolation, and ownership enforcement rather than omitting these requirements. Acceptance must cover persistence across restarts, least-privilege database access, and ownership-check coverage of the new service.

---

## 11. Calendar MCP Tools

Calendar-service should expose narrow business tools rather than generic HTTP or database operations.

### 11.1 Read-only tools

| Tool | Purpose |
|---|---|
| `get_taskmaster_availability` | Return available or occupied slots for a TaskMaster and bounded date range |
| `get_negotiation` | Return a negotiation visible to the caller |
| `list_my_negotiations` | List negotiations for the authenticated requester |
| `get_booking` | Return a booking visible to the caller |
| `list_my_bookings` | List authenticated caller's outgoing bookings |

### 11.2 State-changing tools

| Tool | Purpose | Approval |
|---|---|---|
| `start_negotiation` | Send the initial approved offer | Required by default |
| `revise_negotiation_offer` | Send a requester counteroffer | Policy-controlled |
| `accept_counteroffer` | Accept the TaskMaster's current terms | Always required in v1 |
| `decline_counteroffer` | Decline current terms | Required unless pre-authorized |
| `cancel_negotiation` | Cancel a non-terminal negotiation | Required |
| `create_booking_from_agreement` | Idempotently convert an agreement to a booking | Always required in v1 |

TaskMaster actions use authenticated REST/UI endpoints in v1, not agent tools. Separately authorized TaskMaster tools are a future extension. The requester agent must never call a tool as the TaskMaster.

### 11.3 Tool requirements

Every state-changing tool must:

- Derive actor identity from authenticated execution context.
- Receive an `actionId` or idempotency key through trusted execution metadata generated outside the LLM, not a model-visible argument.
- Reject an action already processed with different arguments.
- Validate the expected negotiation version.
- Return a structured result with machine-readable error codes.
- Publish the applicable domain event.
- Avoid exposing stack traces or infrastructure details to the model.

Example result:

```json
{
  "success": false,
  "code": "NEGOTIATION_VERSION_CONFLICT",
  "message": "The TaskMaster responded before this offer was submitted.",
  "currentVersion": 4,
  "negotiationId": "..."
}
```

### 11.4 Typed argument contract

`IToolDefinition`, `ToolRegistry`, and `McpRemoteTool` must preserve JSON value types through execution and bind hiring arguments to explicit domain DTOs. Validate against the authoritative tool contract even if the model-facing schema is simplified.

- Parse `ratePerHour` as a bounded decimal with the supported monetary precision, not a floating-point approximation or locale-dependent string.
- Require integer `expectedVersion` and `durationHours`, with valid domain ranges.
- Require an explicit timezone for `slotStart`, normalize it to UTC, and enforce calendar-service's hour alignment and duration constraints.
- Preserve arrays, objects, and nulls according to the schema. Reject missing required values, wrong types, placeholder strings, overflow, and unsupported precision with a machine-readable validation error before any mutation.

The `FlattenToString`/`NormalizePlaceholder` path must not apply to hiring tools. Any retained compatibility normalization for existing product search must be explicit and isolated to that read-only tool; it must not silently affect mutation parameters.

### 11.5 Calendar MCP server prerequisite

Calendar-service has no MCP server package or .NET MCP server integration today. Product-service's `McpToolsConfig` is the precedent for explicit tool registration, not a reusable .NET server implementation.

Before AH-03 implementation, complete a bounded compatibility spike and record the selected .NET server package, supported version, transport, endpoint, registration API, and authentication integration. The current AI client forces SSE; selecting another transport requires coordinated client configuration changes. The spike must demonstrate discovery and an authenticated tool call, including rejection of an unauthenticated call. Re-estimate AH-03 if this integration cannot fit its current allocation.

---

## 12. Authentication and Delegated Authorization

### 12.1 User authentication

`ai-assistant-service` already validates the same JWT used by protected marketplace services for chat. Preserve this behavior and apply it to new protected agent endpoints. Request-body identity, including any legacy `ChatRequest.UserId`, must not be trusted for authorization.

The authenticated username and roles must come from validated claims.

Current authorization-service user tokens contain `sub`, `authorities`, `iat`, and `exp`, but no `iss` or `aud`. AH-01 therefore validates the shared HS256 signing configuration and lifetime without issuer/audience validation. Do not describe this as existing issuer validation or require new claims on legacy user tokens as an incidental part of delegation.

### 12.2 MCP execution context

The current application-lifetime MCP connections do not provide sufficient per-user delegation for mutation tools.

Introduce an immutable, explicit `ToolExecutionContext` parameter through `IToolDefinition.ExecuteAsync`, `ToolRegistry.ExecuteAsync`, and `McpRemoteTool.ExecuteAsync`, separate from model-provided business arguments. It carries the validated actor, authorized scopes, correlation identifiers, and, for durable mutations, `agentRunId` and the persisted `actionId`.

HTTP entry points construct this context from authenticated claims. Background resumption reconstructs it from the owned run and its still-valid persisted authorization/approval records, not from event payload identity or a saved bearer token. Read-only chat and startup discovery do not invent agent runs or mutation action IDs.

The orchestrator must persist an action ID and its exact validated arguments before the first external mutation, and reuse them after timeout or restart. Model-supplied identity or execution-metadata fields must be rejected rather than overriding this context.

The registry may remain a singleton tool catalog, but neither it nor registered tools may capture scoped `ICurrentUserAccessor`, user tokens, or mutable per-user headers. Discovery metadata can be shared; authenticated execution sessions cannot be bound to a different caller.

AH-02 must record and prove the transport mechanism before AH-03/AH-04 proceed: either execution-scoped authenticated clients, or a verified per-call transport that binds headers and sessions to the explicit context. Do not mutate shared `HttpClient.DefaultRequestHeaders`. If the SDK cannot guarantee isolated per-call credentials, use execution-scoped clients. Cancellation, disposal, parallel callers, and background runs without `HttpContext` must be covered.

### 12.3 Delegation token trust and calendar authentication

For calendar hiring tools, issue short-lived internal delegation tokens with an actor, explicit token type, issuer, calendar-service audience, least-privilege scopes, expiration, and the applicable run/action IDs. Carry credentials and signed execution metadata outside LLM-visible tool schemas and business arguments.

Delegation must use a signing key distinct from the marketplace user-token HS256 secret. A type or `kid` field alone is insufficient: existing services do not enforce those fields. Only the calendar delegation validator may trust the delegation key, and only for explicitly delegated operations. Configure keys through secrets/configuration, with no committed values or token logging.

Calendar must validate the allowed algorithm, signature, required expiration, issuer, audience, token type, actor, and required scopes. Missing, expired, wrong-audience, wrong-type, or insufficient-scope delegation must fail closed. Delegation tokens must not fall back to the legacy user-token path or authorize unrelated REST/payment operations; other services must continue rejecting them under their existing user-token key.

Calendar currently uses bespoke `JwtAuthMiddleware` and manual controller checks, not a registered ASP.NET authentication scheme. Before AH-02/AH-03 implementation, record whether to extend that explicit enforcement pattern for MCP or introduce a dedicated ASP.NET scheme/policy. Either design must preserve existing audience-less user tokens on existing user routes while keeping delegation validation separate and mandatory on every protected MCP invocation. `UseAuthorization()` alone does not satisfy this requirement.

Tokens must never appear in prompts, tool schemas, tool results, persisted action arguments, or application logs.

Service credentials alone are insufficient because calendar-service must know which user authorized the action.

### 12.4 Authorization principle

Calendar-service performs the final authorization check. The AI service's policy decision is an additional safety boundary, not a replacement for domain authorization.

---

## 13. Approval Model

An approval is a durable record, not a conversational phrase held only in model context.

An `ApprovalRequest` must include:

- Approval ID.
- Agent run ID.
- Proposed action.
- Human-readable summary.
- Exact structured terms.
- Expiration.
- Status: `PENDING`, `APPROVED`, `REJECTED`, or `EXPIRED`.
- Approving user and timestamp.

Approval applies only to the exact terms shown. If price, time, duration, TaskMaster, or currency changes, the old approval is invalid.

### 13.1 Mandatory approval boundaries for v1

The user must explicitly approve:

- The initial external offer.
- Any counteroffer above the preferred budget.
- Any schedule outside an already approved window.
- The final agreement.
- Booking creation.
- Escrow funding or any financial operation.

The final agreement and booking creation may be presented as one approval when the UI clearly states that approval will create the booking.

---

## 14. Agent Execution Rules

The orchestration loop must be implemented in deterministic application code.

The LLM may:

- Extract structured intent.
- Select candidates from supplied tool results.
- Explain comparisons.
- Draft negotiation messages.
- Recommend a proposed action.

The LLM may not:

- Decide whether approval is legally valid.
- Supply authenticated identity.
- Create idempotency keys.
- Override budget or scheduling policies.
- Declare that a tool succeeded without a successful tool result.
- Directly change agent or negotiation state.
- Handle payment credentials.

Before a state-changing tool call, the orchestrator must:

1. Load the latest agent run and negotiation state.
2. Validate the proposed action against policy.
3. Verify a matching approval when required.
4. Generate and persist a stable action ID with the exact validated arguments, or reload the existing action for a retry.
5. Execute the tool with delegated authorization.
6. Persist the result.
7. Advance state only from the verified result.

---

## 15. Events and Resumption

Calendar-service should publish:

| Event | Recipient/use |
|---|---|
| `NEGOTIATION_OFFERED` | Notify TaskMaster |
| `NEGOTIATION_COUNTERED` | Notify requester and resume agent |
| `NEGOTIATION_AGREED` | Notify both parties and resume agent |
| `NEGOTIATION_DECLINED` | Notify requester and complete or re-plan agent |
| `NEGOTIATION_EXPIRED` | Notify both parties and complete or re-plan agent |
| `NEGOTIATION_CANCELLED` | Notify both parties |
| `NEGOTIATION_BOOKING_CREATED` | Notify both parties and complete agent run |

The AI service must consume relevant events using an inbox/checkpoint pattern:

- Deduplicate events by event ID.
- Load the referenced agent run.
- Ignore events for terminal runs.
- Re-fetch authoritative negotiation state.
- Continue from the persisted state machine.

An event is a wake-up signal, not authoritative state by itself.

### 15.1 Event contract and delivery

Use a dedicated `negotiation-events` topic for versioned negotiation domain events. Keep the existing flat `notification-events` contract compatible for current producers; do not use its string-map `ActionPayload` as the agent's durable event contract.

Calendar owns the versioned negotiation event schema. AH-10 must check in its contract/schema and fixtures alongside the calendar producer and use them in AI and notification consumer contract tests. Every envelope includes:

- `eventId`: generated once with the committed transition and retained on every outbox retry.
- `eventType`, `schemaVersion`, and `occurredAt` (UTC).
- `negotiationId`, authoritative participant identifiers, and `negotiationVersion` for ordering.
- `agentRunId` when linked to an agent run, `actionId` when applicable, and `bookingId` for booking creation.
- A typed event-specific payload; no credentials or unnecessary free-form messages.

Use `negotiationId` as the Kafka key and preserve `traceparent` in Kafka headers. Distinguish the envelope's schema version from the negotiation's concurrency version. Manual negotiations may have no agent run; consumers must not infer an authorized run from an untrusted correlation field.

Persist each state transition, its stable event ID, and outbox payload atomically. Kafka delivery is at least once, not physically exactly once. AI and notification-service use separate consumer groups. Notification-service maps domain events to its existing persisted notification/SSE presentation and deduplicates by `(eventId, recipient)`; the AI inbox deduplicates by event ID with recoverable resumption state.

Consumers disable auto-commit and commit only after durable processing or deliberate dead-letter handling. Retryable failures leave or rewind the offset. Unsupported schema versions and malformed envelopes produce explicit errors and follow the dead-letter policy, not a success-shaped skip.

---

## 16. Booking Creation

An agreed negotiation must produce at most one booking.

Calendar-service must:

- Verify both parties agreed to the same offer version.
- Re-check the requested slot before booking.
- Use the agreed rate and duration from server-side negotiation state.
- Reject client- or model-supplied replacement prices.
- Store the source `NegotiationId` on the booking.
- Enforce a unique `NegotiationId` to booking relationship.
- Return the existing booking when the same idempotent action is retried.

If the slot became unavailable, the negotiation must not silently select another time. It should return a conflict so the agent can ask for approval or prepare a new offer.

### 16.1 Price authority and existing booking behavior

Before conversion, the agreed immutable negotiation revision is authoritative for terms. Conversion creates an `ACCEPTED`, unfunded booking, not a new `PENDING` request requiring a second TaskMaster acceptance. Copy `SlotStart`, `DurationHours`, and `OfferedRatePerHour` from that revision and call the existing `FixAgreedPrice(agreedCurrency)` logic once to establish `AgreedAmount` and `AgreedCurrency`, including its two-decimal, midpoint-to-even rounding.

The approval UI must show that same computed total and currency. After conversion, the booking's frozen agreed amount/currency are authoritative for escrow; the linked negotiation remains immutable provenance. Neither subsequent negotiation operations nor direct-booking endpoints may reprice the converted booking. Preserve legacy direct-booking behavior and do not reinterpret old booking messages as negotiation revisions.

### 16.2 Availability, coexistence, and concurrency

Open negotiations, including `AGREED` negotiations awaiting conversion, do not reserve slots. They may coexist with direct pending bookings and other negotiations for the same slot. Existing direct-booking duplicate-request rules still apply.

Agreement conversion must share calendar-service's occupied-range enforcement with direct booking acceptance, including all hours of a multi-hour booking. A successful conversion applies the same auto-decline behavior and notifications for overlapping `PENDING` bookings. Conversely, direct acceptance may make an open negotiation's terms unavailable; it must not silently change those terms or turn the negotiation into a booking.

Re-check availability on proposals, agreement acceptance, and final conversion; loss of availability must not prevent declining or cancelling a negotiation. A losing conversion returns a typed slot conflict and leaves no partial booking; the run returns to planning or requests user direction. The negotiation remains unconverted until an explicit valid transition or expiry. Terms already marked `AGREED` cannot be silently revised; replacement terms require a new offer/negotiation and approval.

AH-05/AH-21 must document the repository-level atomicity and recovery mechanism covering the unique negotiation-to-booking link, occupied ranges, negotiation state, and durable booking-created event. A read-then-write availability check or process-local lock alone is insufficient across service replicas. Coordinate any necessary change to the direct acceptance path so both paths use the same concurrency boundary.

Use a partial unique index for non-null `NegotiationId` so legacy/direct bookings without a negotiation remain valid. Concurrency tests must use Mongo-backed repositories and independent service instances, not only mocks or concurrent calls to one singleton, and cover direct acceptance racing conversion, overlapping multi-hour conversions, retries, and interruption between durable writes.

---

## 17. Error and Recovery Behavior

| Failure | Required behavior |
|---|---|
| Product or calendar tool unavailable | Keep the run resumable; report temporary unavailability |
| MCP connection lost | Retry with bounded backoff; do not duplicate mutation |
| Agent restart | Resume from persisted state and action records |
| Duplicate event | Ignore after inbox deduplication |
| Duplicate tool request | Return the prior result for the same action ID |
| Version conflict | Reload authoritative negotiation and re-plan |
| Approval expired | Request a new approval |
| Offer expired | Mark the run completed or ask whether to prepare a new offer |
| Slot conflict at booking | Do not create a booking; return to planning |
| Model returns invalid structured output | Reject it and retry extraction with bounded attempts |
| Policy violation | Block the action and request user input |

Failures must never be converted into success-shaped responses.

---

## 18. API and User Experience

The existing chat endpoint may remain for simple questions. Durable agent operations should have explicit resources.

Suggested endpoints:

```text
POST /api/ai-assistant/agent-runs
GET  /api/ai-assistant/agent-runs/{id}
POST /api/ai-assistant/agent-runs/{id}/messages
POST /api/ai-assistant/approvals/{id}/approve
POST /api/ai-assistant/approvals/{id}/reject
POST /api/ai-assistant/agent-runs/{id}/cancel
```

The frontend should display:

- Current goal and constraints.
- Candidate comparison.
- Current negotiation terms and history.
- Whether the agent is waiting for the user or TaskMaster.
- Exact terms requiring approval.
- Booking result.
- Audit-friendly action timeline.

The UI must not present a draft or pending tool call as completed.

Keep browser-facing paths aligned across the React API module, Express BFF route, and downstream controller. The BFF already proxies the entire `/api/ai-assistant` prefix and preserves authorization, so both agent-run and approval resources belong under that existing prefix. Preserve the existing authentication storage keys used by frontend and E2E setup.

---

## 19. Observability and Audit

Required telemetry:

- Agent runs started, completed, failed, and cancelled.
- Time spent waiting for user and TaskMaster.
- Tool call count, latency, and failure rate.
- Approval request and rejection rate.
- Negotiation rounds.
- Version conflicts.
- Duplicate actions prevented.
- Agreement-to-booking conversion rate.

Logs and traces should correlate:

- `agentRunId`
- `negotiationId`
- `bookingId`
- `actionId`
- `eventId`

Prompts, JWTs, payment data, and sensitive free-form messages must not be logged by default.

---

## 20. Testing Requirements

### 20.1 Unit tests

- Intent-to-goal validation.
- Policy decisions at and above budget limits.
- Approval matching and invalidation.
- Agent state transitions.
- Negotiation state transitions.
- Optimistic concurrency.
- Idempotent action handling.
- Event deduplication.

### 20.2 Integration tests

- Authenticated MCP read and write calls.
- Actor identity propagation.
- Initial offer creation.
- TaskMaster counteroffer.
- User approval and acceptance.
- Agreement-to-booking conversion.
- Restart and resume.
- Duplicate event and duplicate tool-call handling.
- Slot conflict during final booking.
- Parallel MCP calls from different users, with no identity or action-ID leakage.
- Background event resumption without an HTTP request or persisted bearer token.
- Legacy user-token compatibility and rejection of delegated tokens on unrelated routes/services.
- Typed argument rejection for malformed money, versions, dates, arrays, and placeholders.
- Mongo-backed direct acceptance versus conversion races, including multi-hour overlaps.
- Stable event IDs across outbox retries, schema compatibility, and per-recipient notification deduplication.
- History beyond two messages reaching server-side context assembly through the browser.

### 20.3 End-to-end scenarios

1. User approves an offer; TaskMaster accepts; booking is created.
2. TaskMaster counters within the automatic policy; agent responds.
3. TaskMaster counters above the maximum; agent blocks and asks the user.
4. User rejects a counteroffer.
5. Offer expires while the agent is offline.
6. Calendar-service times out after accepting an action; retry does not duplicate it.
7. An unauthorized user attempts to access another user's run or negotiation.
8. The model attempts to exceed the stored budget; policy enforcement blocks it.

Run browser scenarios with the repository's single-worker Playwright setup against the real Compose stack, reusing authenticated storage-state helpers and unique users where pending-request rules apply. Stub model/network responses for deterministic decisions; do not depend on live Ollama wording or fixed sleeps.

---

## 21. Delivery Plan

### Phase 1 - Secure calendar tools

- Preserve the implemented AI JWT authentication and extend it to new protected endpoints.
- Add explicit tool execution context, isolated delegated transport, and separate delegation-token trust.
- Complete the .NET MCP compatibility spike and calendar authentication integration decision.
- Add calendar-service MCP server.
- Expose authenticated read-only availability and booking tools.
- Replace the local `GetBookingsTool`.

### Phase 2 - Manual negotiation

- Add the `Negotiation` aggregate and immutable offer history.
- Add REST/UI support for TaskMaster accept, decline, and counteroffer.
- Add MCP negotiation tools.
- Add Kafka negotiation events.
- Require explicit approval for every requester mutation.

### Phase 3 - Durable agent

- Add the AI service state store with dedicated provisioning, credentials, and database-ownership enforcement.
- Add agent runs, actions, approvals, and event checkpoints.
- Replace both browser and service two-message limits with coordinated, bounded, token-budgeted context assembly.
- Add rolling conversation summaries and retrieval of relevant earlier messages.
- Implement deterministic state-machine orchestration.
- Resume runs from Kafka events.
- Add agent status and approval UI.

### Phase 4 - Policy-controlled automation

- Add structured negotiation policies.
- Permit automatic counteroffers within approved limits.
- Enforce maximum rounds and expiration.
- Add policy decision audit records.

### Phase 5 - Agreement and booking

- Add idempotent agreement-to-booking conversion.
- Re-check availability during conversion.
- Require final user approval.
- Link negotiation, agent run, and booking records.

### Phase 6 - Financial handoff

- Show escrow funding as the next required user action.
- Do not enable automatic payment until a separate payment-agent security specification is approved.

### 21.1 Review-driven backlog prerequisites

The companion backlog's task IDs remain the delivery references. The following scope is required even where its original task wording is shorter; unresolved implementation choices must be recorded before dependent tasks start.

| Task(s) | Required scope or prerequisite |
|---|---|
| AH-01 | Already implemented for chat; preserve signing/lifetime validation and do not imply legacy issuer/audience claims exist |
| AH-02 | Explicit execution context through the full tool pipeline; isolated transport/lifetimes; distinct delegation key; calendar authentication decision and legacy compatibility |
| AH-03 | .NET MCP package/transport spike and explicit registration; depends on AH-02's authentication/transport contract |
| AH-04 | Retire the local tool and its misleading model-visible "all bookings" description; use caller-authorized calendar tools |
| AH-05, AH-06 | Booking/negotiation authority and coexistence rules; Mongo-backed concurrency coverage |
| AH-08 | TaskMaster REST/UI only in v1; no TaskMaster-side agent |
| AH-09 | Typed arguments and trusted action metadata across AI and calendar, not just calendar tool definitions; requires AH-03's MCP server infrastructure |
| AH-10 | Dedicated domain topic, versioned envelope, durable event IDs/outbox, notification deduplication, and shared contract fixtures |
| AH-11 | Store decision plus provisioning, credentials, bootstrap/configuration, and ownership-check updates |
| AH-12, AH-23 | React/BFF/controller routing alignment under the existing AI prefix |
| AH-14 | Coordinated frontend and backend history handling, including reload/restart behavior |
| AH-15, AH-17 | Persist actions before dispatch; reuse IDs on retry; reconstruct authorized execution context for background resumption |
| AH-21 | Requires AH-03/AH-09's MCP execution contracts and AH-10's event infrastructure as well as AH-06; reconcile price, accepted status, occupied ranges, unique links, and crash recovery |
| AH-25 | Real-stack, single-worker, stubbed-model E2E scenarios plus the integration cases in section 20.2 |

Documentation-path cleanup identified by the review is separate from this feature; do not restore moved root specs or change unrelated instructions as part of hiring-agent implementation.

---

## 22. Acceptance Criteria

The feature is complete when:

- A user can create a durable hiring goal from chat.
- The agent can search TaskMasters and verify availability.
- The user can approve an exact initial offer.
- The TaskMaster can accept, decline, or counter.
- The agent resumes after a TaskMaster response without relying on chat history.
- Budget, schedule, round, and approval rules are enforced outside the LLM.
- Every mutation uses authenticated delegated identity, optimistic concurrency, and idempotency.
- An agreed negotiation creates exactly one booking.
- The agent cannot fund escrow or perform payment operations automatically.
- Users can inspect the current state, terms, approvals, and action history.
- Restarts, retries, duplicate events, and version conflicts do not create duplicate offers or bookings.

---

## 23. Future Service Extraction

Negotiation should remain in calendar-service for the initial implementation because availability, agreed price, and booking creation form one consistency boundary.

A separate `hiring-service` should be considered when the domain expands to include:

- Multi-provider bidding.
- Contracts and signatures.
- Milestones.
- Scope changes after booking.
- Disputes.
- Cancellations with negotiated penalties.
- Complex pricing beyond hourly rates.

If extracted, hiring-service would own negotiations and expose its own MCP server, while calendar-service would remain authoritative for availability and bookings.
