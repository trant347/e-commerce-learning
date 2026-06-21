# TaskMaster Hub
A TaskMaster marketplace that uses micro-services architecture. Users can browse task masters (service providers) for various jobs like plumbing, cleaning, tutoring, and more.

## Prerequisites
- Java, Maven, Consul and Docker

## Getting Started
1. Build docker image for each project by running `build.bat` or `build.cmd`
2. To start everything up, in the root folder, run `docker-compose up`
3. Go to `localhost:3000` to view the website

## Services
- **product-service**: TaskMaster profiles and search
- **calendar-service**: Booking management
- **authentication-service**: User authentication
- **notification-service**: Real-time notifications
- **ai-assistant-service**: AI-powered chat assistant (Ollama)

---

## Data architecture

Each service owns its own MongoDB instance. **Cross-service data access goes through HTTP APIs or Kafka events — never by reading another service's database.** This rule is enforced in CI by `scripts/check-db-ownership.sh`.

```
            ┌──────────────────┐         ┌──────────────────┐
            │  authentication  │ ──────▶ │   mongo-auth     │  db: user
            └──────────────────┘         └──────────────────┘

            ┌──────────────────┐         ┌──────────────────┐
            │     product      │ ──────▶ │  mongo-products  │  db: products
            └─────────┬────────┘         └──────────────────┘
                     ▲│ REST
                     │▼
            ┌──────────────────┐         ┌──────────────────┐
            │     calendar     │ ──────▶ │  mongo-bookings  │  db: BookingsDB
            └─────────┬────────┘         └──────────────────┘
                      │ Kafka: notification-events
                      ▼
            ┌──────────────────┐         ┌──────────────────────┐
            │   notification   │ ──────▶ │ mongo-notifications  │  db: NotificationDB
            └──────────────────┘         └──────────────────────┘

            ┌──────────────────┐  HTTP   ┌──────────────────┐
            │   ai-assistant   │ ──────▶ │ product, calendar│   (no DB of its own)
            └──────────────────┘         └──────────────────┘

           Cross-service events on Kafka:  notification-events, user-events
```

Each mongo instance runs with `--auth` and the service connects as a dedicated user with `readWrite` on **only** its own database. See `database-refactor.md` for the full migration history (Phases 1–4) and `mongo-init/` for the per-DB bootstrap scripts.

---

## How it works

### product-service (Java 11 / Spring Boot)
The core catalog service. Stores and serves TaskMaster profiles and manages the application-to-TaskMaster lifecycle.

**TaskMaster profiles**
- Profiles are stored in MongoDB (`task_masters` collection).
- On first startup, the collection is seeded from `src/main/resources/seed/taskMasters.json` — only when empty, so manually created profiles are never overwritten.
- `GET /products` — public paginated listing (no auth required).
- `GET /products?name=` / `?location=` / `?category=` / `?minRate=&maxRate=` / `?minRating=` — filtered queries backed by Spring Data MongoDB derived methods.
- Faceted search (MongoDB aggregation pipeline) groups results by job category and location.
- `POST /products` — create a new profile directly (admin).
- `POST /products/upload` — stores a profile image on disk under `static/images/`; the filename is returned and embedded in the profile.

**TaskMaster application lifecycle**

```
User submits form → PENDING → Admin accepts → TaskMaster profile created
                             → Admin declines → DECLINED (with optional reason)
```

- `POST /products/applications` — any authenticated user submits an application. A second submission while one is `PENDING` returns HTTP 409.
- `GET /products/applications` — admin lists all applications, filterable by status (`PENDING`, `ACCEPTED`, `DECLINED`).
- `GET /products/applications/unviewed-count` — returns `{ count: N }` of PENDING applications the admin hasn't opened yet. Powers the badge in the navigation header.
- `PUT /products/applications/{id}/accept` — creates a TaskMaster profile from the application data, links the application to the new profile, and publishes a `TASKMASTER_APPLICATION_ACCEPTED` event to Kafka.
- `PUT /products/applications/{id}/decline` — marks the application DECLINED with an optional reason and publishes a `TASKMASTER_APPLICATION_DECLINED` event.
- `PUT /products/applications/{id}/view` — marks an application as viewed by the admin (clears it from the unviewed count).

**Kafka events published** (topic: `notification-events`)
| Event type | Trigger | Recipient |
|---|---|---|
| `TASKMASTER_APPLICATION_SUBMITTED` | User submits application | `admin` |
| `TASKMASTER_APPLICATION_ACCEPTED` | Admin accepts | applicant |
| `TASKMASTER_APPLICATION_DECLINED` | Admin declines | applicant |

---

### notification-service (.NET 8)
Bridges Kafka events to browser clients using Server-Sent Events (SSE).

**Flow**
1. `NotificationConsumerWorker` — a background `IHostedService` that subscribes to the `notification-events` Kafka topic.
2. Each consumed message is deserialised into a `NotificationMessage`, persisted to MongoDB (`notifications` collection), and then pushed to any live browser connection for the target user.
3. Delivery uses `NotificationStreamer` — an in-memory `ConcurrentDictionary<userId, Channel<T>>`. Each connected browser holds one SSE channel. When a message arrives for a user who isn't connected, it is silently dropped (MongoDB still has it for next load).

**REST endpoints**
- `GET /api/notification/{userId}` — returns the last 50 notifications for a user (initial page load).
- `GET /api/notification/{userId}/stream` — opens an SSE stream (`Content-Type: text/event-stream`). The browser holds this connection open and receives new notifications in real-time without polling.

**Frontend integration**
The `useNotifications` React hook connects to the SSE stream on login and exposes `lastNotification`. Components can watch `lastNotification.type` to react to specific events — for example, the navigation badge increments instantly when a `TASKMASTER_APPLICATION_SUBMITTED` event arrives while the admin is using the app.

---

## Authentication

This project uses **shared-secret JWT (HS256)** for authentication.

### How it works
1. The client logs in via `POST /user/login` → the **authentication-service** validates credentials and issues a signed JWT using a symmetric secret key stored in Consul config.
2. The client includes the token in subsequent requests: `Authorization: Bearer <token>`.
3. Protected services (e.g. **product-service**) validate the token **locally** using the same shared secret — no network call to the authentication-service is made per request.

### Implementation detail (`JwtTokenFilter.java`)
- Applied via `@WebFilter(urlPatterns = "/products/*")`
- Only routes matching `/products/{id}` or deeper require a valid token. The list endpoint `GET /products` (no trailing segment) is **public**.
- Image files (`*.png`, `*.jpg`, `*.svg`) are always allowed through.
- The `test` Spring profile bypasses auth entirely (used in unit tests).
- Token is verified with `Jwts.parser().setSigningKey(secret.getKey())` from the `jjwt` library.

### Trade-offs
| Pro | Con |
|-----|-----|
| Fast — pure in-process validation, no extra network hop | Shared secret must be distributed to every service |
| Simple to implement | Token cannot be revoked before expiry |

### Production alternatives
- **RS256 (asymmetric JWT)** — auth-service signs with a private key; other services verify with the public key fetched from a JWKS endpoint. Used by Auth0, Keycloak, AWS Cognito.
- **API Gateway auth** — JWT validated once at the edge (Kong, AWS API Gateway); downstream services trust forwarded identity headers.
- **Token introspection** — resource service calls auth-service's `/introspect` on every request; allows real-time revocation at the cost of added latency.
