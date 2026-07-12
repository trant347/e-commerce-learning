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
