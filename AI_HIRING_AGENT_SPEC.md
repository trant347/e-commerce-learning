# AI Hiring and Negotiation Agent - Specification

> Status: **Proposed**
>
> Scope: Evolve `ai-assistant-service` from a request/response tool-calling chatbot into a constrained agent that can help a requester find, negotiate with, and hire a TaskMaster.

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
| Artificially short chat context | Only the last two user/assistant history messages are currently included, although the upgraded workstation can support a larger local-model context | The limit should be removed or made configurable, but conversation history still cannot be the authoritative record of hiring constraints or approvals |
| No durable agent state | The service has no workflow database or persisted plan | Restarts and new chat sessions lose negotiation progress |
| Fixed tool loop | A request is limited to five tool-calling rounds | It supports short lookups, not long-running business workflows |
| No trusted user identity | `ChatRequest.UserId` is client-supplied and is not used as an authenticated identity | The assistant cannot safely act on behalf of a user |
| No delegated authorization | Remote MCP tools are discovered through application-lifetime connections without per-user authorization context | State-changing tools cannot determine which user authorized an action |
| Calendar access is read-only and incomplete | `get_bookings` only safely retrieves a booking when an ID is supplied | The assistant cannot list the caller's bookings, check availability, or create a booking |
| No hiring tools | Product MCP tools search and read TaskMaster data only | The assistant can recommend but cannot hire |
| No negotiation domain | A booking has one `OfferedRatePerHour`, followed by accept or decline | There is no counteroffer, revision history, expiration, or negotiation policy |
| No event-driven resumption | The assistant does not consume negotiation or booking response events | A TaskMaster response cannot wake or resume an agent run |
| No approval model | Tool execution is controlled mainly by the system prompt | A model mistake could trigger an unintended write once mutation tools are added |
| No action idempotency | There is no agent action ID or idempotency key | Retries could create duplicate offers or bookings |
| No deterministic policy enforcement | Budget and approval boundaries are not enforced outside the LLM | Prompt instructions alone are insufficient for transactional actions |
| No auditable delegation record | The system does not record why an action was selected or who approved it | Disputes and operational debugging would be difficult |

The current assistant is therefore a **tool-using chatbot**, not an agent. It can reason and call tools during one response, but it cannot own a durable goal or safely continue work over time.

### 2.3 Chat context versus agent memory

The current `MaxChatMemorySize = 2` restriction should be removed or replaced with a configurable context strategy. A larger workstation can support a larger model and context window, allowing the assistant to include substantially more conversation history.

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
11. TaskMaster accepts, declines, or counters through the normal application UI/API.
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

TaskMaster actions should use authenticated REST/UI endpoints or separately authorized tools. The requester agent must never call a tool as the TaskMaster.

### 11.3 Tool requirements

Every state-changing tool must:

- Derive actor identity from authenticated execution context.
- Accept an `actionId` or idempotency key generated outside the LLM.
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

---

## 12. Authentication and Delegated Authorization

### 12.1 User authentication

`ai-assistant-service` must validate the same JWT used by protected marketplace services. `ChatRequest.UserId` must not be trusted for authorization.

The authenticated username and roles must come from validated claims.

### 12.2 MCP execution context

The current application-lifetime MCP connections do not provide sufficient per-user delegation for mutation tools.

The implementation must provide a request-scoped execution context without placing credentials in LLM-visible tool arguments. Acceptable designs include:

- A user-delegated access token attached by the MCP transport per call.
- A short-lived internal delegation token issued by `ai-assistant-service`, containing actor, audience, scope, agent run ID, and expiration.

The delegation token must:

- Be signed.
- Be short-lived.
- Be audience-restricted to calendar-service.
- Contain only the scopes required for the action.
- Be validated by calendar-service.
- Never be included in prompts, tool schemas, tool results, or application logs.

Service credentials alone are insufficient because calendar-service must know which user authorized the action.

### 12.3 Authorization principle

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
4. Generate or reuse a stable action ID.
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

### 20.3 End-to-end scenarios

1. User approves an offer; TaskMaster accepts; booking is created.
2. TaskMaster counters within the automatic policy; agent responds.
3. TaskMaster counters above the maximum; agent blocks and asks the user.
4. User rejects a counteroffer.
5. Offer expires while the agent is offline.
6. Calendar-service times out after accepting an action; retry does not duplicate it.
7. An unauthorized user attempts to access another user's run or negotiation.
8. The model attempts to exceed the stored budget; policy enforcement blocks it.

---

## 21. Delivery Plan

### Phase 1 - Secure calendar tools

- Add JWT validation to `ai-assistant-service`.
- Define delegated authorization for MCP calls.
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

- Add the AI service state store.
- Add agent runs, actions, approvals, and event checkpoints.
- Replace the fixed two-message limit with configurable, token-budgeted context assembly.
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
