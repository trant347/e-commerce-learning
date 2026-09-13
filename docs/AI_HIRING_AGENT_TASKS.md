# AI Hiring Agent Implementation Backlog

> Tracking companion to [`AI_HIRING_AGENT_SPEC.md`](AI_HIRING_AGENT_SPEC.md).
>
> Tasks target approximately one to two engineer-weeks, including implementation,
> tests, documentation, and review fixes. Estimates are provisional after the spec
> review: re-estimate expanded cross-service work after the required decisions and
> spikes, rather than assuming missing infrastructure already exists.
>
> Aligned with the specification's review reconciliation and section 21.1.

## How to use this backlog

- Change `[ ]` to `[x]` when a task meets all acceptance criteria.
- Complete dependencies before starting a task unless the task explicitly says
  it can be developed against a stub.
- Create one pull request per task. Avoid combining tasks across service
  boundaries unless a small contract change requires it.
- Preserve existing direct booking and chat flows throughout delivery.
- Treat authentication, authorization, approval, idempotency, and state-machine
  requirements as completion criteria, not follow-up hardening.
- Record the transport/authentication decisions in AH-02, the .NET MCP spike in
  AH-03, the store decision in AH-11, and the persistence/concurrency design in
  AH-05/AH-21 before implementing dependent work.
- An earlier task may define a contract against a stub, but agent mutations must
  remain disabled until durable approvals (AH-16) and policy enforcement (AH-18)
  are integrated. A model or stub cannot authorize production mutations.

## Status summary

| Phase | Tasks | Goal |
|---|---:|---|
| 1. Secure calendar tools | AH-01 to AH-04 | Authenticated read-only calendar access through MCP |
| 2. Manual negotiation | AH-05 to AH-10 | Human-controlled offer and counteroffer workflow |
| 3. Durable agent harness | AH-11 to AH-17 | Persisted, resumable, policy-gated agent execution |
| 4. Policy automation | AH-18 to AH-20 | Safe automatic counteroffers within approved limits |
| 5. Agreement and booking | AH-21 to AH-22 | Exactly-once conversion from agreement to booking |
| 6. UX and production readiness | AH-23 to AH-25 | Financial handoff, observability, and end-to-end proof |

---

## Phase 1 - Secure calendar tools

### [x] AH-01 - Authenticate AI assistant requests

**Estimate:** 1 week  
**Dependencies:** None  
**Primary service:** `ai-assistant-service`

**Scope**

- Configure JWT validation using the marketplace HS256 signing configuration and
  token lifetime. Current user tokens have no issuer or audience claims; preserve
  compatibility rather than implying those claims are already validated.
- Require authentication on user-specific chat endpoints; new agent endpoints
  reuse this implemented boundary in AH-12/AH-16.
- Derive username and roles exclusively from validated claims.
- Never use request-body identity, including legacy `ChatRequest.UserId`, for
  authorization.
- Add a typed current-user abstraction that later agent APIs can reuse.

**Acceptance criteria**

- Anonymous requests to protected AI endpoints return `401`.
- Invalid or expired tokens are rejected.
- A request cannot act as another user by changing its body.
- Unit/integration tests cover valid, missing, malformed, and mismatched identity.
- Existing public behavior, if any, is explicitly identified and preserved.

### [ ] AH-02 - Implement delegated MCP authorization

**Estimate:** 1-2 weeks  
**Dependencies:** AH-01  
**Primary services:** `ai-assistant-service`, `calendar-service`

**Scope**

- Give every tool execution two separate inputs: the business arguments proposed
  by the model (for example, rate, time slot, or duration) and an immutable
  `ToolExecutionContext` created by trusted application code. Pass that context
  through `IToolDefinition`, `ToolRegistry`, and `McpRemoteTool`. It contains the
  authenticated actor, authorized scopes, correlation IDs, and, when applicable,
  the persisted agent run and action IDs. These trusted values must never come
  from, or be overridable by, model-provided arguments.
- Keep the singleton registry as a metadata catalog if useful, but never capture
  scoped `ICurrentUserAccessor`, tokens, or mutable user headers in shared tools.
