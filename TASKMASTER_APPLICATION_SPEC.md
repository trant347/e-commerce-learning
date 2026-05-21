# TaskMaster Application Workflow — Specification

## Overview

Any authenticated user can apply to become a TaskMaster. Admins review pending applications and either accept or decline them. On acceptance, a TaskMaster profile is automatically created and the applicant is notified. The entire flow uses event-driven notifications via Kafka.

---

## User Stories

| # | Actor | Action | Outcome |
|---|-------|--------|---------|
| 1 | User  | Clicks "New TaskMasters" in the nav bar | Navigated to the application form at `/apply` |
| 2 | User  | Submits the application form | Application saved as PENDING; admin notified via bell |
| 3 | User  | Tries to submit a second application while one is PENDING | Receives a 409 Conflict error |
| 4 | Admin | Receives notification bell with "X has applied to become a TaskMaster" | Clicks it → lands on `/admin/applications/{id}` review page |
| 5 | Admin | Browses `/admin/applications` | Sees a filterable list of applications (default: PENDING) |
| 6 | Admin | Clicks **Accept** on an application | TaskMaster profile created; applicant notified; admin redirected to new profile |
| 7 | Admin | Clicks **Decline** on an application (optional reason) | Application marked DECLINED; applicant notified with reason |
| 8 | User  | Receives "Application accepted" notification | Clicks it → navigated to their new TaskMaster profile at `/product/{id}` |
| 9 | User  | Receives "Application declined" notification | Clicks it → navigated to home `/` |

---

## State Machine

```
           ┌─────────────────────┐
           │   User submits form │
           └──────────┬──────────┘
                      │ POST /products/applications
                      ▼
               ┌────────────┐
               │  PENDING   │◄─── only one PENDING allowed per user
               └─────┬──────┘
          ┌──────────┴──────────┐
          │                     │
  PUT …/accept             PUT …/decline
          │                     │
          ▼                     ▼
   ┌────────────┐        ┌────────────┐
   │  ACCEPTED  │        │  DECLINED  │
   │            │        └────────────┘
   │ TaskMaster │
   │  created   │
   └────────────┘
```

---

## Architecture

```
┌──────────────┐  POST /products/applications   ┌──────────────────┐
│   Frontend   │ ─────────────────────────────► │  product-service │
│  (React SPA) │                                │  (Java 11/       │
│              │ ◄─────────────────────────────  │   Spring Boot)   │
│  /apply      │  201 Created (application doc) │                  │
│  /admin/     │                                │  ApplicationCtrl │
│  applications│                                │  ApplicationRepo │
│  /admin/     │                                │  TaskMasterRepo  │
│  applications│                                └────────┬─────────┘
│  /:id        │                                         │
└──────┬───────┘                                         │ Kafka publish
       │                                                 │ topic: notification-events
       │ BFF Express proxy                               ▼
       │ /products/* → product-service          ┌────────────────────┐
       │                                        │ notification-service│
       │ GET /api/notification/{username}        │  (.NET 8)          │
       │ ◄─────────────────────────────────────  │                    │
       │                                        │  ConsumerWorker    │
       │                                        │  MongoDbService    │
       │                                        │  NotificationCtrl  │
       │                                        └────────────────────┘
       │
       │ JWT (Authorization: Bearer …)
       ▼
┌──────────────────┐
│ auth-service     │
│ (Java 8/Spring)  │
│ issues JWT with  │
│ authorities claim│
└──────────────────┘
```

---

## API Reference

### product-service  (`http://product-service:8080`)

All endpoints require `Authorization: Bearer <jwt>`. The `JwtTokenFilter` on `/products/*` parses the token and sets `authenticatedUsername` and `authenticatedAuthorities` on the request.

#### Submit Application
```
POST /products/applications
Authorization: Bearer <token>
Content-Type: application/json

{
  "name": "John Smith",
  "age": 30,
  "location": "New York, NY",
  "description": "Experienced plumber with 10 years...",
  "hourlyRateUsd": 55.00,
  "photo": "https://example.com/photo.jpg",   // optional
  "jobCategories": ["plumbing", "repairs"]
}

201 Created  → TaskMasterApplication document
409 Conflict → { "error": "You already have a pending application." }
```

#### List Applications _(admin only)_
```
GET /products/applications?status=PENDING
Authorization: Bearer <admin-token>

200 OK → TaskMasterApplication[]
403 Forbidden  (non-admin)
```

#### Get Application _(admin only)_
```
GET /products/applications/{id}
Authorization: Bearer <admin-token>

200 OK → TaskMasterApplication
404 Not Found
403 Forbidden
```

#### Accept Application _(admin only)_
```
PUT /products/applications/{id}/accept
Authorization: Bearer <admin-token>

200 OK → TaskMasterApplication (status: ACCEPTED, createdTaskMasterId: "...")
409 Conflict → applicant already has a TaskMaster profile
404 Not Found
403 Forbidden
```

