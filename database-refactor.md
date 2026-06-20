# Plan: Split the Shared MongoDB into Database-per-Service

## Goal
Move from a single shared MongoDB instance (no auth, all services sharing DBs) to a clean **database-per-service** architecture where each service owns its data and exposes it only via APIs or events.

---

## Current State (audit)

| Service                | Mongo DB         | Collections                                      | Issue |
|------------------------|------------------|--------------------------------------------------|-------|
| authorization-service  | `user`           | users                                            | OK – sole owner |
| product-service        | `products`       | products, `taskmaster`, `taskmaster_applications`| OK – sole owner |
| notification-service   | `NotificationDB` | `Notifications`                                  | OK – sole owner |
| calendar-service       | `BookingsDB`     | `Booking`                                        | OK – sole owner (after Phase 1) |
| ai-assistant-service   | —                | —                                                | OK – uses HTTP APIs |

> Phase 1 update: `worker-service` was removed entirely (it was stale: nothing produced to its `bookings` Kafka topic and its `Booking` schema didn't match calendar-service's). `BookingsDB` is now owned solely by `calendar-service`.

All services point at the same `mongodb://mongodb:27017` with no credentials.

---

## Target State

- One MongoDB **instance per service** (or at minimum one DB + dedicated user with permissions limited to that DB).
- Each service has its own credentials.
- No service reads another service's collections — communication is via REST or Kafka events.
- `worker-service` no longer touches the `Booking` collection.

---

## Phase 1 — Fix the worst violation: worker-service ↔ calendar-service shared collection — ✅ DONE

The worker-service was reading/writing `BookingsDB.Booking` directly. Investigation revealed it was **stale code**:
- Nothing in the repo produced to the `bookings` Kafka topic it consumed.
- Its `Booking` model (`Description`/`StartTime`/`EndTime`/`Status="Pending|Completed"`) was incompatible with calendar-service's actual schema (`TaskMasterId`/`SlotStart`/`DurationHours`/`Status=PENDING|ACCEPTED|DECLINED`).

### Resolution
Per user decision, worker-service was **removed entirely**:
- Deleted `worker-service/` directory.
- Removed the `worker-service` block from `docker-compose.yml`.
- Removed the now-unused `bookings` topic creation from `kafka-init`.
- Dropped the unused `Topic = "bookings"` field from `calendar-service/MessageQueue/KafkaProducerConfig.cs`, `calendar-service/appsettings.json`, and the `KafkaProducerConfig__Topic` env in `docker-compose.yml`.
- Removed worker-service references from `README.md`, `NOTIFICATION_TESTING_GUIDE.md`, and `OPENTELEMETRY_SPEC.md`.

**Acceptance met:** No service other than `calendar-service` accesses the `Booking` collection.

---

## Phase 2 — Physically separate the MongoDB instances

Even with logical DB separation, sharing a single mongod means: shared failure domain, shared credentials, no permission boundary.

### Tasks
1. **Update `docker-compose.yml`**: replace the single `mongodb` service with one per data-owning service:
   ```yaml
   mongo-auth:          # for authorization-service
   mongo-products:      # for product-service
   mongo-bookings:      # for calendar-service
   mongo-notifications: # for notification-service
   ```
   Each with its own named volume (`mongo-auth-data`, etc.) and **not** exposing a host port in prod.
2. **Update each service's connection string** to point at its dedicated host:
   - `authorization-service/src/main/resources/application.yml` → `mongo-auth:27017`
   - `product-service/src/main/resources/application.yml` → `mongo-products:27017`
   - `calendar-service` env `ConnectionsString` → `mongodb://mongo-bookings:27017`
   - `notification-service/appsettings.json` → `mongodb://mongo-notifications:27017/NotificationDB`
3. **Update `depends_on`** in each service to reference its own mongo container only.
4. **Migrate existing data** (dev/test only — for learning a fresh start is fine):
   - Optional: `mongodump` from old `mongodb` per DB, then `mongorestore` into the new instance.
5. **Bring the stack up**, smoke-test each service end-to-end (login, create product, create booking, receive notification).

**Acceptance:** `docker-compose ps` shows N mongo containers; `docker exec mongo-auth mongosh --eval "show dbs"` shows only `user`.

---

## Phase 3 — Add authentication & least-privilege per service

### Tasks
1. For each mongo container, set `MONGO_INITDB_ROOT_USERNAME` / `MONGO_INITDB_ROOT_PASSWORD` from `.env`.
2. Add an init script (`docker-entrypoint-initdb.d/init.js`) that creates a service user with `readWrite` only on its own DB.
3. Update each service's connection string to use the service user's credentials (loaded from env, not committed).
4. Confirm `.env` is in `.gitignore` (it should already be).

**Acceptance:** Connecting as `auth_user` and trying `db.getSiblingDB('products').products.find()` is rejected.

---

## Phase 4 — Prevent regressions

1. **Document** the rule in `README.md`: "Each service owns its database; cross-service data access goes through HTTP APIs or Kafka events."
2. **Add a CI check** (simple grep) that fails if any service references another service's DB name or a Mongo collection it doesn't own.
3. **Architecture diagram** in `README.md` showing one DB per service + the event/REST flows.

---

## Out of scope (for now)
- Splitting `product-service`'s `taskmaster*` collections into a separate TaskMaster service — only worth doing if/when TaskMaster becomes its own bounded context.
- Replica sets / sharding per DB.
- Moving Redis, Kafka, Consul (those are infra and legitimately shared).

---

## Suggested order of execution
1. Phase 1 first (highest value, smallest blast radius).
2. Phase 2 once Phase 1 passes tests.
3. Phase 3 after Phase 2 is stable.
4. Phase 4 last, as documentation/guardrail.