- Record and prove execution-scoped clients or an SDK-supported isolated per-call
  transport. Do not mutate shared `HttpClient.DefaultRequestHeaders`; use
  execution-scoped clients if per-call credential/session isolation is unproven.
- Define short-lived delegation tokens with actor, issuer, calendar audience,
  explicit token type, scopes, expiration, and applicable run/action IDs.
- Use a signing key distinct from the shared marketplace user-token key. A type
  or `kid` field alone does not isolate trust in existing validators.
- Attach credentials and trusted execution metadata outside LLM-visible schemas
  and arguments; reject model attempts to supply or override that metadata.
- Record whether calendar extends its bespoke middleware/manual enforcement or
  introduces a dedicated ASP.NET scheme/policy. Validate delegation separately
  without breaking existing audience-less user tokens on existing user routes.
- Add secret/configuration handling and client cancellation/disposal. Define
  background execution without `HttpContext` against a stub; AH-15/AH-17 wire
  persisted authorization and actions into that contract.

**Acceptance criteria**

- Calendar MCP calls receive the authenticated requester identity.
- Missing, expired, wrong-issuer, wrong-audience, wrong-type, wrong-algorithm,
  invalid-signature, and insufficient-scope delegation tokens are rejected.
- Delegation cannot fall back to legacy user-token validation or authorize
  unrelated REST/payment operations; other services reject its distinct key.
- Existing user tokens remain valid on existing user routes.
- Service credentials alone cannot authorize user mutations.
- Tokens never appear in prompts, schemas, results, persisted arguments, or logs.
- Concurrent users cannot leak identity, scopes, action IDs, or sessions into
  each other's calls. Read-only chat/discovery needs no fabricated mutation ID.
- Integration tests prove propagation, isolation, disposal/cancellation, and
  execution without `HttpContext`; AH-03 adds the real calendar MCP endpoint.

### [ ] AH-03 - Add the calendar-service MCP server and read tools

**Estimate:** 1-2 weeks  
**Dependencies:** AH-02  
**Primary services:** `calendar-service`, `ai-assistant-service`

**Scope**

- Complete a bounded .NET MCP compatibility spike: record package/version,
  transport, endpoint, registration API, and AH-02 authentication integration.
  Calendar has no existing .NET MCP server pattern to reuse.
- Demonstrate discovery and an authenticated call, plus unauthenticated rejection.
  The AI client currently forces SSE; coordinate client configuration if the
  chosen transport differs. Re-estimate before delivery if the spike exposes
  more work than the current allocation.
- Add the selected MCP server, mirroring product-service's `McpToolsConfig`
  explicit-registration principle rather than assuming a .NET equivalent exists.
- Explicitly register narrow tools for:
  - `get_taskmaster_availability`
  - `get_booking`
  - `list_my_bookings`
- Reuse booking application services rather than controllers or direct MongoDB
  access.
- Define bounded inputs and structured, model-safe results.

**Acceptance criteria**

- Every tool derives identity from the authenticated execution context.
- Booking reads enforce the same party/admin rules as REST.
- Availability queries require a bounded date range.
- Tool errors have machine-readable codes and expose no stack traces.
- Contract and authorization tests cover each tool.

### [ ] AH-04 - Replace the local booking tool with calendar MCP

**Estimate:** 1 week  
**Dependencies:** AH-03  
**Primary service:** `ai-assistant-service`

**Scope**

- Remove or retire the local `GetBookingsTool`.
- Remove its model-visible claim that omitting an ID retrieves all bookings.
- Discover and invoke the calendar MCP read tools.
- Forward AH-02's explicit execution context using isolated delegated transport.
- Preserve current chat behavior for booking and availability questions.
- Surface authorization and temporary-service failures honestly.

**Acceptance criteria**

- Authenticated users can query only their own booking data through chat.
- The local duplicate booking implementation is no longer registered.
- Discovered tool descriptions describe caller-authorized access, not unrestricted
  booking lists.
- Calendar unavailability does not produce invented or success-shaped answers.
- Integration tests cover successful, unauthorized, and unavailable MCP calls.

---

## Phase 2 - Manual negotiation

