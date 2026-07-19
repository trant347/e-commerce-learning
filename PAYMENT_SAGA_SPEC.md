# Booking Payment — Durable Orchestration (Proposed)

> Status: **Proposed / not implemented.** Extends `BOOKING_PAYMENT_SPEC.md`, which documents the current fully-synchronous, no-durable-state HTTP flow.
>
> **Recommendation: keep the call to payment-service synchronous (HTTP), but add a durable saga/outbox record around it.** Full async, Kafka-mediated orchestration is documented in the Appendix as the natural next step *if* scale/reliability needs later demand it — not adopted now.

---

## Why change anything at all

Today, `POST /api/booking/{id}/pay` calls payment-service synchronously over HTTP and blocks until it responds (see `BOOKING_PAYMENT_SPEC.md`, "Architecture"). The gap isn't the synchronous call itself — it's that there is **no durable record of "a payment attempt is in flight"**. If calendar-service crashes after payment-service approves the charge but before the booking is written as `COMPLETED`, money moved but the booking silently never reflects it, with nothing to reconcile against.

A synchronous call and a durable saga are independent decisions:
- **Orchestration vs. choreography** — who decides the next step. (Already decided: calendar-service orchestrates, per `BOOKING_PAYMENT_SPEC.md`'s existing "owner of the state machine owns orchestration" principle.)
- **Sync vs. async transport** — how each step call is made.
- **Durable vs. in-memory saga state** — whether "attempt in progress" survives a crash.

This spec's recommendation only changes the third: add durable saga state. It deliberately leaves the transport synchronous.

---

## Recommended design: synchronous call + durable `SagaState`

```
Frontend            calendar-service (orchestrator)              payment-service
   |  POST /pay              |                                        |
   |------------------------>|                                        |
   |                         | 1. validate caller/status/amount       |
   |                         | 2. write SagaState{STARTED, sagaId}    |
   |                         | 3. HTTP POST /api/payment/process ---->|
   |                         |                                        | 4. process charge
   |                         |<---------------------------------------|
   |                         | 5a. APPROVED + amount matches:         |
   |                         |     booking -> COMPLETED               |
   |                         |     SagaState -> COMPLETED             |
   |  200 OK                 |     notify TaskMaster                  |
   |<------------------------|                                        |
   |                         | 5b. DECLINED / mismatch / exception:   |
   |                         |     SagaState -> FAILED                |
   |  402 / 502               |     booking stays IMPLEMENTED          |
   |<------------------------|                                        |
```

This is almost exactly today's flow, plus step 2 (write saga state **before** calling out) and a recovery job that reconciles any `SagaState` left `STARTED` after a crash.

### New components (recommendation)

| Component | Location | Purpose |
|---|---|---|
| `SagaState` (Mongo collection) | calendar-service | `{ sagaId, bookingId, status: STARTED\|COMPLETED\|FAILED, requestedAmount, createdAt, updatedAt, failureReason }` |
| Reconciliation job | calendar-service, runs on startup + periodically | Finds `SagaState` rows stuck in `STARTED` beyond a threshold (e.g. process crashed mid-call); calls payment-service to check the outcome and finishes the transition, or marks `FAILED` if payment-service has no record of it |
| Idempotency key (`sagaId`) sent to payment-service | Both services | payment-service dedupes on `sagaId` so a retried request (from the reconciliation job or a frontend retry) can't double-charge |
| `GET /api/payment/transaction/{sagaId}` | payment-service | New lookup endpoint the reconciliation job needs to check "did this charge actually happen?" (previously flagged as future work in `BOOKING_PAYMENT_SPEC.md`) |

### Why this is the right starting point
- Solves the actual gap (crash recovery, idempotency) without adding a message bus, new topics, or consumers.
- No frontend change — `/pay` keeps returning `200`/`402`/`502` synchronously, so `PayBooking.tsx` is untouched.
- The `SagaState` record and `sagaId` concept are **exactly** what a future Kafka-based saga needs too — this isn't throwaway work, it's the foundation (see Appendix migration path).

### Pros / cons

**Pros**
1. Small, targeted change — days not weeks; no new infrastructure (no Kafka topics/consumers for this flow).
2. Closes the real gap: crash mid-flight is now recoverable via reconciliation instead of silently lost.
3. Idempotency key prevents double-charging on retries, closing a gap the current spec explicitly called out as unaddressed.
4. Zero frontend/UX impact.
5. Forward-compatible: the saga state and idempotency key carry over unchanged if you later move to Kafka.

**Cons**
1. Still blocks a request thread on payment-service's response time — doesn't help if payment-service becomes slow/high-latency at scale.
2. Reconciliation job adds a bit of operational surface (needs monitoring, but far less than a full message bus).
3. Doesn't solve availability decoupling — if payment-service is down, the caller still gets an immediate failure rather than a durably queued retry.
4. Requires a new payment-service endpoint (`GET /transaction/{sagaId}`) that doesn't exist today.
5. If calendar-service's own saga store (Mongo) is unavailable when writing the initial `STARTED` row, `/pay` fails closed with `503` before any charge is attempted (see `BookingController.Pay`) — correct behavior, but it is itself a dependency the flow can't operate without.
6. `SagaState` currently has no TTL/archival, so the collection grows unbounded (terminal `COMPLETED`/`FAILED` rows are never pruned) — deferred hardening, tracked as a follow-up (TTL index on `updatedAt`, partial filter on terminal statuses). **Implemented**: see below.

### Reconciliation job hardening: claim-based lock + Mongo topology sanity check

Once calendar-service is horizontally scaled, every replica runs its own `SagaReconciliationWorker` on the same timer. Without coordination, N replicas would all pick up the same stuck saga on the same pass — each redundantly calling payment-service and (worse) each attempting `CompletePaymentAsync`/`FailAsync` against the same booking. Two changes address this:

- **Claim-based lock** (`ISagaStateService.TryClaimAsync`): before doing any work on a stuck saga, a worker atomically claims it via a single-document `FindOneAndUpdate` that only matches if the saga is still `STARTED` and `ReconciliationClaimedAt` is either unset or older than the claim TTL (default 45s, configurable via `SagaReconciliation:ClaimTtlSeconds` — kept shorter than the 60s poll interval so a replica that crashes mid-claim doesn't block reconciliation past the next pass). If the claim fails (another replica already holds it), the worker skips that saga for the current pass and logs at debug level. This relies on MongoDB's single-document atomicity, which holds as long as every replica points at the *same* logical Mongo deployment (today: one `mongo` container per `docker-compose.yml`; in a replica set/sharded cluster, still safe as long as all replicas share one connection string/cluster). Nothing explicitly clears `ReconciliationClaimedAt` back to null — for sagas left `STARTED` (retryable outcomes) the claim simply becomes stale and reclaimable by the time the next poll runs (claim TTL < poll interval), and for terminal outcomes (`FAILED`/`COMPLETED`) the claim field is moot since `TryClaimAsync`'s filter requires `Status == STARTED`.
- **Mongo topology sanity check**: `MongoDBService` now logs the resolved server list, replica set name (or "standalone"), and database name at startup (`MongoDBService.DescribeTopology`, unit-tested independent of a live Mongo connection). This can't *prevent* a misconfigured deployment where replicas accidentally point at different, unsynced Mongo instances (that's a deployment bug, not something app code can detect for certain) — but it makes such a misconfiguration visible in logs instead of silently causing duplicate/racy reconciliation work.

### SagaState retention (TTL index)

To keep the `SagaState` collection from growing unboundedly, `SagaStateService` creates a partial TTL index on `updatedAt` that only matches terminal rows (`Status` in `COMPLETED`/`FAILED`); MongoDB's background TTL monitor removes matching documents once they're older than the configured retention window (`SagaState:RetentionDays`, default 90). `STARTED` rows are explicitly excluded from the filter — they're either actively in-flight or the reconciliation job's responsibility, never something we want Mongo to delete out from under it. If a prior deploy already created this index with different options, Mongo rejects the conflicting `CreateOne` call; `SagaStateService` catches and logs that (`MongoCommandException`) rather than crashing calendar-service's startup, so an operator can resolve the index-options mismatch manually without an outage.

### Manually testing failure/recovery scenarios

Two deterministic, debugger-free hooks exist for exercising the failure paths above via ordinary HTTP requests:

- **Simulated decline**: submit card number `4000000000000002` (`PaymentService.SimulatedDeclineCardNumber`) to `/api/payment/process` (directly, or via `/pay`). payment-service returns a `DECLINED` transaction instead of always approving, exercising `BookingController.Pay`'s decline branch and (if the saga is later force-stuck) the reconciliation job's declined/mismatch handling.
- **Simulated post-charge crash**: set `Faults:SimulatePostChargeCrash=true` (e.g. env var `Faults__SimulatePostChargeCrash=true`) on calendar-service. After a charge succeeds but before the saga/booking are completed, `BookingController.Pay` throws `SimulatedPostChargeCrashException` — deliberately uncaught, so no response ever reaches the caller (mirroring a real crash) and the saga is left `STARTED` for `SagaReconciliationWorker` to pick up on its next pass. **Must remain `false` in production** — it is a test-only fault injector, not a production feature flag.
- **Simulated payment-service outage**: `docker compose stop payment-service` (or `docker kill`) while calling `/pay` exercises the "unreachable" paths (503 from calendar-service if it happens before `StartAsync`'s payment call returns; reconciliation's "unreachable → leave STARTED, retry" branch if triggered during the reconciliation job's own lookup) — no code change needed.

### Bug fix: MongoDB.Driver 3.x Guid serialization

Manually testing the above surfaced a real bug: writing the first `SagaState` row against a live MongoDB instance threw `BsonSerializationException: GuidSerializer cannot serialize a Guid when GuidRepresentation is Unspecified`. MongoDB.Driver 3.x removed the implicit "Unspecified" Guid representation and deprecated the old per-property `[BsonGuidRepresentation]` attribute fix from 2.x — the correct 3.x fix is a single global serializer registration (`BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard))`), done once via `MongoDbGuidSupport.Register()` at the very top of calendar-service's `Program.cs`, before any Mongo document is (de)serialized. This affects every `Guid` property in the app (currently just `SagaState.SagaId`), not only the one that first surfaced it. Covered by a regression test (`MongoDBServiceTests.RegisteredGuidSerializer_RoundTripsSagaStateSagaId`) that serializes/deserializes a `SagaState` directly — no live Mongo connection needed to catch this class of bug in future.

### Bug fix: initial payment call ambiguity swallowed by immediate FAILED

Manually stopping payment-service mid-request to test recovery surfaced a second, more serious bug: `PaymentApiClient.ProcessPaymentAsync` (the *initial* charge call from `BookingController.Pay`) collapsed "definitely unreachable" and "confirmed no charge" into the same `null` return value. `BookingController.Pay` then immediately called `FailAsync` on any `null` and returned — so the saga was marked `FAILED` right away instead of being left `STARTED`, meaning:
1. The reconciliation job never got a chance to see it (it only sweeps `STARTED` sagas), so testing showed 0 stuck sagas even though a real failure had just occurred.
2. Worse, if payment-service actually processed the charge but the response was lost in transit (e.g. it crashed right after replying), the saga would be wrongly marked `FAILED` while money was already taken — a real correctness bug, not just a UX issue.

This was inconsistent with `GetTransactionBySagaIdAsync` (used by reconciliation), which already correctly distinguished "unreachable/error" (`PaymentServiceUnavailableException`, ambiguous) from "confirmed 404" (safe to mark `FAILED`). **Fix**: `ProcessPaymentAsync` now throws the same `PaymentServiceUnavailableException` on any connection failure or non-success response, instead of returning `null`. `BookingController.Pay` catches it, does **not** call `FailAsync`, and leaves the saga `STARTED` — exactly like the "unreachable" branch during reconciliation lookups — so `SagaReconciliationWorker` resolves it authoritatively (via the sagaId lookup) once payment-service is reachable again, within `StuckThresholdSeconds` (default 30s).

**Client guidance (also reflected in the 502 response body)**: if you see this error, **do not retry the payment**. Each `/pay` call mints a brand-new `sagaId`, so an immediate retry is a genuinely distinct charge attempt — payment-service's dedupe only protects against the *same* sagaId being replayed, not against two different sagaIds for the same booking. If the original ambiguous attempt actually succeeded, retrying would cause a real double charge. Instead, wait ~30–60s and refresh the booking: it will resolve automatically to either `COMPLETED` (the reconciliation job confirmed the charge went through) or back to `IMPLEMENTED`/payable-again (confirmed no charge was ever recorded) — only then is it safe to pay again if still unpaid.

### Frontend: persistent "payment pending" guard (survives page reload)

The 502 response above tells the user not to retry, but that guidance previously lived only in transient React state — if the user closed and reopened the browser (or the tab), the warning was gone and the Pay button was enabled again, with nothing stopping a second (genuinely risky) charge attempt.

**Fix — server-derived signal instead of client-only state:**
- `ISagaStateService.GetLatestByBookingIdAsync(bookingId)` (backed by a new `(BookingId, CreatedAt desc)` Mongo index) returns the most recently created saga for a booking, if any.
- `BookingController.Get(id)` now populates a transient `Booking.PaymentPending` field (`[BsonIgnore]`, not persisted) — `true` only when the booking is `IMPLEMENTED` **and** its latest saga is still `STARTED` (i.e. genuinely ambiguous/in-flight, not resolved). This costs one extra Mongo lookup only for `IMPLEMENTED` bookings, not every booking read.
- The frontend (`PayBooking.tsx`) checks `booking.paymentPending` on every load — including a fresh mount after closing/reopening the browser — and if true, hides the payment form entirely and shows a persistent "Your payment is being processed... do not submit another payment" message instead, with a link back home. Because this is derived from server state on every `GET`, it survives reloads and multiple devices/tabs, unlike the old submit-time-only message.
- **Server-side backstop**: `BookingController.Pay` also now checks `GetLatestByBookingIdAsync` itself, *before* starting a new saga, and returns `409 Conflict` ("A payment for this booking is already being processed...") if the latest saga is still `STARTED`. This protects against a genuine double-charge attempt even if the frontend check is stale, bypassed, or the user has two tabs/devices open — the check that gates the UI is the same one enforced server-side, so there's no way to race past it from the client.

---

## Appendix: full Kafka-mediated saga (future scaling path)

Adopt this **only when** you actually observe or anticipate one of:
- payment-service call volume high enough that blocking request threads on it becomes a throughput problem,
- payment-service (or a future real payment provider) has meaningful latency/downtime that direct calls can't absorb,
- a need to decouple deploys/availability of calendar-service and payment-service in time, not just logically.

### Flow
```
Frontend            calendar-service (orchestrator)         payment-service         notification-service
   |  POST /pay              |                                     |                        |
   |------------------------>|                                     |                        |
   |                         | 1. validate + write SagaState        |                        |
   |  202 Accepted (sagaId)  | 2. publish payment-requests topic    |                        |
   |<------------------------|------------------------------------>|                        |
   |                         |                                     | 3. process charge      |
   |                         |    publish payment-results topic     |                        |
   |                         |<------------------------------------|                        |
   |                         | 4a. APPROVED + amount matches:        |                        |
   |                         |     booking -> COMPLETED, notify ------------------------->    |
   |                         | 4b. DECLINED / mismatch / timeout:    |                        |
   |                         |     SagaState -> FAILED, notify ------------------------->     |
```

### Additional components beyond the sync+outbox design
| Component | Purpose |
|---|---|
| `payment-requests` / `payment-results` Kafka topics | Async request/response, mirrors `NotificationProducer`/`UserEventConsumerWorker` conventions already in `calendar-service/MessageQueue` |
| `PaymentRequestProducer`, `PaymentResultConsumerWorker` (calendar-service) | Same JSON-camelCase + `traceparent` propagation pattern as existing producers/consumers |
| `PaymentRequestConsumerWorker` (payment-service) | New — payment-service is currently REST-only, gains its first Kafka consumer |
| Saga timeout watchdog | Replaces simple reconciliation-on-restart with continuous scanning for stuck sagas |

### Migration cost from the recommended design to this appendix

Because `SagaState` and the `sagaId` idempotency key already exist from the recommended design, this is a **targeted backend swap**, not a redesign:

| Item | Cost |
|---|---|
| Replace HTTP call with `PaymentRequestProducer.PublishAsync` | Small — hours |
| Add `PaymentResultConsumerWorker` in calendar-service | Small-medium — ~1 day incl. tests, mirrors existing worker |
| Add Kafka consumer in payment-service (net-new capability) | Medium — new infra in a currently REST-only service |
| Flip `/pay` from sync 200/402 to `202 Accepted` | Small on backend |
| **Frontend**: `PayBooking.tsx` from "await response" to "poll or await notification" | **Largest cost** — real UX rewrite: loading/pending states, polling or websocket, retry messaging |
| Saga timeout watchdog (upgrade from restart-time reconciliation) | Small-medium |
| Ops: topic lag monitoring, DLQ handling | Medium, recurring operational cost |

**Bottom line on cost to scale up later**: moderate, and concentrated in the frontend UX change (sync feedback → async feedback) plus new operational monitoring — not in redoing the core business/idempotency logic, which is why building the recommended sync+outbox design first is the cheaper overall path rather than jumping straight to Kafka.

---

## Security note (carried over, still applies either way)

Raw card details (`cardNumber`, `cvv`) currently pass over plain HTTP calendar-service → payment-service. If the appendix's Kafka path is ever adopted, do **not** put raw card data on a Kafka topic — tokenize first (a lightweight synchronous exchange for a short-lived token) and only publish the token. This applies regardless of which transport is used for the charge step itself.

---

## Open questions (resolved during implementation)

1. ~~Is the `GET /api/payment/transaction/{sagaId}` lookup endpoint acceptable to add to payment-service now, ahead of full Kafka adoption, purely to support reconciliation?~~ **Resolved: yes** — added in migration step 3 (`PaymentController.GetTransactionBySagaId`).
2. ~~What's an acceptable "stuck saga" threshold...?~~ **Resolved: 30s** (configurable via `SagaReconciliation:StuckThresholdSeconds`), as proposed.
3. ~~Should reconciliation run only at calendar-service startup, or also on a periodic timer...?~~ **Resolved: both** — `SagaReconciliationWorker` runs an immediate pass on startup, then repeats every 60s (configurable via `SagaReconciliation:PollIntervalSeconds`), so sagas stuck without a restart are still caught.

## Migration plan

Each step below is only considered done once its own automated test(s) are added and passing (unit tests for services/dedupe logic, integration/controller tests where applicable) — see `payment-service.Tests`, `calendar-service.Tests`.

1. Add `SagaState` model + repository in calendar-service.
2. Add `sagaId` to the existing `POST /api/payment/process` request/response contract; add dedupe check in payment-service.
3. Add `GET /api/payment/transaction/{sagaId}` to payment-service.
4. Wrap the existing HTTP call in `CompletePaymentAsync` with saga-state writes (`STARTED` before, `COMPLETED`/`FAILED` after).
5. Add the reconciliation job (startup pass first; periodic timer if needed per open question #3).
6. **Only if/when scale demands it**: follow the Appendix's migration path to Kafka-mediated orchestration.

## TODO: migrate `/pay` to asynchronous Kafka orchestration

Implement these tasks in order. Keep the current synchronous HTTP flow available behind an
`AsyncPaymentsEnabled` feature flag until the asynchronous flow has been verified end to end.
Every task must include at least one automated test before it is considered complete.

The target business flow is now **pay before work**:

1. After the booking is accepted and its price is fixed, the requester funds escrow.
2. The money is held in a configured admin custody wallet, but a separate per-booking escrow
   ledger remains the source of truth for who owns the held funds.
3. The TaskMaster may perform the work only after escrow funding succeeds.
4. Uploading proof durably requests release of the escrow to the TaskMaster.
5. A permitted cancellation durably requests a refund from escrow to the requester.

### Task 1 — Define escrow-aware message and HTTP contracts

- [x] Add versioned `PaymentRequestedV1` and `PaymentResultV1` contracts in a shared
      `payment-contracts` project referenced by both services.
- [x] Use `sagaId` as the Kafka message key and idempotency/correlation identifier.
- [x] Identify every command with `bookingId`, `escrowId`, and one of `FUND_ESCROW`,
      `RELEASE_ESCROW`, or `REFUND_ESCROW`.
- [x] Include amount, currency, payer, payee, and an optional payment-method token in
      `PaymentRequestedV1`; the token is required only for `FUND_ESCROW`.
- [x] Include the operation and escrow identifiers plus transaction id, amount, currency,
      status, and optional decline reason in `PaymentResultV1`.
- [x] Define the new `/pay` response as `202 Accepted` with `sagaId`, `escrowId`, `PENDING`
      status, and a status URL.
- [x] Add serialization and forward-compatibility tests for both V1 contracts.

Do not put a raw card number or CVV in Kafka. The contract version is for safe schema evolution;
it is unrelated to database optimistic concurrency.

### Task 2 — Introduce payment-method tokenization

- [ ] Replace raw card details in the asynchronous escrow-funding request with a short-lived,
      single-use payment-method token. Release and refund commands must not carry one.
- [ ] For the current simulation, add a tokenization boundary that validates the submitted card
      and returns an opaque token. A real deployment should use the payment provider's frontend
      SDK instead.
- [ ] Ensure logs, saga documents, outbox records, and Kafka messages never contain raw card
      numbers or CVVs.
- [ ] Add tests for valid, invalid, expired, and reused tokens.

### Task 3 — Add the escrow ledger and booking lifecycle

- [ ] Fix the booking amount and currency before escrow funding; `/pay` must not accept an amount
      supplied by the client.
- [ ] Add a PostgreSQL escrow record unique per booking with amount, currency, requester,
      TaskMaster, custody account, status, and funding/release/refund transaction ids.
- [ ] Use explicit escrow states such as `PENDING`, `FUNDED`, `RELEASED`, and `REFUNDED`, with
      compare-and-set transitions that reject release/refund before funding or after a terminal
      transfer.
- [ ] Treat the admin wallet only as the custody account; never infer a booking's escrow balance
      from the aggregate admin wallet balance.
- [ ] Prevent the TaskMaster from starting/submitting proof until escrow is `FUNDED`.
- [ ] Change proof upload from "send invoice" to "request escrow release".
- [ ] Define cancellation rules and allow a refund only while the escrow is funded and not
      released.
- [ ] Add tests for valid lifecycle transitions and every invalid/duplicate transition.

### Task 4 — Make each saga a durable enqueue operation

- [ ] Extend `SagaState` with `escrowId`, operation, payment request, dispatch status, attempt
      count, next-attempt time, and dispatch timestamps so it also acts as the command outbox.
- [ ] Atomically prevent more than one active saga for the same booking and operation.
- [ ] Persist the `STARTED` saga and pending request before attempting to publish anything.
- [ ] Return `503` without publishing when the saga/outbox write fails.
- [ ] Add tests for persistence failure and concurrent funding/release/refund requests.

Keeping the pending command in the saga document permits an atomic single-document Mongo write
without requiring multi-document transactions on the current standalone Mongo deployment.

### Task 5 — Change `/pay` to enqueue escrow funding and return immediately

- [ ] When `AsyncPaymentsEnabled` is true, validate the accepted booking and token, create the
      escrow plus durable `FUND_ESCROW` saga request, and return `202 Accepted` without calling
      `IPaymentApiClient.ProcessPaymentAsync`.
- [ ] Preserve ownership checks and reject unpaid-price changes, duplicate funding, work already
      started, and bookings that are not eligible for funding.
- [ ] Keep the synchronous implementation as the disabled-feature fallback during rollout.
- [ ] Add controller tests proving `/pay` returns saga/escrow ids and does not wait for or call
      payment-service directly.

### Task 6 — Publish pending escrow commands from calendar-service

- [ ] Add a `PaymentRequestOutboxWorker` that claims undispatched saga requests and publishes
      them to the `payment-requests` topic.
- [ ] Publish camel-case JSON, set `sagaId` as the message key, and propagate `traceparent`.
- [ ] Mark a request dispatched only after Kafka acknowledges publication.
- [ ] Retry publication failures with bounded exponential backoff and a claim lease so another
      calendar-service replica can recover abandoned work.
- [ ] Add tests for successful dispatch, Kafka failure, retry, and duplicate publication.

### Task 7 — Consume escrow commands transactionally in payment-service

- [ ] Add a `PaymentRequestConsumerWorker` using a dedicated consumer group and manual offset
      commits.
- [ ] Validate the schema version, operation, escrow state, amount, currency, payer, and payee
      before processing.
- [ ] For `FUND_ESCROW`, redeem the token and transfer requester funds into the admin custody
      wallet; for `RELEASE_ESCROW` and `REFUND_ESCROW`, transfer held funds from custody to the
      TaskMaster or requester without a payment-method token.
- [ ] Persist the wallet movement, payment transaction, and escrow state transition in one
      PostgreSQL transaction.
- [ ] Preserve the unique `SagaId` constraint so duplicate Kafka deliveries return the original
      result rather than moving money again.
- [ ] Lock wallets in deterministic user-id order to reduce deadlock risk.
- [ ] Commit the Kafka offset only after the database transaction succeeds.
- [ ] Add tests for funding, release, refund, insufficient funds, invalid escrow state, malformed,
      unsupported-version, and duplicate events.

### Task 8 — Add a transactional payment-result outbox

- [ ] Add a PostgreSQL `payment_result_outbox` table.
- [ ] Insert the payment transaction, wallet and escrow changes, and result-outbox row in the same
      database transaction.
- [ ] Add a worker that publishes undispatched rows to `payment-results` and marks them dispatched
      only after Kafka acknowledgement.
- [ ] Make result publication retryable and idempotent.
- [ ] Add tests proving rollback cannot leave wallet or escrow changes without a result and that
      an unpublished result is republished after restart.

### Task 9 — Consume escrow results in calendar-service

- [ ] Add a `PaymentResultConsumerWorker` with manual offset commits.
- [ ] Validate `sagaId`, `escrowId`, operation, amount, currency, and transaction id against the
      stored saga.
- [ ] On approved funding, mark the booking escrow-funded and notify both parties that work may
      begin.
- [ ] On approved release, complete the booking and notify the TaskMaster that funds were paid.
- [ ] On approved refund, mark the booking cancelled/refunded and notify the requester.
- [ ] For a declined operation, mark only that saga failed and leave the escrow in its previous
      authoritative state.
- [ ] Ignore already-applied duplicate results without repeating booking or notification changes.
- [ ] Leave recoverable failures uncommitted so Kafka can redeliver them.
- [ ] Add tests for funding, release, refund, declined, mismatched, duplicate, and out-of-order
      results.

### Task 10 — Trigger release and refund durably

- [ ] Make proof upload create a durable `RELEASE_ESCROW` saga/outbox request instead of sending
      an invoice or transferring funds synchronously.
- [ ] Make eligible cancellation create a durable `REFUND_ESCROW` saga/outbox request.
- [ ] Reject proof release or refund when escrow is not funded, another matching operation is
      active, or the escrow is already terminal.
- [ ] Keep proof metadata durable even if Kafka is unavailable so release can resume later.
- [ ] Add controller/service tests for release, refund, duplicate requests, and invalid states.

### Task 11 — Expose status and update the frontend

- [ ] Add a payment-status endpoint that returns `PENDING`, `COMPLETED`, or `FAILED` for the saga
      plus the current escrow state while enforcing booking ownership.
- [ ] Update `BookingService.pay` to accept the `202` response.
- [ ] Update the booking UI so payment occurs after acceptance and before work starts.
- [ ] Show durable pending/funded/release/refund states and poll with bounded backoff.
- [ ] On funding completion, show that money is safely held and work may begin; on release or
      refund completion, refresh the terminal booking state.
- [ ] On failure, show the reason and permit a retry only after the prior saga is terminal.
- [ ] Preserve pending behavior across page reloads using server state rather than only React
      state.
- [ ] Add frontend tests for funding, held-in-escrow, release, refund, decline, timeout, and reload.

### Task 12 — Harden recovery and operations

- [ ] Update reconciliation to distinguish requests that were never dispatched from requests
      dispatched but not yet completed.
- [ ] Do not mark a saga failed merely because Kafka or payment-service is temporarily
      unavailable.
- [ ] Add retry limits and dead-letter topics for permanently invalid request/result messages.
- [ ] Add structured logs and metrics for pending saga age, outbox backlog, retries, DLQ count,
      processing duration, escrow age/value by state, and Kafka consumer lag.
- [ ] Reconcile aggregate custody-wallet balances against the sum of all funded, unreleased
      escrow records and alert on any mismatch.
- [ ] Add crash-recovery tests for failures before request publication, during payment
      processing, before result publication, during calendar result application, and between
      proof persistence and release publication.

### Task 13 — Configure infrastructure and complete rollout

- [ ] Add `payment-requests`, `payment-results`, and their DLQ topics to `kafka-init`.
- [ ] Add topic, consumer group, retry, polling, and feature-flag configuration to both services
      and `docker-compose.yml`.
- [ ] Configure the admin custody account explicitly; never infer it from the first admin user.
- [ ] Verify funding, release, refund, multiple service replicas, and duplicate event delivery.
- [ ] Enable asynchronous payments by default after the new path is stable.
- [ ] Remove the synchronous payment-processing path and obsolete `IPaymentApiClient` methods
      only after rollback support is no longer required.
