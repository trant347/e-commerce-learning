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