### [ ] AH-05 - Add the negotiation aggregate and persistence

**Estimate:** 1-2 weeks  
**Dependencies:** None  
**Primary service:** `calendar-service`

**Scope**

- Add the `Negotiation` aggregate and immutable offer revisions from the spec.
- Store requester and TaskMaster identities, current version, status, expiration,
  and optional booking ID.
- Add MongoDB indexes for participant queries and expiration processing.
- Keep offer history append-only.
- Document the spec section 16 reconciliation: negotiations own terms before
  conversion; bookings own frozen payment terms afterward. Open negotiations,
  including agreed-but-unconverted ones, do not reserve slots.
- Plan the repository-level atomicity/recovery boundary for immutable revisions,
  conversion links, occupied ranges, and event outbox writes with AH-10/AH-21.

**Acceptance criteria**

- Offer revisions have monotonically increasing versions.
- Existing revisions cannot be overwritten through repository APIs.
- Participant, status, timestamp, and expiration fields round-trip correctly.
- Mongo-backed repository tests cover creation, retrieval, concurrent updates,
  and history using independent service instances, not only singleton/mocked
  service tests.

### [ ] AH-06 - Implement negotiation state transitions

**Estimate:** 1-2 weeks  
**Dependencies:** AH-05  
**Primary service:** `calendar-service`

**Scope**

- Implement application services for starting, revising, accepting, declining,
  cancelling, and reading negotiations.
- Enforce the state machine and participant authorization.
- Validate TaskMaster ownership, availability, duration, currency, rate, and
  offer expiration.
- Apply optimistic concurrency using the expected offer version.
- Re-check availability on proposals and agreement acceptance without reserving
  slots. Loss of availability must not prevent decline/cancellation.
- Preserve direct-booking duplicate-request rules and allow open negotiations
  alongside direct pending bookings. Never silently revise agreed terms.

**Acceptance criteria**

- Invalid transitions return typed domain errors.
- Only the correct participant can perform requester or TaskMaster actions.
- Concurrent updates cannot overwrite a newer offer.
- All prices and times used by transitions come from validated structured input.
- Unit tests cover every valid transition and important rejection path.
- Mongo-backed race tests prove stale writes fail. Availability conflicts do not
  rewrite terms; replacement agreed terms require a new offer/negotiation and
  approval.

### [ ] AH-07 - Add requester negotiation REST APIs

**Estimate:** 1 week  
**Dependencies:** AH-06  
**Primary service:** `calendar-service`

**Scope**

- Add authenticated requester endpoints to create, view, revise, decline, and
  cancel negotiations.
- Use request/response DTOs rather than persisted models.
- Map domain errors consistently with existing controller conventions.
- Keep controllers thin and delegate state changes to the negotiation service.

**Acceptance criteria**

- Requester endpoints enforce ownership and expected-version checks.
- Responses include current terms, status, version, expiration, and history.
- HTTP status codes distinguish validation, authorization, not-found, conflict,
  and infrastructure failures.
- Controller tests cover all routes and authorization boundaries.

### [ ] AH-08 - Add TaskMaster negotiation APIs and UI

**Estimate:** 1-2 weeks  
**Dependencies:** AH-06  
**Primary services:** `calendar-service`, `frontend`

**Scope**

- Add TaskMaster endpoints for listing offers and accepting, declining, or
  countering the current offer.
- Add frontend views for incoming negotiations and immutable offer history.
- Require the expected negotiation version for every response.
- Clearly distinguish draft, offered, countered, agreed, and terminal states.
- Keep TaskMaster actions REST/UI-only in v1; do not add TaskMaster-side agent
  tools. Align React requests, BFF routes, and calendar controllers.

**Acceptance criteria**

- Only the authoritative TaskMaster owner can respond.
- A stale UI action returns a conflict and reloads current terms.
- The UI never displays a pending request as completed.
- API, React, and focused browser/component tests cover the primary flow.

### [ ] AH-09 - Expose negotiation MCP tools

**Estimate:** 1-2 weeks  
**Dependencies:** AH-02, AH-03, AH-06  
**Primary services:** `calendar-service`, `ai-assistant-service`

