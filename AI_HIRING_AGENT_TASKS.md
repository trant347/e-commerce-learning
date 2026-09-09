# AI Hiring Agent Implementation Backlog

> Tracking companion to [`AI_HIRING_AGENT_SPEC.md`](AI_HIRING_AGENT_SPEC.md).
>
> Each task is scoped for one mid-level engineer to complete in approximately
> one to two weeks, including implementation, tests, documentation, and review
> fixes. Estimates assume the engineer is familiar with the affected service.

## How to use this backlog

- Change `[ ]` to `[x]` when a task meets all acceptance criteria.
- Complete dependencies before starting a task unless the task explicitly says
  it can be developed against a stub.
- Create one pull request per task. Avoid combining tasks across service
  boundaries unless a small contract change requires it.
- Preserve existing direct booking and chat flows throughout delivery.
- Treat authentication, authorization, approval, idempotency, and state-machine
  requirements as completion criteria, not follow-up hardening.

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

### [ ] AH-01 - Authenticate AI assistant requests

**Estimate:** 1 week  
**Dependencies:** None  
**Primary service:** `ai-assistant-service`

**Scope**

- Configure JWT validation using the marketplace issuer/signing configuration.
- Require authentication on user-specific chat and agent-facing endpoints.
- Derive username and roles exclusively from validated claims.
- Stop using `ChatRequest.UserId` as an authorization identity.
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

- Define the delegated token claims: actor, audience, scopes, agent run ID, and
  expiration.
- Issue short-lived, audience-restricted tokens in `ai-assistant-service`.
- Attach authorization in the MCP transport, never in LLM-visible arguments.
- Validate delegated tokens and scopes in `calendar-service`.
- Add configuration and secret handling without logging tokens.

**Acceptance criteria**

- Calendar MCP calls receive the authenticated requester identity.
- Missing, expired, wrong-audience, and insufficient-scope tokens are rejected.
- Service credentials alone cannot authorize user mutations.
- Tokens never appear in prompts, tool schemas, tool results, or logs.
- Integration tests prove actor and scope propagation end to end.

### [ ] AH-03 - Add the calendar-service MCP server and read tools

**Estimate:** 1-2 weeks  
**Dependencies:** AH-02  
**Primary service:** `calendar-service`

**Scope**

- Add an MCP server using the repository's existing MCP conventions.
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
- Discover and invoke the calendar MCP read tools.
- Forward the request-scoped delegated identity.
- Preserve current chat behavior for booking and availability questions.
- Surface authorization and temporary-service failures honestly.

**Acceptance criteria**

- Authenticated users can query only their own booking data through chat.
- The local duplicate booking implementation is no longer registered.
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

**Acceptance criteria**

- Offer revisions have monotonically increasing versions.
- Existing revisions cannot be overwritten through repository APIs.
- Participant, status, timestamp, and expiration fields round-trip correctly.
- Persistence tests cover creation, retrieval, concurrent updates, and history.

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

**Acceptance criteria**

- Invalid transitions return typed domain errors.
- Only the correct participant can perform requester or TaskMaster actions.
- Concurrent updates cannot overwrite a newer offer.
- All prices and times used by transitions come from validated structured input.
- Unit tests cover every valid transition and important rejection path.

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

**Acceptance criteria**

- Only the authoritative TaskMaster owner can respond.
- A stale UI action returns a conflict and reloads current terms.
- The UI never displays a pending request as completed.
- API, React, and focused browser/component tests cover the primary flow.

### [ ] AH-09 - Expose negotiation MCP tools

**Estimate:** 1-2 weeks  
**Dependencies:** AH-02, AH-06  
**Primary service:** `calendar-service`

**Scope**

- Add `get_negotiation`, `list_my_negotiations`, `start_negotiation`,
  `revise_negotiation_offer`, `accept_counteroffer`,
  `decline_counteroffer`, and `cancel_negotiation`.
- Require delegated identity, appropriate scopes, expected versions, and stable
  action IDs for mutations.
- Return structured results and machine-readable error codes.

**Acceptance criteria**

- The requester agent cannot execute TaskMaster actions.
- Repeating an action ID with identical arguments returns the prior result.
- Reusing an action ID with different arguments is rejected.
- Version conflicts return the current negotiation version.
- Integration tests cover authorization, idempotency, and conflict behavior.

### [ ] AH-10 - Publish negotiation events and notifications

**Estimate:** 1-2 weeks  
**Dependencies:** AH-06  
**Primary services:** `calendar-service`, `notification-service`

**Scope**

- Publish versioned events for offered, countered, agreed, declined, expired,
  and cancelled negotiations.
- Write state changes and event outbox records atomically.
- Add notification-service consumers and user-facing notification types.
- Preserve trace context and explicit Kafka offset handling.

**Acceptance criteria**

- A committed transition produces exactly one logical event.
- Retries do not duplicate persisted user notifications.
- Events identify the negotiation, participants, version, and correlation IDs.
- Failed processing follows the existing retry/dead-letter conventions.
- Integration tests cover publication and consumption of each event type.

---

## Phase 3 - Durable agent harness

### [ ] AH-11 - Add the agent state store

**Estimate:** 1-2 weeks  
**Dependencies:** AH-01  
**Primary service:** `ai-assistant-service`

