# E2E Tests

End-to-end tests for the TaskMaster application flow, driven by [Playwright](https://playwright.dev/) against the **real** Docker Compose stack (no service mocks).

---

## What this suite covers

Current scenario: [tests/taskmaster-application.spec.ts](tests/taskmaster-application.spec.ts)

1. A regular user logs in (via pre-seeded JWT) and submits a TaskMaster application at `/apply`.
2. An admin logs in (via pre-seeded JWT) and opens `/admin/applications`.
3. The admin finds the new application, opens the review page, and clicks **Accept**.
4. The user receives an `ACCEPTED` notification (verified via the notification API the bell consumes).

The flow exercises: frontend BFF, `authorization-service`, `product-service`, Kafka, and `notification-service` end-to-end.

---

## Design

### Real environment, no service mocks
We run against the actual `docker-compose.yml` stack. Mocks would defeat the purpose of an integration scenario that depends on JWT auth, Kafka eventing, and the notification consumer. The only service we'd consider mocking is `ai-assistant-service` (the LLM is non-deterministic) — but the current scenario does not call it, so nothing is stubbed today. When AI-dependent flows are added, prefer `page.route('**/ai-assistant/**', ...)` to return fixed responses rather than hitting Ollama.

### Wait for services before testing
[global-setup.ts](global-setup.ts) polls the frontend, auth, product, and notification services until each responds (`< 500`). Tests do not start until the stack is healthy.

### Seeded admin, fresh user per run
- **Admin** (`admin` / `admin`) is seeded by `authorization-service` itself on boot (see `TaskMasterAuthenticationApplication.createDummyDatabase`).
- **Regular user** is registered fresh on every run with a timestamp suffix (`e2euser_<base36>`). This avoids the "one PENDING application per user" rule and keeps reruns idempotent without touching the database.

### Programmatic login, no UI login per test
Logging in through the UI on every spec is slow and adds a failure surface unrelated to the feature under test. Instead, global setup:

1. Calls `POST /authenticate` on the auth service to obtain a JWT for each actor.
2. Writes Playwright `storageState` JSON files (`.auth/admin.json`, `.auth/user.json`) containing the JWT and username in `localStorage` under the keys the frontend expects (`token`, `USER_NAME_KEY_BOOKSTORE` — see `frontend/ui/api/authenticationStorage.tsx`).

Tests then open a browser context with `storageState: '...'` and are immediately "logged in".

### Sequential workers
`workers: 1` in [playwright.config.ts](playwright.config.ts). The application flow has cross-actor state (admin sees what user just created); parallel runs against a single shared database would cause flakiness. Parallelism can be revisited if tests are partitioned per-user.

### Async assertions, no fixed sleeps
The "user receives notification" step uses `expect.poll` against the notification API rather than a `waitForTimeout`. This keeps the test fast on a healthy system and stable on a slow one.

---

## Layout

```
e2e/
├── package.json
├── playwright.config.ts      # baseURL, globalSetup, sequential workers
├── tsconfig.json
├── config.ts                 # URLs, admin creds, storageState paths
├── helpers.ts                # readiness polling, register/login, storageState writer
├── global-setup.ts           # wait → seed user → login admin+user → write storageState
├── tests/
│   └── taskmaster-application.spec.ts
└── .auth/                    # generated each run (gitignored)
    ├── admin.json
    ├── user.json
    └── test-user.json        # username/email/password of the per-run user
```

---

## Prerequisites

- Node.js 18+
- Docker Desktop running, with the project stack started:
  ```powershell
  cd ..
  docker compose up -d
  ```
  Verify the frontend at http://localhost:3000 before running tests.

---

## Install

```powershell
cd e2e
npm install
npm run install:browsers
```

---

## Run

```powershell
# headless
npm test

# headed (watch the browser)
npm run test:headed

# Playwright UI mode (interactive)
npm run test:ui

# open the last HTML report
npm run report
```

### Environment overrides

| Variable | Default | Purpose |
|---|---|---|
| `FRONTEND_URL` | `http://localhost:3000` | Playwright `baseURL` and frontend health check |
| `AUTH_SERVICE_URL` | `http://localhost:8081` | Used by setup to register + authenticate |
| `PRODUCT_SERVICE_URL` | `http://localhost:8080` | Health check |
| `NOTIFICATION_SERVICE_URL` | `http://localhost:8084` | Health check |

Example:
```powershell
$env:FRONTEND_URL = "http://localhost:4000"; npm test
```

---

## Adding more scenarios

1. Reuse the existing storageState files — don't re-implement login.
   ```ts
   import { USER_STATE_FILE, ADMIN_STATE_FILE } from '../config';
   test.use({ storageState: USER_STATE_FILE });
   ```
2. If your scenario needs a brand-new user (e.g. to avoid the PENDING conflict), register one inline via `helpers.registerUser` + `loginAndGetToken` and write a fresh storageState file.
3. For flows that hit `ai-assistant-service`, stub it at the network layer to keep the test deterministic:
   ```ts
   await page.route('**/ai-assistant/**', route =>
     route.fulfill({ status: 200, body: JSON.stringify({ reply: 'fixed answer' }) }),
   );
   ```

---

## Troubleshooting

- **Setup times out waiting for a service** — confirm `docker compose ps` shows everything healthy and the port mappings match the defaults above.
- **`409 Conflict` on application submit** — happens if the test user already has a PENDING application. Global setup avoids this by generating a unique username; if you hardcode one, clear MongoDB or change the name.
- **Admin row not found on `/admin/applications`** — selector is best-effort (`tr | .application-row | li` containing the username). Add `data-testid="application-row"` to the list component for a stable selector and update the spec.
- **Notification never arrives** — check `notification-service` logs and that the `notification-events` Kafka topic has been created by `kafka-init`.