**Scope**

- Add `get_negotiation`, `list_my_negotiations`, `start_negotiation`,
  `revise_negotiation_offer`, `accept_counteroffer`,
  `decline_counteroffer`, and `cancel_negotiation`.
- Require delegated identity, appropriate scopes, expected versions, and stable
  action IDs for mutations via AH-02's trusted metadata, never model arguments.
- Preserve JSON types across `IToolDefinition`, `ToolRegistry`, and
  `McpRemoteTool`, binding hiring parameters to explicit domain DTOs.
- Validate bounded decimals, integer versions/durations, explicit-timezone dates
  normalized to UTC, calendar hour alignment, and supported monetary precision.
- Exclude hiring tools from `FlattenToString`/`NormalizePlaceholder`; isolate any
  retained search normalization to that existing read-only product tool.
- Return structured results and machine-readable error codes.

**Acceptance criteria**

- The requester agent cannot execute TaskMaster actions.
- Repeating an action ID with identical arguments returns the prior result.
- Reusing an action ID with different arguments is rejected.
- Version conflicts return the current negotiation version.
- Missing values, wrong types, placeholders, malformed arrays/objects, overflow,
  invalid times, and unsupported precision produce typed validation errors before
  any mutation. Simplified model schemas never replace authoritative validation.
- Model-supplied identity/action metadata cannot override trusted context.
- Integration tests cover authorization, idempotency, and conflict behavior.

### [ ] AH-10 - Publish negotiation events and notifications

**Estimate:** 1-2 weeks  
**Dependencies:** AH-06  
**Primary services:** `calendar-service`, `notification-service`

**Scope**

- Publish offered, countered, agreed, declined, expired, and cancelled domain
  events on a dedicated `negotiation-events` topic, keyed by `negotiationId`.
- Define the calendar-owned envelope: stable `eventId`, `eventType`,
  `schemaVersion`, UTC `occurredAt`, `negotiationId`, participant identifiers,
  `negotiationVersion`, and applicable `agentRunId`, `actionId`, and `bookingId`.
  Keep schema and concurrency versions distinct; use typed event payloads.
- Check in schema/contracts and fixtures for producer and consumer contract tests.
  AH-17 consumes this contract; AH-21 adds booking-created event publication.
- Persist transitions, generated event IDs, and outbox payloads atomically.
  Preserve event IDs on retries; Kafka delivery remains at least once.
- Consume domain events in notification-service with a separate group from the AI
  consumer; map them to existing persisted notification/SSE presentation and
  deduplicate by `(eventId, recipient)`.
- Keep legacy `notification-events` producers and their flat contract compatible;
  do not make the agent depend on their string-map `ActionPayload`.
- Preserve `traceparent` headers. Disable auto-commit, commit after durable
  processing or deliberate dead-letter handling, and leave/rewind retryable
  offsets.

**Acceptance criteria**

- A committed transition produces exactly one logical event.
- Outbox retries retain the same event ID and do not promise physically
  exactly-once Kafka delivery.
- Retries do not duplicate persisted user notifications.
- Events identify the negotiation, participants, version, and correlation IDs.
- Failed processing follows the existing retry/dead-letter conventions.
- Malformed envelopes and unsupported schema versions surface explicit errors;
  they are not silently treated as successful processing.
- Integration tests cover publication and consumption of each event type.

---

## Phase 3 - Durable agent harness

### [ ] AH-11 - Add the agent state store

**Estimate:** 1-2 weeks  
**Dependencies:** AH-01  
**Primary service:** `ai-assistant-service`

**Scope**

- Record the store choice and durability/atomicity strategy before implementation;
  the existing unused Redis setting is not implemented persistence.
- Provision an AI-owned store with dedicated credentials and no access to another
  service's data. For MongoDB, add its host, logical database, volume, and
  credentials in `docker-compose.yml`, a `mongo-init/` bootstrap script, and
  non-secret placeholders in `.env.example`.
- For MongoDB, update both `OWN_HOST` and `ALL_HOSTS` in
  `scripts/check-db-ownership.sh` to enforce ownership in both directions.
  A different store requires equivalent provisioning/isolation enforcement.