**Scope**

- Select and configure the AI service's persistent store.
- Add `AgentRun`, `HiringGoal`, `AgentAction`, `ApprovalRequest`, and
  `AgentEventCheckpoint`.
- Add indexes and optimistic concurrency where concurrent resumption is possible.
- Keep credentials and sensitive free-form content out of persisted action data.

**Acceptance criteria**

- Agent state survives service restarts.
- Runs and approvals are queryable only by their authenticated owner.
- Action IDs are unique and event checkpoints prevent duplicate processing.
- Persistence tests cover concurrency, uniqueness, and sanitized storage.

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

**Acceptance criteria**

- Users cannot access or mutate another user's run.
- Cancellation is idempotent and cannot reopen a terminal run.
- API responses distinguish waiting, active, completed, failed, and cancelled.
- Controller/integration tests cover lifecycle and authorization.

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
**Primary service:** `ai-assistant-service`

**Scope**

- Replace the fixed two-message history limit.
- Assemble context from current structured state, recent messages, relevant
  earlier messages, tool results, and a rolling summary.
- Enforce a configurable token budget.
- Treat structured state and current tool results as authoritative.

**Acceptance criteria**

- Long conversations remain within the configured context limit.
- Restarted runs rebuild useful context from persisted data.
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
- Persist each action and advance only from verified tool results.

**Acceptance criteria**

- Every transition is performed by deterministic application code.
- The loop pauses cleanly for user input, approval, or TaskMaster response.
- Failed and malformed model/tool results never become successful transitions.
- Restarting during an action does not duplicate the external mutation.
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

- Consume relevant negotiation events with inbox/checkpoint deduplication.
- Load the referenced run and ignore terminal runs.
- Re-fetch authoritative negotiation state before continuing.
- Resume the deterministic loop from persisted state.

**Acceptance criteria**

- Duplicate and out-of-order events do not duplicate actions.
- Events act only as wake-up signals; payloads do not replace domain reads.
- A restart between receiving and processing an event is recoverable.
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
**Dependencies:** AH-06  
**Primary service:** `calendar-service`

**Scope**

- Add `create_booking_from_agreement` application logic and MCP tool.
- Verify both parties agreed to the same offer version.
- Re-check slot availability and use server-side negotiated terms.
- Store `NegotiationId` on the booking and enforce uniqueness.
- Publish the booking-created negotiation event.

**Acceptance criteria**

- One negotiation can produce at most one booking.
- Retrying the same action returns the existing booking.
- Client- or model-supplied replacement price and schedule are ignored/rejected.
- A slot conflict creates no booking and returns a machine-readable conflict.
- Concurrency tests prove exactly-once behavior.

### [ ] AH-22 - Orchestrate final approval and booking creation

**Estimate:** 1-2 weeks  
**Dependencies:** AH-16, AH-17, AH-18, AH-21  
**Primary services:** `ai-assistant-service`, `frontend`

**Scope**

- Request final approval for the exact agreed terms and booking creation.
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

**Acceptance criteria**

- Reloading the page reconstructs state from the agent-run API.
- Users can clearly tell who or what the run is waiting for.
- Failed or pending calls are never shown as successful.
- Component and browser tests cover the primary lifecycle.

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
**Dependencies:** AH-19, AH-20, AH-22, AH-24  
**Primary services:** Cross-service

**Scope**

- Add metrics for run outcomes, wait time, tool calls, approvals, rounds,
  conflicts, prevented duplicates, and booking conversion.
- Correlate traces and sanitized logs by agent run, negotiation, booking, action,
  and event IDs.
- Add replayable/fake-model scenarios and the specification's end-to-end cases.
- Add fault injection for timeout, duplicate event, invalid model output, and
  restart/resume behavior.

**Acceptance criteria**

- Operators can trace one hiring run across AI, calendar, Kafka, and booking.
- Prompts, JWTs, payment data, and sensitive messages are not logged by default.
- All eight end-to-end scenarios in the specification pass.
- A deterministic test proves the model cannot bypass stored policy.
- Deployment and operational documentation describes failure diagnosis.

---

## Recommended implementation order

```text
AH-01 -> AH-02 -> AH-03 -> AH-04

AH-05 -> AH-06 -> AH-07
                  +-> AH-08
                  +-> AH-09
                  +-> AH-10

AH-01 -> AH-11 -> AH-12 -> AH-16
                  +-> AH-13 -> AH-14

AH-04 + AH-09 + AH-11 + AH-13 -> AH-15
AH-10 + AH-11 + AH-15         -> AH-17
AH-13 + AH-16                 -> AH-18
AH-15 + AH-17 + AH-18         -> AH-19
AH-10 + AH-15 + AH-18         -> AH-20

AH-06                         -> AH-21
AH-16 + AH-17 + AH-18 + AH-21 -> AH-22
AH-12 + AH-16 + AH-17         -> AH-23
AH-22 + AH-23                 -> AH-24
AH-19 + AH-20 + AH-22 + AH-24 -> AH-25
```

With multiple engineers, work on the calendar negotiation track (AH-05 onward)
can proceed in parallel with the AI security and state-store tracks after shared
contracts and authorization decisions are agreed.
