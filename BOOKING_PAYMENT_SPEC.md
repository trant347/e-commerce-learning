# Booking Payment & Invoice Workflow — Specification

> Status: **Implemented.** Lives in `calendar-service` (orchestrator), `payment-service` (payment processor), `product-service` (proof-of-job file storage), `notification-service` (notifications), and the frontend (`SubmitProof.tsx`, `PayBooking.tsx`, `MyCalendar.tsx`, `IncomingBookings.tsx`).

## Overview

Once a TaskMaster has completed a job for an accepted booking, they upload proof of the work and send an invoice. The requester is notified, reviews the amount due, and pays with a credit card. The booking moves through two new terminal-ish states on top of the existing PENDING → ACCEPTED → DECLINED/CANCELLED flow:

```
PENDING → ACCEPTED → IMPLEMENTED → COMPLETED
                 (submit-proof)   (pay)
```

- **IMPLEMENTED**: TaskMaster submitted proof + invoice amount. Awaiting payment.
- **COMPLETED**: Requester paid. Terminal state.

---

## User Stories

| # | Actor | Action | Outcome |
|---|-------|--------|---------|
| 1 | TaskMaster | Opens Booking Details on an ACCEPTED booking (`MyCalendar.tsx`), clicks "Submit Proof & Send Invoice" | Redirected to `/booking/{id}/submit-proof` |
| 2 | TaskMaster | Uploads a proof file/image and enters the invoice amount | Booking → IMPLEMENTED; requester notified with the amount due |
| 3 | Requester | Clicks the notification (or navigates to `/booking/{id}/pay`) | Sees proof link + amount due; enters card number, expiry, CVV, name |
| 4 | Requester | Submits the payment form | Card details are verified through payment-service; booking → COMPLETED; TaskMaster notified that payment was received |
| 5 | Anyone | Tries to call `/pay` with a booking that isn't theirs, or isn't IMPLEMENTED, or with no invoice amount | `403` / `409` / `400` respectively — see **Authorization & validation** |

---

## API (calendar-service)

### Submit proof of job *(TaskMaster owner only)*
```
POST /api/booking/{id}/submit-proof
Authorization: ******
{ "proofFileUrl": "/products/image/abc123.jpg", "invoiceAmount": 150.00 }

200 OK       → booking moved to IMPLEMENTED, BOOKING_PAYMENT_REQUIRED notification sent to requester
400          → missing proofFileUrl or invoiceAmount <= 0
401          → no authenticated caller
403          → caller is not the TaskMaster owner
404          → no booking with that id
409          → booking is not ACCEPTED
```

### Pay the invoice *(requester only)*
```
POST /api/booking/{id}/pay
Authorization: ******
{ "cardNumber": "...", "expiryDate": "MM/YY", "cvv": "...", "ownerName": "..." }

200 OK       → booking moved to COMPLETED, BOOKING_PAYMENT_RECEIVED notification sent to TaskMaster
400          → missing card fields, or booking has no invoice amount
401          → no authenticated caller
402          → payment declined, or the processed amount didn't match the invoice amount
403          → caller is not the requester
404          → no booking with that id
409          → booking is not IMPLEMENTED
502          → payment-service unreachable
```

The proof file itself is uploaded separately to the existing `product-service` endpoint (`POST /products/image`), reusing infrastructure rather than building file storage into calendar-service. The resulting URL is passed to `submit-proof` as a plain string.

---

## Architecture — why calendar-service calls payment-service (not the other way around)

The frontend does **not** call payment-service directly, and payment-service does **not** call back into calendar-service. Calendar-service sits in the middle and orchestrates:

```
┌──────────┐  card details   ┌──────────────────┐  process payment   ┌─────────────────┐
│ Frontend │ ───────────────►│ calendar-service  │────────────────────►│ payment-service │
│ (payer)  │                 │                   │◄────────────────────│  (generic, no   │
└──────────┘                 │ 1. load booking   │  transaction result │  booking domain │
                              │ 2. check caller = │  (status, amount)   │  knowledge)     │
                              │    requester      │                     └─────────────────┘
                              │ 3. check status = │
                              │    IMPLEMENTED    │
                              │ 4. call payment-  │
                              │    service with   │
                              │    the *server's*  │
                              │    invoiceAmount   │
                              │ 5. verify APPROVED │
                              │    + amount match  │
                              │ 6. mark COMPLETED  │
                              └────────────────────┘
```

### Rejected alternative: payment-service calls calendar-service after processing

We considered having the frontend call payment-service directly, and have payment-service push the "paid" status into calendar-service once it verified the charge. This was rejected:

1. **Bidirectional coupling.** Today the dependency graph is one-directional (calendar-service → payment-service). The alternative creates a cycle, permanently tying the generic, reusable payment-service to the booking domain just so it can call back.
2. **Amount verification doesn't get simpler.** Payment-service would still need the *correct* invoice amount to charge. Either it trusts a client-supplied amount (the exact "trust the client" flaw described below), or it must first call calendar-service to fetch it — turning one hop into two (calendar-service → payment-service → calendar-service) instead of one.
3. **Auth context is awkward to carry.** Calendar-service's "only the requester may pay this booking" check relies on the caller's JWT. If payment-service calls calendar-service afterward, it must forward that identity or invent a service-to-service trust mechanism, for no real benefit.
4. **Reopens the attack surface we closed.** It requires exposing payment-service to the browser again via the BFF, which was deliberately removed (see below) so booking-domain checks can't be bypassed.
5. **Messier failure handling.** If the callback from payment-service to calendar-service fails after a successful charge, the booking never completes — now payment-service needs retry/idempotency logic it wasn't designed for.

**Principle applied:** the service that owns the entity/state machine (the booking) should own the orchestration and call out to its generic dependency (payment) — not the reverse.

---

## Security note: why the frontend can't just tell calendar-service "payment succeeded"

The original implementation had the frontend call payment-service directly (`POST /api/payment/process`) and then call calendar-service's `/pay` endpoint with just the resulting `paymentTransactionId`. This was insecure:

- payment-service has no persistence lookup (`GET /transaction/{id}` doesn't exist), so calendar-service had no way to verify that transaction id after the fact.
- A user could skip the card form entirely and call `POST /api/booking/{id}/pay` directly (devtools/curl) with a fabricated `paymentTransactionId`, and the booking would be marked COMPLETED — no money-equivalent ever moved.

**Fix:** the frontend now posts raw card details to calendar-service's `/pay` endpoint. Calendar-service — a trusted backend — forwards them to payment-service itself, using the invoice amount from **its own database**, not from the client. It then checks the returned transaction's `Status == APPROVED` and `Amount == booking.InvoiceAmount` before completing the booking. The frontend can no longer assert a payment outcome; only a real, server-verified transaction can move a booking to COMPLETED. The direct frontend → payment-service path (`routes/payment.js`, `paymentServices.tsx`, the `payment-service` BFF proxy entry) was removed entirely — payment-service is now reachable only from calendar-service on the internal docker network.

---

## Data model additions (`calendar-service/Model/Booking.cs`)

| Field | Type | Set when |
|---|---|---|
| `ProofFileUrl` | `string?` | `submit-proof` |
| `InvoiceAmount` | `decimal?` | `submit-proof` |
| `PaymentTransactionId` | `string?` | `pay` (approved payment-service transaction id) |
| `ImplementedAt` | `DateTime?` | `submit-proof` |
| `CompletedAt` | `DateTime?` | `pay` |

---

## Notifications

| Event | Recipient | `actionType` | Frontend route |
|---|---|---|---|
| `BOOKING_PAYMENT_REQUIRED` | Requester | `VIEW_PAYMENT_REQUEST` | `/booking/{id}/pay` |
| `BOOKING_PAYMENT_RECEIVED` | TaskMaster | `VIEW_INCOMING_BOOKING_REQUEST` | `/booking/{id}/submit-proof` (via incoming bookings) |

---

## Frontend

- `SubmitProof.tsx` (`/booking/:id/submit-proof`) — TaskMaster-only page; uploads the proof file to `product-service`, then calls `BookingService.submitProof()`.
- `PayBooking.tsx` (`/booking/:id/pay`) — requester-only page; shows amount due + proof link, collects card details, calls `BookingService.pay()` (which now goes to calendar-service, not payment-service).
- `MyCalendar.tsx` — Booking Details modal shows a "Submit Proof & Send Invoice" button when status is ACCEPTED, and displays invoice amount / proof link / payment status for IMPLEMENTED/COMPLETED bookings. The calendar's event query was widened to include ACCEPTED, IMPLEMENTED and COMPLETED bookings (previously ACCEPTED only) so these don't disappear from the calendar once proof is submitted.
- `IncomingBookings.tsx` — status colors and filter tabs extended to include IMPLEMENTED/COMPLETED.

---

## Deliberate simplifications (per product decision)

The user explicitly asked for a simple flow, not a production-grade payment system:

- payment-service has a single endpoint (`POST /api/payment/process`), always approves, and does no real card validation (no Luhn check, no expiry check). This is unchanged.
- payment-service has no concept of a `bookingId` — it stays a generic, reusable payment processor. The linkage between a booking and its payment lives entirely in calendar-service (`PaymentTransactionId` field).
- No idempotency key / duplicate-payment protection beyond the booking's own status guard (`IMPLEMENTED → COMPLETED` transition can only happen once, enforced by a status-guarded `UpdateOneAsync` filter).

---

## Future improvements

1. Add a `GET /api/payment/transaction/{id}` lookup in payment-service if an audit trail independent of calendar-service is ever needed.
2. Add idempotency keys to payment-service's `/process` endpoint to guard against double-submits from network retries.
3. A dedicated "outgoing bookings" page so requesters can see IMPLEMENTED bookings awaiting payment without relying solely on the notification bell.