- Document bootstrap and upgrade behavior for existing volumes.
- Add `AgentRun`, `HiringGoal`, `AgentAction`, `ApprovalRequest`, and
  `AgentEventCheckpoint`.
- Add indexes and optimistic concurrency where concurrent resumption is possible.
- Keep credentials and sensitive free-form content out of persisted action data.

**Acceptance criteria**

- Agent state survives service restarts.
- Runs and approvals are queryable only by their authenticated owner.
- Action IDs are unique and event checkpoints prevent duplicate processing.
- Persistence tests cover concurrency, uniqueness, and sanitized storage.
- Restart recovery and least-privilege database access work in the deployed
  topology; ownership enforcement covers the new service and prevents other
  services from using its database.

### [ ] AH-12 - Add agent-run lifecycle APIs

**Estimate:** 1 week  
**Dependencies:** AH-11  
**Primary service:** `ai-assistant-service`

**Scope**

- Implement create, get, message, and cancel endpoints for agent runs.
- Return the structured goal, current status, required next action, and action
  timeline.
- Enforce run ownership and terminal-state behavior.
- Leave the existing chat endpoint available for simple non-durable questions.
- Reuse AH-01 authentication and align React, Express BFF, and controller routes
  under `/api/ai-assistant/agent-runs`; preserve existing auth storage keys.

**Acceptance criteria**

- Users cannot access or mutate another user's run.
- Cancellation is idempotent and cannot reopen a terminal run.
- API responses distinguish waiting, active, completed, failed, and cancelled.
- Controller/integration tests cover lifecycle and authorization.
- Requests through the BFF preserve authentication and reach the intended routes.

### [ ] AH-13 - Implement structured goal extraction

**Estimate:** 1-2 weeks  
**Dependencies:** AH-11  
**Primary service:** `ai-assistant-service`

**Scope**

- Define the structured `HiringGoal` schema and validation.
- Use the model to extract category, location, time windows, duration, budget,
  rating, currency, approval preferences, and negotiation limits.
- Detect missing required constraints and request user input.
- Reject invalid structured model output with bounded retries.

**Acceptance criteria**

- Valid goals are normalized and persisted independently of chat history.
- Missing fields move the run to `WAITING_FOR_USER_INPUT`.
- Invalid dates, budgets, durations, and limits cannot enter authoritative state.
- A deterministic fixture suite covers representative and adversarial prompts.

### [ ] AH-14 - Build the token-budgeted context builder

**Estimate:** 1-2 weeks  
**Dependencies:** AH-11, AH-13  
**Primary services:** `ai-assistant-service`, `frontend`

**Scope**

- Replace both service `MaxChatMemorySize = 2` and browser `prior.slice(-2)` in
  `chat-overlay.tsx`; changing only the server cannot recover discarded history.
- Assemble context from current structured state, recent messages, relevant
  earlier messages, tool results, and a rolling summary.
- Enforce a configurable token budget.
- Treat structured state and current tool results as authoritative.
- Persist durable-run messages as they arrive and rebuild context server-side.
  Coordinate bounded browser history for simple chat; do not send unbounded
  conversation payloads.

**Acceptance criteria**

- Long conversations remain within the configured context limit.
- Restarted runs rebuild useful context from persisted data.
- Browser-to-server coverage proves history beyond two messages reaches context
  assembly, and reloads preserve durable-run context.
- Summaries cannot override structured constraints or approvals.
- Tests cover truncation, relevance selection, and prompt-injection attempts in
  historical content.

### [ ] AH-15 - Implement deterministic agent orchestration

**Estimate:** 2 weeks  
**Dependencies:** AH-04, AH-09, AH-11, AH-13  
**Primary service:** `ai-assistant-service`

**Scope**

- Implement the agent-run state machine from `CREATED` through terminal states.
- Implement the bounded observe/propose/validate/act/verify/persist loop.
- Allow the model to propose actions but prevent it from directly changing state,
  identity, approvals, or idempotency keys.
- Persist each action ID and exact validated arguments before external dispatch;
  reload and reuse them after timeout or restart. Persist results and advance
  only from verified tool outcomes.
