# AI Assistant Deployment

How to run `ai-assistant-service` with local Ollama, OpenRouter, or both.
Background and requirements: [OpenRouter Integration Specification](OPENROUTER_INTEGRATION_SPEC.md)
(Sections 5, 6 and 8).

## How model selection works

Each backend task ID (currently only `chat`) maps to a complete profile:

| Setting | Environment variable | Meaning |
|---|---|---|
| Provider | `AiModels__Tasks__<id>__Provider` | `Ollama` or `OpenRouter` |
| Model | `AiModels__Tasks__<id>__Model` | Ollama tag or OpenRouter model slug |
| Round budget | `AiModels__Tasks__<id>__TimeoutSeconds` | One model round, including retries and retry delays |
| Request budget | `AiModels__Tasks__<id>__RequestTimeoutSeconds` | End-to-end: all rounds and tool calls; must be at least the round budget |
| Output cap | `AiModels__Tasks__<id>__MaxOutputTokens` | `max_tokens`/`num_predict` sent on every round |

Provider connection settings are shared by every task that uses that provider:
`Ollama__BaseUrl`, and `OpenRouter__ApiKey`, `OpenRouter__BaseUrl`,
`OpenRouter__HttpReferer`, `OpenRouter__AppTitle`, `OpenRouter__MaxRetries` (0 or 1).
Defaults are in `ai-assistant-service/appsettings.json`. `RequireParameters=true`
and `DataCollection=deny` are enforced.

Startup validates the entire task map: every profile must be complete, and
the OpenRouter key is required only if **any** task uses OpenRouter.
Changes take effect after a restart. Nothing is live-reloaded, and a failing
task never falls back to another task, model, or provider.

