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

## Phase 2 — Physically separate the MongoDB instances — ✅ DONE

Each service now points at its own mongod instance on a private Docker network. No shared failure domain at the data layer.

### What was done
1. **`docker-compose.yml`**: single `mongodb` service replaced with four dedicated containers and volumes:
   | Container | Volume | Host port (dev) | Used by |
   |---|---|---|---|
   | `mongo-auth` | `mongo-auth-data` | 27017 | authorization-service |
   | `mongo-products` | `mongo-products-data` | 27018 | product-service |
   | `mongo-bookings` | `mongo-bookings-data` | 27019 | calendar-service |
   | `mongo-notifications` | `mongo-notifications-data` | 27020 | notification-service |

   Different host ports were chosen so developers can connect each instance from their host without conflicts. **For production these `ports:` mappings should be removed** so the mongods are reachable only on the internal Docker network.

2. **Service connection targets updated** (both in `docker-compose.yml` env and as committed defaults so local dev works too):
   - `authorization-service`: `SPRING_DATA_MONGODB_HOST=mongo-auth` (default in `application.yml` is now `${MONGO_HOST:mongo-auth}`).
   - `product-service`: `SPRING_DATA_MONGODB_HOST=mongo-products` (default in `application.yml` is now `${MONGO_HOST:mongo-products}`).
   - `calendar-service`: `ConnectionsString=mongodb://mongo-bookings:27017` (env literal — no longer reads `${ConnectionsString}` from `.env`).
   - `notification-service`: `ConnectionStrings__MongoDB=mongodb://mongo-notifications:27017/NotificationDB` (default in `appsettings.json` updated to match).

3. **`depends_on`** updated for each service to reference only its own mongo container.

4. **Cleanup**: dead `ConnectionsString=${ConnectionsString}` and `MongoBookingDatabaseName=${MongoBookingDatabaseName}` env vars (leftover from worker-service) removed from the notification-service block.

### Migration note
The previous single `mongodb-data` volume is **orphaned but not deleted**. If you need to migrate existing data:
```powershell
# from a one-off mongo container mounting the old volume
docker run --rm -v mongodb-data:/data/db -v ${PWD}/dump:/dump mongo:7 mongodump --out /dump --db user
docker run --rm -v mongo-auth-data:/data/db -v ${PWD}/dump:/dump mongo:7 mongorestore /dump
```
Repeat per DB (`products`, `BookingsDB`, `NotificationDB`). For a learning project, a fresh start is usually fine — just `docker volume rm e-commerce-learning_mongodb-data` once you're sure.

### Local `.env` cleanup (manual)
`.env` is gitignored. The following entries are no longer used and can be deleted from your local `.env`:
- `ConnectionsString=mongodb://mongodb:27017`
- `MongoBookingDatabaseName=BookingsDB`

### Verification
- `docker compose config` validates clean.
- calendar-service: 24/24 tests pass.
- authorization-service: 3/3 tests pass (BUILD SUCCESS).
- `grep` for `mongodb://mongodb` or `host: mongodb` returns nothing in service code.
- **End-to-end runtime verified:** `docker exec mongo-auth mongosh --eval "show dbs"` shows only the `user` DB; `mongo-products` shows only `products`; service logs confirm each app connects to its dedicated mongo.

### Gotcha: orphan container on first upgrade
Because the old `mongodb` service was removed from compose, `docker compose up` will leave the old `e-commerce-learning-mongodb-1` container running and holding port 27017, which prevents `mongo-auth` from starting. The fix is a one-time:
```
docker compose down --remove-orphans
docker compose up -d
```
The orphan `mongodb-data` volume can also be removed once you're sure you don't need to migrate old data: `docker volume rm e-commerce-learning_mongodb-data`.

**Acceptance met:** four mongo containers, each service depends on only its own.

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