- Construct AH-02 execution context from authenticated claims or the owned run's
  still-valid authorization/approval records. Never use event identity, model
  metadata, or saved bearer tokens as authority.

**Acceptance criteria**

- Every transition is performed by deterministic application code.
- The loop pauses cleanly for user input, approval, or TaskMaster response.
- Failed and malformed model/tool results never become successful transitions.
- Restarting during an action does not duplicate the external mutation.
- Recovery uses the original action ID/arguments, and invalid or expired
  authorization blocks dispatch. Approval/policy stubs cannot enable mutations.
- State-machine and fake-model tests cover all paths.

### [ ] AH-16 - Implement durable approvals

**Estimate:** 1-2 weeks  
**Dependencies:** AH-11, AH-12  
**Primary services:** `ai-assistant-service`, `frontend`

**Scope**

- Add approval creation, approve, reject, and expiration behavior.
- Bind approval to exact structured terms and the authenticated approving user.
- Invalidate approval when material terms change.
- Add UI that presents the exact action and terms being approved.
- Route approvals through the existing `/api/ai-assistant/approvals` BFF prefix
  with validated ownership; preserve frontend authentication storage keys.

**Acceptance criteria**

- Conversational phrases alone never count as approval.
- Expired, rejected, already-used, wrong-user, and changed-term approvals fail.
- Approval use and invalidation are auditable.
- API, policy, and UI tests cover all approval states.

### [ ] AH-17 - Resume agent runs from Kafka events

**Estimate:** 1-2 weeks  
**Dependencies:** AH-10, AH-11, AH-15  
**Primary service:** `ai-assistant-service`

**Scope**

- Consume AH-10's versioned `negotiation-events` contract in an AI-specific group,
  with durable event-ID inbox/checkpoint deduplication and recoverable resumption.
- Load the referenced run and ignore terminal runs.
- Re-fetch authoritative negotiation state before continuing.
- Resume the deterministic loop from persisted state.
- Reconstruct AH-02 execution context from the run and current stored permissions,
  not event participants/correlation fields; manual negotiations may have no run.
- Reuse AH-10 schema fixtures and explicit offset/retry/dead-letter behavior.

**Acceptance criteria**

- Duplicate and out-of-order events do not duplicate actions.
- Events act only as wake-up signals; payloads do not replace domain reads.
- A restart between receiving and processing an event is recoverable.
- Background resumption works without `HttpContext` or a persisted bearer token.
- Unlinked manual events cannot create or authorize an agent run; unsupported
  schemas and malformed events follow explicit error/dead-letter handling.
- Integration tests cover counter, agreement, decline, expiry, and duplicates.

---

## Phase 4 - Policy-controlled automation

### [ ] AH-18 - Implement the negotiation policy engine

**Estimate:** 1-2 weeks  
**Dependencies:** AH-13, AH-16  
**Primary service:** `ai-assistant-service`

**Scope**

- Implement deterministic checks for preferred and absolute budget, approved time
  windows, duration, currency, allowed term changes, maximum rounds, and required
  approvals.
- Return a typed policy decision with reasons.
- Record decisions against agent actions without storing secrets.

**Acceptance criteria**

- The model cannot override or reinterpret a failed policy decision.
- Boundary values at and above every limit are tested.
- Changed terms invalidate approvals as defined in the specification.
- Decisions are reproducible from stored goal, terms, and approval records.

### [ ] AH-19 - Add policy-controlled automatic counteroffers

**Estimate:** 1-2 weeks  
**Dependencies:** AH-15, AH-17, AH-18  
**Primary service:** `ai-assistant-service`

**Scope**

- Evaluate TaskMaster counteroffers against the stored policy.
- Let the model draft a supported counteroffer while deterministic code validates
  the final structured terms.
- Automatically revise only when every automatic-negotiation condition passes.
- Otherwise transition to `WAITING_FOR_USER_APPROVAL`.

**Acceptance criteria**

- Automatic offers never exceed absolute limits or change unsupported terms.
- Maximum negotiation rounds are enforced.
- Above-limit and ambiguous counters always require user input.
- Integration tests cover within-policy, boundary, above-policy, and stale-version
  responses.

