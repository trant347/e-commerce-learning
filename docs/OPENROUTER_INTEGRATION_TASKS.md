# OpenRouter Integration Tasks

Status: **Planned; implementation tasks are not yet marked complete.**

Source: [OpenRouter Integration Specification](OPENROUTER_INTEGRATION_SPEC.md).
The specification is authoritative; this checklist organizes its implementation.

## Scope and workflow

Change the AI assistant, its tests, configuration, and deployment documentation.
Reuse product-service's existing MCP server. Keep the booking tool local to the
assistant; converting calendar-service to MCP is not part of this feature.
Do not add browser-to-OpenRouter access, streaming, new domain tools, automatic
model selection, or changes to payment/booking contracts.

Each task includes its own tests and completion criteria. Implement, test, verify,
and commit small working increments rather than postponing tests until the end.
Tasks in Phase 1 may share an increment where needed to keep the service buildable.
Paid evaluation and production deployment require separate operator approval.

## Phase 1: Provider-neutral assistant, with Ollama still working

- [ ] **1. Establish the baseline and migration inventory.**
  Locate all callers and tests of `IOllamaClient`, Ollama message types, and
  `GenerateAsync`; inspect model/timeout settings and seeded examples.
  Run the existing assistant tests and record any pre-existing failures.
  **Done when:** affected files and callers are accounted for, and unrelated
  failures are distinguishable from regressions introduced by the feature.

- [ ] **2. Introduce shared chat contracts and typed failures.**
  Depends on 1. Add `IChatModelClient`, typed messages, tool definitions, calls,
  results, response metadata, and usage types from spec Section 3. Keep provider
  wire DTOs separate and JSON values valid after document disposal. Define
  failure categories supporting Section 7 without raw payloads in exceptions.
  **Done when:** contracts express multiple calls with matching result IDs,
  typed schema/arguments, requested versus resolved models, and safe failures.

- [ ] **3. Add validated model configuration and timeout budgets.**
  Depends on 2. Add strongly typed `AiModel` and Ollama options. Migrate
  `Ollama:Model` and `Ollama:TimeoutSeconds`, including existing Compose mappings,
  without breaking the current local deployment. Preserve the specified
  appsettings/local Compose timeout values. Introduce separate round and
  end-to-end budgets with linked cancellation.
  **Done when:** invalid providers/budgets fail startup, Ollama remains the
  default, and tests distinguish caller cancellation from deadline expiry.
  Keep OpenRouter unavailable until its adapter and options are wired in task 7.

- [ ] **4. Adapt Ollama to the shared contract.**
  Depends on 2-3. Implement `OllamaChatModelClient`, preserving `/api/chat`,
  `think: false`, optional tools, and zero automatic retries. Generate missing
  call IDs. Move inline-call recovery into the adapter and validate tool names
  against request definitions, not an injected `ToolRegistry`.
  Return typed failures instead of fabricated assistant messages.
  Remove `GenerateAsync` only after the caller inventory permits it; migrate
  relevant tests rather than dropping failure coverage.
  **Done when:** native and inline calls, IDs, timeouts, unavailable models,
  malformed responses, and connection failures have adapter tests.

- [ ] **5. Migrate orchestration and few-shot examples.**
  Depends on 2-4. Switch `AiAssistantService` to the shared client and typed
  definitions. Extend example models/configuration with explicit call/result
  IDs and validate missing, duplicate, dangling, or unresolved pairs at startup.
  Keep IDs collision-free in the assembled conversation.
  Preserve prompt/history order, tools on every round, sequential execution,
  grounding, `ToolExecutionContext`, and the five-round guard.
  **Done when:** the first request has valid seeded pairs; subsequent requests
  carry the correct IDs/results; successful sources and mentions come only
  from this request's executed tools; orchestration has no Ollama-named types.

- [ ] **6. Complete safe failure handling and remove unsafe existing logs.**
  Depends on 4-5. Map failures in `AiAssistantService`, outside the tool loop's
  provider-independent logic. Keep the specified Ollama messages and deliberate
  invalid-output/cancellation changes. Handled failures return safe text, the
  requested model, and empty sources/mentions, even after a successful tool round.
  Preserve controller authentication, validation, and handled-failure HTTP status.
  Let caller cancellation escape provider and tool catches.
  Remove argument dumps, payload-bearing Debug logs, raw error-body logging,
  and the assistant's Debug override; redact headers and audit exception logs.
  **Done when:** failure/cancellation tests pass and captured Information/Debug
  logs contain no sentinel keys, prompts, arguments, results, or completions.

**Phase gate:** Ollama still completes the existing flows with the new boundary.
Successful behavior is preserved; intentional error/cancellation changes are
covered before introducing the external provider.

## Phase 2: OpenRouter adapter and deployment support

- [ ] **7. Implement OpenRouter configuration and wire translation.**
  Depends on Phase 1. Add validated OpenRouter options and register exactly one
  selected implementation at startup. Require its key/model only when selected.
  Send requests to the full `/api/v1/chat/completions` path with bearer auth,
  optional attribution and hidden visibility, `stream: false`, `max_tokens`,
  and configured routing options. Never send `think`.
  Parse stringified JSON-object arguments, preserve IDs, and serialize tool
  results with `tool_call_id`. Include tools every round. Keep resolved model
  and usage diagnostic-only; public `Model` remains the configured identifier.
  **Done when:** capturing-handler tests verify URLs with either base-URL slash
  form, headers, routing, output caps, argument types, and multi-call round trips.
  No real API calls belong in these tests.

