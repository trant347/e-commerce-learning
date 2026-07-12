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

## Open questions

1. Is the `GET /api/payment/transaction/{sagaId}` lookup endpoint acceptable to add to payment-service now, ahead of full Kafka adoption, purely to support reconciliation?
2. What's an acceptable "stuck saga" threshold for the reconciliation job to treat a `STARTED` row as needing recovery (proposed: 30s, given payment-service has no external dependency itself today)?
3. Should reconciliation run only at calendar-service startup, or also on a periodic timer to catch sagas stuck without a restart?

## Migration plan

Each step below is only considered done once its own automated test(s) are added and passing (unit tests for services/dedupe logic, integration/controller tests where applicable) — see `payment-service.Tests`, `calendar-service.Tests`.

1. Add `SagaState` model + repository in calendar-service.
2. Add `sagaId` to the existing `POST /api/payment/process` request/response contract; add dedupe check in payment-service.
3. Add `GET /api/payment/transaction/{sagaId}` to payment-service.
4. Wrap the existing HTTP call in `CompletePaymentAsync` with saga-state writes (`STARTED` before, `COMPLETED`/`FAILED` after).
5. Add the reconciliation job (startup pass first; periodic timer if needed per open question #3).
6. **Only if/when scale demands it**: follow the Appendix's migration path to Kafka-mediated orchestration.