### [ ] AH-20 - Add expiration and recovery processing

**Estimate:** 1 week  
**Dependencies:** AH-10, AH-15, AH-18  
**Primary services:** `calendar-service`, `ai-assistant-service`

**Scope**

- Expire stale offers and approval requests.
- Publish and consume expiration events.
- Implement bounded retry/backoff for temporary MCP failures.
- Reload and re-plan after optimistic-concurrency conflicts.

**Acceptance criteria**

- Expiration is idempotent and cannot alter terminal negotiations.
- Temporary failures leave runs resumable.
- Retry exhaustion is visible and never represented as success.
- Tests cover offline expiry, retry, version conflict, and restart recovery.

---

## Phase 5 - Agreement and booking

### [ ] AH-21 - Implement idempotent agreement-to-booking conversion

**Estimate:** 1-2 weeks  
**Dependencies:** AH-03, AH-06, AH-09, AH-10  
**Primary service:** `calendar-service`

**Scope**

- Add `create_booking_from_agreement` application logic and MCP tool.
- Verify both parties agreed to the same offer version.
- Re-check slot availability and use server-side negotiated terms.
- Create an `ACCEPTED`, unfunded booking from the agreed revision, not another
  pending request. Copy rate/schedule/duration and call
  `FixAgreedPrice(agreedCurrency)` once, preserving two-decimal midpoint-to-even
  rounding and immutable booking-owned escrow terms.
- Store `NegotiationId` with a partial unique index for non-null values, preserving
  legacy/direct bookings without that field.
- Share repository-level occupied-range enforcement with direct acceptance for
  all hours of multi-hour bookings; apply overlapping-pending auto-decline behavior
  and notifications on successful conversion.
- Implement the documented atomicity/recovery mechanism for the booking link,
  occupied ranges, negotiation status, and durable booking-created event using
  AH-10's envelope/outbox. A read-then-write check or process-local lock is not
  sufficient across replicas; coordinate the direct acceptance path.

**Acceptance criteria**

- One negotiation can produce at most one booking.
- Retrying the same action returns the existing booking.
- Client- or model-supplied replacement price and schedule are rejected.
- A slot conflict creates no booking and returns a machine-readable conflict.
- Converted bookings need no second TaskMaster acceptance and cannot be repriced
  by negotiation changes or direct-booking endpoints; legacy behavior is preserved.
- Mongo-backed tests using independent instances cover direct acceptance racing
  conversion, overlapping multi-hour conversions, unique-link retries, and crash
  recovery between durable writes. No duplicate or partial booking survives.
- A failed conversion does not silently change agreed terms or reserve a slot;
  decline/cancellation remains available when allowed by the state machine.

### [ ] AH-22 - Orchestrate final approval and booking creation

**Estimate:** 1-2 weeks  
**Dependencies:** AH-16, AH-17, AH-18, AH-21  
**Primary services:** `ai-assistant-service`, `frontend`

**Scope**

- Request final approval for the exact agreed terms and booking creation.
- Display the same rounded total/currency used by `FixAgreedPrice`, clearly
  stating that approval creates an accepted booking with funding still required.
- Invoke the booking conversion tool with a stable action ID.
- Handle success, slot conflict, expiration, and uncertain timeout results.
- Show the confirmed booking and next required action.

**Acceptance criteria**

- No booking is created without a valid final approval.
- A timeout followed by retry cannot create a duplicate booking.
- Changed or stale agreement terms invalidate the approval.
- Slot conflict returns the run to planning or requests user direction.
- End-to-end tests cover success and all recovery paths.

---

## Phase 6 - UX and production readiness

### [ ] AH-23 - Build the agent status and timeline UI

**Estimate:** 1-2 weeks  
**Dependencies:** AH-12, AH-16, AH-17  
**Primary service:** `frontend`

**Scope**

- Display the goal, constraints, candidates, negotiation terms/history, current
  state, pending approval, booking result, and action timeline.
