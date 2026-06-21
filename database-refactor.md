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

## Phase 3 — Add authentication & least-privilege per service — ✅ DONE

Each mongo container now requires authentication, and each service connects as a dedicated user with `readWrite` on **only** its own database.

### What was done
1. **Per-mongo root + service-user bootstrap.** Each mongo container in `docker-compose.yml` now sets `MONGO_INITDB_ROOT_USERNAME`/`MONGO_INITDB_ROOT_PASSWORD`, which:
   - Enables `--auth` automatically (the official image does this when root creds are set).
   - Creates a root user in the `admin` DB used only for the one-time init.
2. **First-start init scripts** in `mongo-init/*.js` create the application user with the minimum role:
   | Script | Created user | Role |
   |---|---|---|
   | `mongo-init/auth-init.js` | `${AUTH_DB_USER}` | `readWrite` on `user` |
   | `mongo-init/products-init.js` | `${PRODUCTS_DB_USER}` | `readWrite` on `products` |
   | `mongo-init/bookings-init.js` | `${BOOKINGS_DB_USER}` | `readWrite` on `BookingsDB` |
   | `mongo-init/notifications-init.js` | `${NOTIFICATIONS_DB_USER}` | `readWrite` on `NotificationDB` |
   These scripts are mounted read-only at `/docker-entrypoint-initdb.d/`, so they execute exactly once on first start of an empty data volume.
3. **Service connection strings updated** to authenticate as the service user (creds loaded from `.env`):
   - **authorization-service** / **product-service** (Spring): added `SPRING_DATA_MONGODB_USERNAME` / `_PASSWORD` / `_AUTHENTICATION_DATABASE` env vars.
   - **calendar-service** (.NET): `ConnectionsString=mongodb://${BOOKINGS_DB_USER}:${BOOKINGS_DB_PASSWORD}@mongo-bookings:27017/?authSource=BookingsDB`.
   - **notification-service** (.NET): `ConnectionStrings__MongoDB=mongodb://${NOTIFICATIONS_DB_USER}:${NOTIFICATIONS_DB_PASSWORD}@mongo-notifications:27017/NotificationDB?authSource=NotificationDB`.
4. **`.env.example`** committed showing the required new variables (real `.env` remains gitignored).

### Verification
- All four init scripts logged `[init] Created user '<u>' on db '<x>'` on first start.
- Each service connected to its mongo with no `AuthenticationFailed` / `MongoSecurityException` errors.
- Least-privilege boundary confirmed live:
  ```
  $ mongosh -u auth_user -p ... --authenticationDatabase user
  db.getSiblingDB('user').runCommand({listCollections:1}).ok   # → 1     (OK)
  db.getSiblingDB('admin').system.users.find().toArray()       # → REJECTED: Unauthorized
  ```

### Gotcha: re-applying on existing volumes
Init scripts only run when `/data/db` is empty. To re-apply auth changes you must drop the mongo volumes:
```powershell
docker compose down
docker volume rm e-commerce-learning_mongo-auth-data `
                 e-commerce-learning_mongo-products-data `
                 e-commerce-learning_mongo-bookings-data `
                 e-commerce-learning_mongo-notifications-data
docker compose up -d
```
This wipes the existing app data — fine for a learning project, but in production you'd `db.createUser` manually instead.

**Acceptance met:** every service authenticates as a scoped user; cross-DB access by a service user is denied.

---

## Phase 4 — Prevent regressions — ✅ DONE

### What was done
1. **README documents the rule and shows the data architecture.** New "Data architecture" section in `README.md` includes a diagram of the per-service mongo topology plus the explicit rule:
   > Each service owns its own MongoDB instance. Cross-service data access goes through HTTP APIs or Kafka events — never by reading another service's database.
2. **CI guardrail** in `scripts/check-db-ownership.sh`:
   - For each service, greps the source tree for references to any *other* service's mongo host (`mongo-auth`, `mongo-products`, `mongo-bookings`, `mongo-notifications`).
   - Also blocks the legacy shared `mongodb` hostname.
   - Exits non-zero with a clear `VIOLATION:` message + file/line on failure.
   - Skips build artifacts (`bin`, `obj`, `target`, `node_modules`, `*.csproj.user`, `*.Backup.tmp`).
3. **Wired into GitHub Actions** as a new `db-ownership-check` job in `.github/workflows/test.yml`, running on every push to `main` and every PR.

### Verification
- Script passes clean on the current tree: `Database ownership check passed: each service references only its own mongo host.`
- Negative test: injecting `# fake-violation: mongo-products` into `authorization-service/.../application.yml` makes the script exit 1 with:
  ```
  VIOLATION: 'authorization-service' references 'mongo-products' (owned by another service):
    authorization-service/src/main/resources/application.yml:42:# fake-violation: mongo-products
  ```

**Acceptance met:** the database-per-service rule is documented and mechanically enforced.

---

## Summary — refactor complete

| Phase | Goal | Outcome |
|---|---|---|
| 1 | Remove the shared `BookingsDB.Booking` collection | worker-service deleted (was stale code) |
| 2 | Physically separate mongo instances | 4 dedicated mongo containers, one per service |
| 3 | Per-service auth & least-privilege | Each service authenticates as a user with `readWrite` on one DB only |
| 4 | Prevent regressions | README rule + diagram + CI guardrail script |

The shared-database anti-pattern is gone and there's a guardrail to keep it gone.

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
