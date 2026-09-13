# MongoDB Multi-Tenancy / Isolation Spec

## 1. Summary

The e-commerce platform does **not** run a separate MongoDB *instance* (container/process) per service. Instead, a **single shared MongoDB container** hosts multiple **logical databases**, one per service, and isolation is enforced at the **application-user / role level** via MongoDB's built-in role-based access control (RBAC) — not via network or process separation.

## 2. Topology

```
docker-compose.yml
└── mongo (single container: mongodb/mongodb-community-server:7.0-ubi8)
    ├── data volume: mongo-data (shared across all logical DBs)
    ├── root user: MONGO_ROOT_USERNAME / MONGO_ROOT_PASSWORD (full admin access)
    └── logical databases (created/seeded via mongo-init/*.js on first boot):
        ├── user           (auth-service)
        ├── products        (product-service)
        ├── BookingsDB      (booking-service)
        └── NotificationDB  (notification-service)
```

Each dependent service connects to the same `mongo` host/port (27017) but targets its own database name and authenticates with its own credentials.

## 3. Per-Service Credentials

| Service | Env vars (docker-compose.yml) | Target DB | authSource |
|---|---|---|---|
| auth-service | `AUTH_DB_USER` / `AUTH_DB_PASSWORD` | `user` | `user` |
| product-service | `PRODUCTS_DB_USER` / `PRODUCTS_DB_PASSWORD` | `products` | `products` |
| booking-service | `BOOKINGS_DB_USER` / `BOOKINGS_DB_PASSWORD` | `BookingsDB` | `BookingsDB` |
| notification-service | `NOTIFICATIONS_DB_USER` / `NOTIFICATIONS_DB_PASSWORD` | `NotificationDB` | `NotificationDB` |

Connection strings/config all point at the same `mongo` host, differing only by credentials, target database, and `authSource`.

## 4. How Isolation Is Enforced

Isolation is implemented by `mongo-init/*.js` scripts, which run **once**, on first container startup (via MongoDB's `docker-entrypoint-initdb.d` convention), after the root user is created:

- `mongo-init/auth-init.js` → creates `AUTH_DB_USER` with role `{ role: "readWrite", db: "user" }`
- `mongo-init/products-init.js` → creates `PRODUCTS_DB_USER` with role `{ role: "readWrite", db: "products" }`
- `mongo-init/bookings-init.js` → creates `BOOKINGS_DB_USER` with role scoped to `BookingsDB`
- `mongo-init/notifications-init.js` → creates `NOTIFICATIONS_DB_USER` with role scoped to `NotificationDB`

Each application user is granted `readWrite` **only** on its own database — no roles are granted on any other service's database. This means:

- **A service cannot read or write another service's data**, even though they share the same MongoDB server/process/volume, because its credentials carry no authorization on other databases.
- Authentication for each user is scoped via `authSource=<own db>` in the connection string, so a user can only authenticate "as itself" against its own database.
- Only the **root user** (`MONGO_ROOT_USERNAME`/`MONGO_ROOT_PASSWORD`) has cross-database access; root credentials are not distributed to application services.

## 5. What This Is / Is Not

| | This design |
|---|---|
| Physical instance isolation (separate containers/processes per service) | ❌ No — one shared `mongo` container |
| Separate data volumes per service | ❌ No — one shared `mongo-data` volume |
| Logical database-per-service | ✅ Yes |
| Per-service credentials | ✅ Yes |
| RBAC preventing cross-service DB access | ✅ Yes (via `readWrite` role scoped to a single DB) |
| Network-level isolation between services' data | ❌ No — enforced purely by Mongo auth/RBAC, not network segmentation |

## 6. Risks / Caveats

- **Blast radius of a Mongo outage/upgrade is shared** — since all services depend on the same container, an outage, resource exhaustion, or version upgrade affects every service simultaneously.
- **No resource isolation** — one service's heavy query load (e.g., a full collection scan) can degrade performance for all other services sharing the same Mongo process.
- **Root credentials are a single point of full compromise** — anyone with `MONGO_ROOT_USERNAME`/`PASSWORD` has access to all services' data.
- **Init scripts only run once** — the `mongo-init/*.js` scripts execute on first container startup with an empty data volume. If credentials or roles need to change later, they require manual intervention (e.g., connecting as root and updating users), not just an env var change.

## 7. References

- `docker-compose.yml` — `mongo` service definition (lines ~98-118) and each dependent service's Mongo env vars.
- `mongo-init/auth-init.js`, `mongo-init/products-init.js`, `mongo-init/bookings-init.js`, `mongo-init/notifications-init.js` — per-database user creation and role scoping.