- Support sending additional messages and cancelling a run.
- Distinguish pending, attempted, failed, and completed actions.
- Use the aligned React API module, existing `/api/ai-assistant` BFF proxy, and
  downstream agent-run/approval routes without changing auth storage keys.

**Acceptance criteria**

- Reloading the page reconstructs state from the agent-run API.
- Users can clearly tell who or what the run is waiting for.
- Failed or pending calls are never shown as successful.
- Component and browser tests cover the primary lifecycle.
- Authenticated BFF requests reach both resource families and cannot expose
  another user's run or approvals.

### [ ] AH-24 - Add explicit escrow handoff

**Estimate:** 1 week  
**Dependencies:** AH-22, AH-23  
**Primary service:** `frontend`

**Scope**

- After booking creation, show escrow funding as the next explicit user action.
- Link to the existing booking payment flow.
- Ensure the hiring agent has no payment credential or automatic funding tool.

**Acceptance criteria**

- The user must actively initiate the existing payment flow.
- No payment token enters agent prompts, state, actions, or logs.
- The agent reports that hiring is complete but funding is still required.
- UI tests verify the handoff and absence of automatic payment behavior.

### [ ] AH-25 - Add observability and end-to-end resilience coverage

**Estimate:** 1-2 weeks  
**Dependencies:** AH-14, AH-19, AH-20, AH-22, AH-24  
**Primary services:** Cross-service

**Scope**

- Add metrics for run outcomes, wait time, tool calls, approvals, rounds,
  conflicts, prevented duplicates, and booking conversion.
- Correlate traces and sanitized logs by agent run, negotiation, booking, action,
  and event IDs.
- Add replayable/fake-model scenarios and the specification's end-to-end cases.
- Add fault injection for timeout, duplicate event, invalid model output, and
  restart/resume behavior.
- Run Playwright single-worker against the real Compose stack using existing
  storage-state helpers and unique users where pending-request rules apply.
  Stub model/network decisions and use polling/assertions rather than fixed sleeps.
- Include spec section 20.2 integration cases: parallel identity isolation,
  background authorization, legacy/delegation token separation, typed argument
  rejection, Mongo-backed overlap races, event contract/deduplication behavior,
  and browser history beyond two messages.

**Acceptance criteria**

- Operators can trace one hiring run across AI, calendar, Kafka, and booking.
- Prompts, JWTs, payment data, and sensitive messages are not logged by default.
- All eight end-to-end scenarios in the specification pass.
- A deterministic test proves the model cannot bypass stored policy.
- Deployment and operational documentation describes failure diagnosis.
- Regression coverage uses deterministic model responses, not live Ollama wording.

---

## Recommended implementation order

```text
AH-01 -> AH-02 -> AH-03 -> AH-04

AH-05 -> AH-06 -> AH-07
                  +-> AH-08
                  +-> AH-10

AH-02 + AH-03 + AH-06 -> AH-09

AH-01                        -> AH-11
AH-11                        -> AH-12, AH-13
AH-11 + AH-12                -> AH-16
AH-11 + AH-13                -> AH-14

AH-04 + AH-09 + AH-11 + AH-13         -> AH-15
AH-10 + AH-11 + AH-15                 -> AH-17
AH-13 + AH-16                         -> AH-18
AH-15 + AH-17 + AH-18                 -> AH-19
AH-10 + AH-15 + AH-18                 -> AH-20

AH-03 + AH-06 + AH-09 + AH-10         -> AH-21
AH-16 + AH-17 + AH-18 + AH-21         -> AH-22
AH-12 + AH-16 + AH-17                 -> AH-23
AH-22 + AH-23                         -> AH-24
AH-14 + AH-19 + AH-20 + AH-22 + AH-24 -> AH-25
```

With multiple engineers, work on the calendar negotiation track (AH-05 onward)
can proceed in parallel with the AI security and state-store tracks after shared
contracts and authorization decisions are agreed.

AH-15's orchestration contract may be developed against approval/policy stubs,
but enabling mutations requires the real AH-16/AH-18 implementations. The MCP
spike, transport/authentication choice, database provisioning, and conversion
atomicity are delivery prerequisites, not deferred hardening. Unrelated cleanup
of old root-document references is outside this feature backlog.
