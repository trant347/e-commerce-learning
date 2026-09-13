# User Deletion Workflow — Specification

> Status: **Draft — for review.** Implementation lives in `authorization-service` (publisher) and `product-service` (consumer).

## Overview

An admin can delete a user account. Deleting a user must also cascade to any TaskMaster profile they own and any TaskMaster applications they submitted. The cascade is event-driven via Kafka so the deleting service is not tightly coupled to the cascading services.

---

## User Stories

| # | Actor | Action | Outcome |
|---|-------|--------|---------|
| 1 | Admin | `DELETE /user/{username}` (via BFF) | User removed; `USER_DELETED` event published |
| 2 | Admin | Tries to delete themselves | `403 Forbidden` — self-delete not allowed |
| 3 | Non-admin | Tries to delete any user | `403 Forbidden` |
| 4 | System | Consumes `USER_DELETED` in `product-service` | TaskMaster (by `ownerUsername`) + all applications (by `applicantUsername`) for that user are removed |

---

## API

### Delete user *(admin only)*
```
DELETE /user/{username}                        # via frontend BFF
DELETE /{username}                             # authorization-service
Authorization: Bearer <admin-jwt>

204 No Content      → user deleted, event published
403 Forbidden       → caller is not admin, or admin is deleting themselves
404 Not Found       → username does not exist
503 Service Unavailable → Kafka publish failed; the user record was NOT deleted
```

The endpoint publishes the event **before** committing the delete. If Kafka publish throws, the user is left intact and `503` is returned. This avoids the "user gone, cascade lost" failure mode without introducing a full transactional-outbox table.

---

## Event Schema

Topic: `user-events`

```json
{
  "type": "USER_DELETED",
  "username": "alice",
  "deletedAt": "2026-05-24T16:00:00Z",
  "deletedByUsername": "admin"
}
```

Future event types on the same topic can include `USER_CREATED`, `USER_UPDATED`, etc.

---

## Architecture

```
┌────────────┐ DELETE /user/{username} ┌──────────────────────┐
│  Frontend  │ ──────────────────────► │ authorization-service│
│  (admin)   │                         │                      │
└────────────┘                         │  1. JWT → isAdmin?   │
                                       │  2. forbid self      │
                                       │  3. publish event ───┼─► Kafka topic: user-events
                                       │  4. delete user      │              │
                                       │  5. 204              │              │
                                       └──────────────────────┘              │
                                                                             ▼
                                                     ┌──────────────────────────────┐
                                                     │      product-service         │
                                                     │  @KafkaListener("user-events")│
                                                     │   on USER_DELETED:           │
                                                     │     deleteByOwnerUsername    │
                                                     │     deleteAllByApplicant     │
                                                     └──────────────────────────────┘
```

---

## Resilience design

The full analysis is captured here so the choices can be reviewed and tightened later.

### Why no compensating "revert" (saga)
A saga-style compensation ("if cascade fails, recreate the user") is the right tool when both sides must succeed-or-fail as a business invariant — typically money/inventory. For user deletion:

- The user record is the source of truth. Recreating a deleted user is awkward: the plaintext password is unrecoverable and the user may already have been notified.
- A TaskMaster without an owning user is meaningless and harmless — eventual consistency is acceptable.
- The failure mode is almost always transient (network, restart), not a business rejection.

So we don't compensate; we make the path durable and idempotent.

### Failure modes and how we cover them

| # | Failure | Mitigation |
|---|---------|------------|
| 1 | `authorization-service` deletes the user, then crashes before publishing the event. | **Publish-before-delete.** If publish throws, we return `503` and the user is intact. (Stronger alternative for the future: transactional outbox table — see *Future improvements*.) |
| 2 | `product-service` is down when the event is published. | Kafka retains the message. The consumer picks it up on restart. No action needed. |
| 3 | `product-service` consumes the event but deletion fails (DB error). | Don't commit the offset → Kafka redelivers with backoff. After N retries, route to a dead-letter topic. |
| 4 | Same event delivered twice. | **Idempotent consumer.** `deleteByOwnerUsername` is a no-op if the document is missing; `deleteAllByApplicantUsername` returns 0 cleanly. |
| 5 | Historical orphan TaskMasters from past bugs. | *Optional* nightly reconciliation job that compares TaskMaster `ownerUsername` against the live user list. Not implemented in v1. |

### Trade-offs vs. a full outbox

| Aspect | Publish-before-delete (v1) | Transactional outbox (future) |
|---|---|---|
| Implementation cost | Low — one Kafka send + try/catch | Medium — extra collection + relay loop |
| Loses event on crash between `send()` returning and `delete()` committing? | No — if send threw we abort; if send succeeded, the event is on the broker |
| Loses event if process dies mid-`send()`? | Possible (returns `503`, user safe; admin may retry) | Not possible (event already persisted locally) |
| Operational complexity | None | Outbox relay must be monitored |

For a learning project, v1 is the right level. Upgrade to outbox if cross-service deletes become common or if multiple events need to be published atomically with one DB write.

---

## Authorisation

- The JWT must carry the authority `ROLE_ADMIN` (as set by `authorization-service` for the seeded `admin` user — see `TaskMasterAuthenticationApplication`).
- Self-delete is forbidden by comparing the JWT `sub` claim to the target username; this avoids an admin locking the system out.

---

## Future improvements

1. **Transactional outbox** in `authorization-service` to eliminate the small "publish succeeded, process died, delete didn't run" window.
2. **Dead-letter topic** (`user-events-dlq`) plus an alert when messages land there.
3. **Reconciliation job** in `product-service` that periodically deletes orphan TaskMasters/applications whose users no longer exist.
4. **Notification cascade**: `notification-service` could also consume `user-events` to purge stored notifications. Skipped in v1 because notifications are small, low-PII, and decay naturally.
5. **Soft delete + retention window** instead of hard delete, if a "restore user" feature is ever needed.