#### Decline Application _(admin only)_
```
PUT /products/applications/{id}/decline
Authorization: Bearer <admin-token>
Content-Type: application/json

{ "reason": "Profile incomplete" }   // optional body

200 OK → TaskMasterApplication (status: DECLINED, declineReason: "...")
404 Not Found
403 Forbidden
```

---

## Data Models

### `taskmaster_applications` (MongoDB)

| Field | Type | Notes |
|-------|------|-------|
| `_id` | ObjectId | Auto-generated |
| `applicantUsername` | String | Indexed; set from JWT on submit |
| `name` | String | Full name of applicant |
| `age` | int | |
| `location` | String | |
| `description` | String | |
| `hourlyRateUsd` | double | |
| `photo` | String | Optional URL |
| `jobCategories` | String[] | |
| `status` | Enum | `PENDING` / `ACCEPTED` / `DECLINED` |
| `submittedAt` | Instant | Set on creation |
| `declineReason` | String | Optional; set by admin |
| `createdTaskMasterId` | String | Set on ACCEPTED; links to `taskmaster` collection |

### `taskmaster` (MongoDB) — extended field

| Field | Type | Notes |
|-------|------|-------|
| `ownerUsername` | String | Added on accept; sparse unique index; enables "is this my profile?" lookup |

### Kafka Event Schema (`notification-events` topic)

```json
{
  "type": "TASKMASTER_APPLICATION_SUBMITTED | TASKMASTER_APPLICATION_ACCEPTED | TASKMASTER_APPLICATION_DECLINED",
  "recipientUsername": "admin | <applicant-username>",
  "message": "Human-readable notification text",
  "actionUrl": "/admin/applications/{id} | /product/{id} | /"
}
```

### `Notifications` (MongoDB, notification-service)

| Field | Type | Notes |
|-------|------|-------|
| `_id` | ObjectId | |
| `recipientEmail` | String | Stores username (legacy field name; used as lookup key) |
| `type` | String | Event type string |
| `message` | String | |
| `actionUrl` | String | Frontend route; navigated to on bell click |
| `status` | String | `Pending` / `Sent` / `Failed` |
| `timestamp` | DateTime | |

---

## Authorization

| Role | Determined by | Stored as |
|------|--------------|-----------|
| Regular user | Default on register | `role = "user"` → JWT authority `ROLE_user` |
| Admin | Seeded in `TaskMasterAuthenticationApplication` | `role = "ADMIN"` → JWT authority `ROLE_ADMIN` |

`ApplicationController.isAdmin()` checks the `authenticatedAuthorities` request attribute (set by `JwtTokenFilter`) for the string `"ROLE_ADMIN"`.

---

## Frontend Components

| Component | Route | Access |
|-----------|-------|--------|
| `ApplyForTaskMaster` | `/apply` | Any logged-in user |
| `AdminApplicationsList` | `/admin/applications` | Admin only |
| `ApplicationReview` | `/admin/applications/:id` | Admin only |
| `NotificationBell` | (page header) | All logged-in users |

### BFF Proxy (Express — `routes/product.js`)

The Node.js BFF proxies all `/products/applications/*` calls to `product-service`, forwarding the `Authorization` header unchanged.

```
Frontend                  BFF (Express)                 product-service
   │  POST /products/applications  │                           │
   │ ─────────────────────────────►│  POST /products/applications │
   │                               │ ─────────────────────────►│
   │ ◄─────────────────────────────│ ◄─────────────────────────│
```

---

## Notification Flow (detailed)

```
product-service                Kafka                 notification-service          Frontend
      │                          │                           │                        │
      │  publish(notification-events)                        │                        │
      │ ─────────────────────────►│                          │                        │
      │                          │  consume                  │                        │
      │                          │ ─────────────────────────►│                        │
      │                          │                           │ save to MongoDB        │
      │                          │                           │ (recipientEmail=username)│
      │                          │                           │                        │
      │                          │                           │  GET /api/notification/{username}
      │                          │                           │ ◄──────────────────────│
      │                          │                           │ ─────────────────────► │
      │                          │                           │  [ { message, actionUrl, … } ]
      │                          │                           │                        │
      │                          │       user clicks bell notification                │
      │                          │                           │  history.push(actionUrl)│
```

---

## Services Involved

| Service | Language/Runtime | Role |
|---------|-----------------|------|
| `product-service` | Java 11, Spring Boot 2.1 | Application CRUD, TaskMaster creation, Kafka publishing |
| `notification-service` | .NET 8 | Kafka consumer, notification persistence, SSE/HTTP delivery |
| `authorization-service` (auth-service) | Java 8, Spring Boot | JWT issuance with `authorities` claim |
| `frontend` | React 16 + TypeScript, Express BFF | UI forms, admin review, notification bell |
| Kafka | Confluent 7.4 | Async event bus between product-service and notification-service |
| MongoDB | 5.0 | Persistent store for applications, task masters, notifications |