- [ ] **8. Implement OpenRouter errors and bounded retries.**
  Depends on 7. Handle every status/category in spec Section 7, including HTTP
  200 error envelopes before reading choices. Reject malformed/empty output,
  invalid arguments, missing names/IDs, truncated completions, and policy blocks
  without executing tools. Unknown error codes must fail explicitly.
  Allow at most one configured retry of only the current model request.
  Honor `Retry-After` seconds/dates and shared deadlines; 402 requires both the
  in-flight-budget metadata and a valid header. Never retry cancellation or
  restart completed tool execution.
  **Done when:** tests cover retryable/non-retryable cases, both missing 402
  conditions, invalid headers, over-budget delays, cancellation, and exhaustion.

- [ ] **9. Add metadata-only assistant telemetry.**
  Depends on 6-8. Register the `TaskMaster.AiAssistant` activity source and meter.
  Instrument chat/round duration, tool calls, failures, retries, and available
  token usage, with provider/requested/resolved model metadata as appropriate.
  Keep generation IDs off metric labels and payloads out of all telemetry.
  Retain disabled-by-default Compose export and configuration-only health checks.
  **Done when:** in-process activity/meter listener tests observe the expected
  metadata without a collector, inference calls, or sensitive payloads.

- [ ] **10. Add the OpenRouter Compose override and deployment instructions.**
  Depends on 7-8. Keep base Compose on Ollama; implement the override specified
  in Section 6, removing only the Ollama dependency and placing both Ollama
  services behind an inactive profile. Require an OpenRouter model; map the
  optional secret without warnings in Ollama mode.
  Update `.env.example` and deployment README with a verified Compose version
  supporting the reset syntax, timeout values, secrets, credit limits, model
  capability checks, start/stop commands, and full rollback configuration.
  **Done when:** both configurations render; OpenRouter's active graph has no
  Ollama services/dependency, other dependencies remain, and default Ollama
  needs no OpenRouter key. Use fake secrets for rendered-config inspection.

- [ ] **11. Close automated integration and regression coverage.**
  Depends on 7-10. Exercise both DI selections and startup validation, seeded
  message serialization through the real adapters, multi-round tool execution,
  partial-progress failures, requested/resolved model behavior, and preserved
  auth/controller behavior. Re-run assistant regressions, including category
  grounding, sources, mentions, and tool authorization.
  Review the checklist against every criterion in spec Section 12.
  **Done when:** the assistant build and test suite pass, both Compose modes
  validate, and default CI remains deterministic with no paid OpenRouter calls.

## Phase 3: Operator-approved non-production evaluation

- [ ] **12. Prepare a bounded evaluation deployment.**
  Depends on 11. Obtain approval before paid calls. Create a dedicated
  credit-limited key, configure alerts, choose a model supporting `tools` and
  `max_tokens`, and review upstream retention/training terms. Define acceptable
  grounding/tool-selection results, latency, spend, and an observation period
  before evaluating. Keep keys and user payloads out of committed evidence.
  **Done when:** the model and evaluation criteria are documented, deployment
  secrets are configured, and operators have a usable rollback procedure.

- [ ] **13. Run smoke scenarios and rehearse rollback.**
  Depends on 12. Exercise TaskMaster MCP search, the existing local booking
  tool, final answers from tool results, follow-up history, and safe invalid-key
  handling. Compare representative category prompts with Ollama for correctness,
  grounding, latency, and cost. Enable server telemetry explicitly for its smoke
  scenario. Verify fresh OpenRouter deployment does not start/download Ollama;
  verify switching an existing stack stops it as documented. Rehearse restoring
  Ollama's provider, model, URL, timeouts, and services without code changes.
  **Done when:** non-sensitive evidence records outcomes against the agreed
  criteria, and failures are resolved or explicitly block rollout.

## Phase 4: Production opt-in

- [ ] **14. Approve, deploy, and monitor the selected provider.**
  Depends on 13 and explicit deployment approval. Enable OpenRouter only in the
  intended environment, monitor failures, latency, tool rounds, tokens, and spend,
  and roll back if agreed criteria are breached. Retain the Ollama deployment
  option throughout the agreed stability period.
  **Done when:** operators accept the rollout against the evaluation criteria.
  Update the spec/task status to reflect actual completion, not just code merge.

## Validation commands

Run from the repository root. Use focused test filters during each increment;
run the whole assistant suite at the integration gate. The repository has no
configured lint command; do not invent one for this feature.

```powershell
dotnet build ai-assistant-service\ai-assistant-service.csproj --nologo
dotnet test ai-assistant-service.Tests\ai-assistant-service.Tests.csproj --nologo --verbosity normal

# After task 10; supply the required environment placeholders/secrets.
# --quiet avoids printing resolved credentials.
docker compose config --quiet
docker compose -f docker-compose.yml -f docker-compose.openrouter.yml config --quiet
```

Configuration validation alone does not prove service startup, tool execution,
or absence of model downloads; verify those outcomes in task 13.