**Jev is not a chat model.** Do not set
`OPENROUTER_CHAT_MODEL=typesafe/jev-1.13`: this variable feeds the existing
Chat Completions adapter, while Jev uses the separate Decisions API.
[Jev category-first integration](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md#jev-category-first-integration-proposed)
is specified but not implemented. Adding a category model variable to `.env`
does not activate it. Keep the existing chat model and future category decision
model separate; sharing an OpenRouter key does not make their APIs compatible.

Compose does not forward host environment variables automatically. Every task
setting must appear in the service's `environment` in a Compose file.

## Prerequisites

- Copy `.env.example` to `.env` and fill in the existing secrets.
- Docker Compose with support for the `!reset` merge tag. Verified with
  Docker Compose v5.1.4. On any other version, run the `config` checks
  below before deploying; ordinary YAML merging cannot remove a dependency.
- For OpenRouter, you need:
  - A dedicated TaskMaster Hub key with the smallest practical credit limit
    and billing alerts. All OpenRouter tasks share this key, so its limit caps
    their combined spend, not each task's.
  - A model that supports both `tools` and `max_tokens`; check its OpenRouter
    model page.
  - A review of the upstream provider's retention and training terms before
    production use.

## Recipe 1: All-Ollama (default)

`docker-compose.yml` maps `chat` to `qwen3:8b` on the bundled `ollama` service,
with a 600 s round budget and a 3000 s request budget. `ollama-init` pulls the
model on first start. No OpenRouter key is needed, and none is warned about.

```powershell
docker compose config --quiet
docker compose up -d --build
docker compose down        # stop
```

## Recipe 2: OpenRouter-only

`docker-compose.openrouter.yml` switches `chat` to OpenRouter with a 60 s round
budget, a 300 s request budget, and 1024 output tokens. It moves `ollama` and
`ollama-init` behind the inactive `local-ollama` profile and removes only the
assistant's Ollama dependency. A fresh stack therefore neither starts Ollama
nor downloads a model.

Set `OPENROUTER_API_KEY` and `OPENROUTER_CHAT_MODEL` in `.env`; Compose refuses
to render the recipe without them.

```powershell
# Verify first. --quiet avoids printing resolved secrets.
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml config --quiet
# Should list no ollama services:
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml config --services

docker compose -f docker-compose.yml -f docker-compose.openrouter.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml down   # stop
```

Do not enable `--profile local-ollama` or name `ollama`/`ollama-init` explicitly
in this mode.

**Switching an existing all-Ollama stack.** Applying the override does not stop
containers that are already running. Stop Ollama with the base file, then apply the
override:

```powershell
docker compose stop ollama-init ollama
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml up -d --build
```

**Rollback to Ollama.** Use the base file alone. It restores the `chat`
provider, model, Ollama URL, 600/3000 s budgets, and Ollama services together:

```powershell
docker compose up -d --build
```

## Recipe 3: Mixed providers

Use this when some tasks use Ollama and others use OpenRouter. Start from the
**base file**, which keeps Ollama. Do not use `docker-compose.openrouter.yml`,
because it disables Ollama. Put every task's settings in a local override file.
Do not commit it if it contains environment-specific values; keep secrets in
`.env`.

Only `chat` has a production caller today. Other task IDs are for future
backend callers; the IDs below are synthetic examples, not features.

Example `docker-compose.mixed.yml`: OpenRouter `chat` plus a local Ollama task.

```yaml
services:
  ai-assistant-service:
    environment:
      AiModels__Tasks__chat__Provider: OpenRouter
      AiModels__Tasks__chat__Model: ${OPENROUTER_CHAT_MODEL:?OPENROUTER_CHAT_MODEL must be set}
      AiModels__Tasks__chat__TimeoutSeconds: "60"
      AiModels__Tasks__chat__RequestTimeoutSeconds: "300"
      AiModels__Tasks__chat__MaxOutputTokens: "1024"
      OpenRouter__ApiKey: ${OPENROUTER_API_KEY:?OPENROUTER_API_KEY must be set}
      AiModels__Tasks__synthetic_local__Provider: Ollama
      AiModels__Tasks__synthetic_local__Model: qwen3:8b
      AiModels__Tasks__synthetic_local__TimeoutSeconds: "600"
      AiModels__Tasks__synthetic_local__RequestTimeoutSeconds: "3000"
      AiModels__Tasks__synthetic_local__MaxOutputTokens: "256"
```

The reverse also works: keep `chat` on the base Ollama settings and add an
OpenRouter task. Any OpenRouter task makes `OpenRouter__ApiKey` required.

```powershell
docker compose -f docker-compose.yml -f docker-compose.mixed.yml config --quiet
docker compose -f docker-compose.yml -f docker-compose.mixed.yml up -d --build
```

`ollama-init` only pulls `qwen3:8b`. Pull every other local model used by any
Ollama task yourself, because startup validation doesn't check that a model exists:

```powershell
docker compose exec ollama ollama pull <model-tag>
```

## Per-task rollback and shared infrastructure

- Roll back one task by restoring **that task's** provider, model and budgets,
  then restart the assistant. Leave other profiles unchanged.
- Do not stop `ollama` while any task still uses Ollama.
- Do not remove `OPENROUTER_API_KEY` while any task still uses OpenRouter.
- Compose cannot infer which infrastructure the task map needs. Choose the recipe
  that matches the **whole** map, and run `config --quiet` before deploying.

## Verifying a deployment

Run the same deterministic recipe check used by CI from the repository root:

```powershell
.\scripts\Test-AiAssistantDeployments.ps1
```

It renders all-Ollama, OpenRouter-only, and both mixed directions using only
fake credentials, checks forwarded profiles and active service/dependency graphs,
and verifies that OpenRouter-only requires a key and model. It does not start
containers, download models, or call OpenRouter. On Linux CI, run it with `pwsh`.
This configuration check does not replace the operator-approved live smoke test
in Phase 3 of the integration checklist.

- `/health` only checks configuration and never calls a model.
- An invalid or revoked key is only detected on the first real chat request. Users
  get a safe "unavailable" answer, and the logs show the `Authentication` failure category.
- Telemetry (source/meter `TaskMaster.AiAssistant`) is exported only when
  `OTEL_SDK_DISABLED=false` and a reachable OTLP collector is configured. Compose
  disables it by default.

## Task 12: Bounded non-production chat evaluation

**Task 12 completed September 30, 2026 by operator confirmation and key-limit
verification.** The operator confirmed a working spending monitor, accepted
the criteria/privacy review and separately approved task 13. Its recorded
outcome below blocks production; task 14 approval remains separate.

### Selected profile and limits

| Item | Evaluation setting |
|---|---|
| Task/provider/model | `chat` / `OpenRouter` / `openai/gpt-4.1-mini` |
| Deployment | Local non-production stack, Recipe 2; no public test endpoint |
| Round/request/output budgets | 60 s / 300 s / 1024 tokens, as in the existing override |
| Retry/routing policy | Existing maximum one eligible retry; `require_parameters=true`, `data_collection=deny` |
| Dedicated key | Evaluation-only key, USD 2 lifetime limit, no scheduled reset |
| Observation window | One session, at most 30 application chat requests or 60 minutes, whichever comes first |
| Baseline | Base Compose Ollama `chat`, `qwen3:8b`, unchanged 600/3000 s budgets |
| Scope | Existing MCP tools, local read-only `get_bookings`, history and grounded answers |

All application requests count toward 30, including follow-ups, warm-ups,
failures and reruns. One chat request can make multiple billable model rounds
and retries; 30 requests is not a limit of 30 upstream calls. Do not run requests
concurrently or share the evaluation key with other applications/tasks.
The 60-minute window starts with the first paid request. Stop at any limit,
any safety failure or the spending stop threshold below. Do not enable Jev,
multi-category search or another production task caller for this evaluation.
Do not start a request unless the remaining session time covers its full
300-second request budget; finish or cancel in-flight work within the window.

The [model documentation](https://openrouter.ai/openai/gpt-4.1-mini) advertises
`tools` and `max_tokens`. The public
[endpoint metadata](https://openrouter.ai/api/v1/models/openai/gpt-4.1-mini/endpoints)
was checked on September 30, 2026: the OpenAI endpoint advertises both required
parameters, with USD 0.40 per million input tokens and USD 1.60 per million
output tokens. Prices and eligibility can change; recheck before task 13.
These rates are not a session-cost guarantee.

### Operator setup checklist (confirmed for the recorded session; required for reruns)

1. In [OpenRouter key settings](https://openrouter.ai/settings/keys), create a
   dedicated non-management evaluation key with a USD 2 lifetime credit limit
   and no reset schedule. Do not reuse an unrestricted key. Disable Auto Top-Up
   for the evaluation account; do not change a shared account's billing without
   its owner's approval. Do not raise the limit if the evaluation exhausts it.
2. Configure a local/operator-controlled spending alert using the
   [current-key endpoint](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-key)
   (`GET https://openrouter.ai/api/v1/key`) or an existing trusted monitoring
   facility. Poll every 30 seconds while the session is active, alert
   at USD 1 usage and stop issuing new requests at USD 1.50. Monitor failure or
   missing/invalid usage data also stops the session. Display only numeric usage,
   configured limit and remaining allowance; never headers, key labels or raw
   responses. Verify alert delivery before paid evaluation.
3. Treat [Activity](https://openrouter.ai/activity) and key usage as the spend
   source of truth, not token estimates. The official
   [spending guidance](https://openrouter.zendesk.com/hc/en-us/articles/51690568268059-How-do-I-cap-my-spending-and-stop-runaway-usage-from-coding-agents)
   warns that in-flight calls can overshoot a key limit. Serial execution, a
   USD 1.50 stop threshold and disabled Auto Top-Up provide headroom; a USD 2
   key limit alone is not an exact total-billing guarantee. The reviewed
   documentation does not establish a built-in configurable threshold alert:
   do not mark alerts configured merely because a key limit exists. Manual
   dashboard checks are supplemental, not proof of automatic alert delivery.
4. Install the key locally as `OPENROUTER_API_KEY` and set
   `OPENROUTER_CHAT_MODEL=openai/gpt-4.1-mini` in the untracked `.env`.
   Do not paste the key into chat, commands, screenshots or committed files.
   This preparation did not read or modify the existing `.env`.
5. Confirm the upstream privacy review below and accept/finalize the proposed
   success criteria. Record only configuration/readiness attestations, not
   credentials or account identifiers. Obtain explicit task-13 paid-call
   approval after these prerequisites are satisfied.

### Upstream privacy review

Reviewed September 30, 2026:

- [OpenRouter provider policies](https://openrouter.ai/docs/guides/privacy/provider-logging)
  distinguish training restrictions from retention. `data_collection=deny`
  does not promise zero retention. The model slug is not an upstream-provider
  pin; permitted same-model endpoints can vary.
- [OpenAI API data controls](https://developers.openai.com/api/docs/guides/your-data)
  state that API data is not used for training by default, but abuse-monitoring
  logs can retain content for up to 30 days, with stated exceptions. Approved
  zero-data-retention controls are not assumed for this key.
- The public OpenRouter provider-policy metadata currently lists OpenAI and
  Azure as not training on prompts, but with different retention flags.
  Metadata is not a substitute for applicable provider terms; recheck every
  eligible provider and account privacy settings before execution.

Use only fictional evaluation accounts, synthetic bookings/provider records
and synthetic requests. Existing tools send returned records to the chat
model: synthetic user prompts alone do not make real tool results safe to
send. Do not use customer data, private repository content, real booking
details or secrets. If actual eligible-provider terms cannot be accepted,
block evaluation rather than silently assuming ZDR or weakening routing.

### Acceptance criteria (accepted for the recorded session; reapprove changes)

Freeze the catalog, synthetic provider/bookings fixture and labeled scenarios
before comparing providers. Use the same fixture and user-authored history
for OpenRouter and Ollama. Run labeled scenarios twice where the 30-request
cap permits; count every turn separately and reserve room for failure checks.
Use at least 10 completed service-search samples for latency reporting.

| Measure | Proposed pass gate |
|---|---|
| Tool/category selection | At least 90% of labeled applicable decisions are correct; report search, booking and unsupported cases separately |
| Explicit filter grounding | 100% retain supplied filters and invent no location, budget or rating |
| Answer grounding | Zero invented provider/booking facts; zero sources/mentions from unexecuted tools or seeded examples |
| Empty results/history | Honest empty results; follow-up ranking uses verified data without invented filters or category broadening |
| Authentication/failure safety | Invalid key yields safe unavailability with no tool execution or sensitive logs; do not retry auth failures |
| Warm end-to-end latency | p95 at most 60 s and at most 1.2 times the paired Ollama baseline; report sample count and variability |
| Spend/window | No new calls at USD 1.50 usage; total observed usage at most USD 2, 30 requests and 60 minutes |
| Operations | Required topology, restart, telemetry and rollback checks from task 13 pass |

Small samples are smoke evidence, not proof of general category accuracy.
Record baseline failures honestly; do not relax criteria or change model,
fixtures or prompts mid-run without stopping and approving a revised plan.

Task-13 scenarios must include wooden-cupboard repair (`carpentry`), flat-pack
assembly (`furniture-assembly`), an explicit budget/location search, a
location-only search, a cheapest-provider follow-up, read-only booking lookup,
a known category with no matching providers, an unsupported service and safe
invalid-key handling. Distinguish first-run/restart timing from warm timing.
Infrastructure-only and synthetic mixed-profile checks must not add paid
domain callers. Follow the existing task-13 telemetry prerequisites.

Commit only scenario IDs, expected/observed pass flags, requested model,
aggregate latency/cost/usage and sanitized failure categories. Never commit
raw requests, histories, tool results, completions, credentials, booking IDs
or account identifiers. Include the observation start/end and fixture revision.

### Preparation checks and rollback

The existing fake-secret check is safe to run now:

```powershell
.\scripts\Test-AiAssistantDeployments.ps1
```

After the operator installs the dedicated key, validate the real recipe
without printing resolved credentials:

```powershell
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml config --quiet
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml config --services
```

These checks do not validate the key against OpenRouter or prove runtime
startup/tool behavior. Do not start the evaluation stack or submit chat
requests until task 13 is explicitly approved.

For the chat-only evaluation, stop submitting requests and restore Ollama
using Recipe 1 (base file only):

```powershell
docker compose config --quiet
docker compose up -d --build
```

The base file restores Ollama infrastructure and chat's provider/model/budgets.
Wait for initialization and verify `/health`, then rehearse a synthetic Ollama
chat in task 13; `/health` alone does not prove model readiness. Disable the
evaluation key after the run if no other approved caller needs it. Do not run
`down -v`, delete data or edit unrelated task profiles. For any actual mixed
task map, use the per-task rollback section instead of assuming chat is the
only caller.

## Task 13: Recorded evaluation outcome

**Evaluation executed; selected profile not approved for production.**
Session: September 30, 2026, 22:28:19-22:36:48 EDT
(October 1, 02:28:19-02:36:48 UTC). Source revision before evidence:
`0386891`. The operator approved paid calls, isolated local restarts/rollback,
fictional accounts and synthetic data; no production deployment was authorized.

Fixture: 36 catalog IDs and 19 local provider profiles. Eighteen profiles came
from the repository seed; the operator explicitly attested that all profiles,
including the additional one, were fictional test data. Seed file SHA-256:
`4d92a26bff4c94c8725efaa874395e233b669874861a4efe62b2f90f85d18726`.
Three fresh fictional customer
accounts were created; credential values and account identifiers are not
included in evidence. No databases or volumes were reset.

| Measure | Observed result | Gate |
|---|---|---|
| Warm search samples | 10 per provider: five scenarios, twice each; warm-ups excluded | Smoke sample floor met |
| Category/tool selection | OpenRouter 8/10; Ollama 10/10 | **Failed** OpenRouter's 90% target |
| Explicit location/rate/rating filters | No invented filters in observed calls | Passed |
| Provider facts/mentions | Reviewed synthetic answers against fixture; returned mention IDs/names matched actual profiles | Passed observed cases |
| Warm end-to-end p95 | OpenRouter 2.551 s; Ollama 1.648 s; ratio 1.548x | **Failed** 1.2x relative target; passed 60 s absolute target |
| OpenRouter-configured app requests | 19 total, including two warm-ups and one invalid-key request | Within 30-request limit |
| Reported key usage | USD 0.016990 after reconciliation, versus USD 2 limit | Within spending limits |
| Invalid key | Safe unavailable answer, empty sources/mentions, Authentication classification, no tool execution/retry or key-sentinel logging | Passed |
| Telemetry | Temporary local OTLP collector observed 30 chat spans, 58 model-round spans and token metrics; checked for prompt, credential-sentinel and actor leakage | Passed observed checks |
| Fresh assistant deployment | Separate temporary Compose project reused existing domain services; OpenRouter search worked while Ollama stayed stopped and merged recipe excluded its services | Passed this startup/topology check |
| Rollback | Real Ollama chat succeeded; restored `qwen3:8b`, 600/3000 s budgets and `OTEL_SDK_DISABLED=true` | Passed |

Both location-only requests added unrelated service categories instead of
omitting `category`, then misleadingly reported no matching providers.
This was a real model-selection failure, not a blank/null transport failure.
Other scenarios covered repair versus assembly, explicit budget/location,
cleaning, follow-up ranking, unsupported services and empty-location results.
The follow-up legitimately re-read provider data without inventing rate filters.

The booking scenario invoked the existing read-only tool and requested a
booking ID. Its current client cannot list bookings without an ID; this did
not exercise authenticated retrieval of a calendar record or establish
end-to-end booking-list support. No real booking records were sent.

Twelve deterministic integration/telemetry tests passed, including synthetic
mixed-provider/profile isolation. Those checks do not claim live production
callers for additional tasks. A complete new database-backed stack was not
created; the fresh-start check covered a new assistant container and the merged
service graph against the existing fictional domain fixture.

An initial smoke-harness guard mistook empty optional strings for invented
filters; product logs showed they normalized to absent filters. The guard was
corrected, that warm-up remained counted, and no application behavior or
acceptance criteria were changed. Provider usage updates lagged requests;
the reconciled figure above supersedes the immediate USD 0.0160008 snapshot.

Temporary evaluation containers and the collector were removed. The local
application remains on Ollama with telemetry disabled; the evaluation key
remains installed but is not used by that chat profile. The operator may
disable it if no other approved caller needs it. Raw synthetic review artifacts
are not committed evidence.

**Release blocker:** do not proceed to task 14 with this profile under the
accepted criteria. Investigate location-only selection and latency or evaluate
another explicitly approved chat profile, preserving the same fixture and
recording any approved criterion changes. Jev and multi-category work remain
separate and disabled.
