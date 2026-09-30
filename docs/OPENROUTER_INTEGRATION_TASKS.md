# OpenRouter Integration and Task-Specific Model Tasks

Status: **In progress; tasks 1-7 are complete. OpenRouter error/retry handling and deployment work remain pending.**

Source: [OpenRouter Integration Specification](OPENROUTER_INTEGRATION_SPEC.md).
The specification is authoritative; this checklist organizes its implementation.

## Scope and workflow

Change the AI assistant, its tests, configuration, and deployment documentation.
Reuse product-service's existing MCP server. Keep the booking tool local to the
assistant; converting calendar-service to MCP is not part of this feature.
Do not add browser-to-OpenRouter access, streaming, new domain tools, automatic
model selection, or changes to payment/booking contracts.

Implement a configuration-driven provider/model pair per task, selected by
explicit backend task IDs. Only existing `chat` becomes a production caller
here. Category resolution and hiring remain separate features; do not add their
workflows, prompts, output schemas, or endpoints. Jev/Gemma are not hard-coded or
assumed-compatible dependencies. Mixed Ollama/OpenRouter task maps are supported;
synthetic internal callers demonstrate the foundation without adding features.

Each task includes its own tests and completion criteria. Implement, test, verify,
and commit small working increments rather than postponing tests until the end.
Tasks in Phase 1 may share an increment where needed to keep the service buildable.
Paid evaluation and production deployment require separate operator approval.

## Phase 1: Provider-neutral assistant, with Ollama still working

- [x] **1. Establish the baseline and migration inventory.**
  Locate all callers and tests of `IOllamaClient`, Ollama message types, and
  `GenerateAsync`; inspect model/timeout settings and seeded examples.
  Run the existing assistant tests and record any pre-existing failures.
  **Done when:** affected files and callers are accounted for, and unrelated
  failures are distinguishable from regressions introduced by the feature.

  **Baseline recorded 2026-09-28 (historical pre-migration snapshot):**

  - `AiAssistantService` is the only production caller of `IOllamaClient`. It
    calls `ChatAsync` for every model round and directly constructs, mutates,
    and inspects `OllamaChatMessage`, `OllamaToolCall`, and
    `OllamaToolCallFunction`. It also owns inline tool-call recovery, tool
    definition projection, tool-result messages, and mention extraction over
    Ollama-named messages.
  - `OllamaClient` is the only implementation and `Program.cs` contains its
    only production registration. `GenerateAsync` has no production caller;
    its sole caller is the model-not-found regression test in
    `OllamaClientTests`, so its failure coverage must be migrated before the
    legacy method is removed.
  - `AiAssistantServiceTests` is the only suite mocking `IOllamaClient` or
    directly assembling Ollama tool-call messages. `OllamaClientTests` is the
    only adapter/wire-format suite using the Ollama message types directly.
  - The model is read by `AiAssistantService` from `Ollama:Model`, with a
    `llama3.2:3b` code fallback. The HTTP base URL and timeout are read during
    client registration from `Ollama:BaseUrl` and `Ollama:TimeoutSeconds`, with
    `http://localhost:11434` and 60-second code fallbacks.
  - `appsettings.json` configures `qwen3:8b`, a 60-second timeout, and
    `http://host.docker.internal:11434`. Base Compose overrides the URL for the
    `ollama` service, keeps `qwen3:8b`, and raises the timeout to 600 seconds;
    `ollama-init` pulls that same model. `appsettings.Development.json` changes
    logging only.
  - The single configured few-shot example is bound through
    `FewShotExample`/`FewShotTurn`/`FewShotToolCall`, then materialized by
    `AiAssistantService` into Ollama messages. Its assistant tool call and
    following tool result currently have no call/result IDs, which task 5 must
    migrate and validate.
  - Baseline command:
    `dotnet test ai-assistant-service.Tests\ai-assistant-service.Tests.csproj --nologo --verbosity normal`.
    Result: 59 passed, 0 failed, 0 skipped. The pre-existing build warnings are
    NU1902 for `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.11.2
    (GHSA-4625-4j76-fww9) and CS8601 at
    `Services\Clients\OllamaClient.cs:34`; neither is a test failure.

- [x] **2. Introduce shared chat contracts and typed failures.**
  Depends on 1. Add `IChatModelClient`, typed messages, tool definitions, calls,
  results, response metadata, usage types, and per-request output limits from
  spec Section 3. Add `ITaskChatModelResolver`, `ITaskChatModelClient`, and the
  immutable `TaskModelProfile` selection contract. Keep provider
  wire DTOs separate and JSON values valid after document disposal. Define
  failure categories supporting Section 7 without raw payloads in exceptions.
  **Done when:** contracts express multiple calls with matching result IDs,
  typed schema/arguments, requested versus resolved models, and safe failures.

  **Implemented 2026-09-28:**

  - Added immutable provider-neutral request, response, message, tool,
    tool-call, usage, task-profile, resolver, and client contracts under
    `Services\Contracts`. Inputs are snapshotted so callers cannot mutate an
    in-flight contract through retained lists or dictionaries.
  - Tool schemas and tool-call arguments require JSON objects and clone retained
    `JsonElement` values, keeping them valid after source documents are disposed.
    Messages represent multiple stable call IDs and matching tool-result IDs;
    whole-conversation duplicate/dangling-pair validation remains in task 5.
  - Added semantic failure categories and allowlisted diagnostic metadata
    without arbitrary provider messages, response bodies, or retry policy.
    Provider status mapping, retry decisions, and presentation remain assigned
    to tasks 4, 6, and 8.
  - Added focused contract tests covering JSON lifetime, typed scalar argument
    values, immutable snapshots, intrinsic invalid states, multiple
    calls/results, budget validation, and failure-metadata safety. The full
    assistant suite passes 67 tests with no failures.
    Existing Ollama runtime contracts, registration, configuration, and callers
    are unchanged.

- [x] **3. Add the task map, resolver, and isolated timeout budgets.**
  Depends on 2. Add strongly typed `AiModels:Tasks` and Ollama options. Require a
  complete `chat` profile; permit and validate additional task IDs. Migrate
  `Ollama:Model` and `Ollama:TimeoutSeconds`, including existing Compose mappings,
  to `AiModels:Tasks:chat` without breaking the current local deployment.
  Do not introduce the superseded global `AiModel` configuration. Preserve the specified
  appsettings/local Compose timeout values. Introduce separate round and
  end-to-end budgets with linked cancellation. Resolve profiles into immutable
  task-bound clients over independently registered adapters; never mutate shared
  HttpClient state. Fail unknown task resolution before HTTP with diagnostics,
  not a fallback to chat. Changes require restart/redeployment.
  **Done when:** missing/incomplete profiles and invalid IDs/providers/budgets
  fail startup, the default chat mapping remains Ollama, same-provider synthetic
  tasks select independent models/limits, and cancellation/deadline tests pass.
  Keep OpenRouter unavailable until its adapter and options are wired in task 7.

  **Implemented 2026-09-28:**

  - Added strongly typed `AiModels:Tasks` and Ollama connection options with
    startup validation for the required `chat` profile, every additional
    profile, canonical task IDs, supported providers, models, positive budgets,
    budget ordering, output limits, and the Ollama base URL. OpenRouter profiles
    fail validation explicitly until task 7 wires that provider.
  - Added an immutable startup profile catalog, independently registered
    provider-client registrations, deterministic task resolver, and task-bound
    clients. Unknown tasks fail before adapter execution with no fallback to
    `chat`; same-provider tasks keep independent model and output-limit values.
  - Added linked per-round cancellation that maps internal expiry to a typed
    timeout while preserving caller cancellation, plus a separate linked
    end-to-end request deadline for orchestration to adopt in task 5.
  - Migrated application, legacy caller, tests, and base Compose settings from
    `Ollama:Model`/`Ollama:TimeoutSeconds` to `AiModels:Tasks:chat`. Appsettings
    retains 60/300-second round/request budgets; local Compose retains a
    600-second round budget and uses the specified 3000-second request budget.
  - Focused validation, isolation, unknown-task, and deadline tests pass. The
    full assistant suite passes 81 tests, and `docker compose config --quiet`
    renders successfully with a placeholder JWT secret.

- [x] **4. Adapt Ollama to the shared contract.**
  Depends on 2-3. Implement `OllamaChatModelClient`, preserving `/api/chat`,
  `think: false`, optional tools, and zero automatic retries. Generate missing
  call IDs. Move inline-call recovery into the adapter and validate tool names
  against request definitions, not an injected `ToolRegistry`.
  Return typed failures instead of fabricated assistant messages.
  Remove `GenerateAsync` only after the caller inventory permits it; migrate
  relevant tests rather than dropping failure coverage.
  **Done when:** native and inline calls, IDs, timeouts, unavailable models,
  malformed responses, and connection failures have adapter tests.

  **Implemented 2026-09-28:**

  - Registered `OllamaChatModelClient` independently with the task resolver.
    Its HTTP client has no separate timeout; the bound task supplies the round
    cancellation budget. Calls use `/api/chat`, `stream: false`, `think: false`,
    optional native tool definitions, and `options.num_predict` for the profile's
    output limit. No automatic retries are added.
  - Native and recovered inline calls are checked against the request's tool
    definitions. Missing IDs are generated; supplied IDs are retained and checked
    for collisions with other calls and conversation history. Parsed arguments
    retain their JSON types and survive response-document disposal.
  - Follow-up requests translate internal result IDs to Ollama's `tool_name`
    using preceding calls, preserving message order instead of guessing which
    result belongs to which call. Provider wire DTOs remain private to the adapter.
  - Malformed/empty/incomplete output, invalid arguments, unavailable models,
    HTTP and transport failures produce safe typed exceptions rather than
    assistant text. Caller cancellation propagates. Diagnostic metadata retains
    status and requested model, not provider response bodies or inner exceptions.
  - Removed unused `GenerateAsync` and its wire DTOs; model-not-found coverage
    remains in both the legacy chat tests and the new adapter status tests.
    Inline recovery moved out of `AiAssistantService` and is shared by the new
    adapter and the temporary legacy client. Its orchestration test moved to
    the provider boundary, with compatibility coverage for the legacy path.
  - Capturing-handler tests exercise actual serialization/parsing, multi-call
    follow-up requests, inline formats, invalid output, cancellation, no-retry
    failures, and real DI resolution with two synthetic task profiles.
    No Ollama server or paid calls are needed.

  **Historical task 4 transition boundary:** The assistant still used
  `IOllamaClient` while the new adapter was usable through the resolver.
  Task 5 below completes that migration; task 6 retains the logging audit.
  OpenRouter remains unavailable until task 7.

- [x] **5. Migrate orchestration and few-shot examples.**
  Depends on 2-4. Have `AiAssistantService` resolve the server-owned `chat` ID once
  and retain its profile/client through every round/retry. Set the end-to-end
  deadline once from that profile. Keep task/provider/model selection out of
  public input and the tool loop. Use typed definitions.
  Extend example models/configuration with explicit call/result
  IDs and validate missing, duplicate, dangling, or unresolved pairs at startup.
  Keep IDs collision-free in the assembled conversation.
  Preserve prompt/history order, tools on every round, sequential execution,
  grounding, `ToolExecutionContext`, and the five-round guard.
  **Done when:** the first request has valid seeded pairs; subsequent requests
  carry the correct IDs/results; successful sources and mentions come only
  from this request's executed tools; orchestration has no Ollama-named types.
  No category-resolution/hiring caller or model-driven router is introduced.

  **Implemented 2026-09-28:**

  - `AiAssistantService` resolves the server-owned `chat` task once and retains
    the selected client/profile. One linked request deadline covers prompt
    assembly, every model round, and sequential tool execution. Provider model,
    limits, and round budgets remain inside the task-bound client.
  - Added validated `PromptOptions` and an immutable `AssistantPrompt` snapshot.
    Few-shot call/result IDs are explicit in configuration. Startup rejects
    missing IDs, duplicate calls/results, dangling results, and unresolved
    calls before another turn or the end of an example. IDs are unique across
    examples; live IDs are checked against the assembled conversation before
    executing any tool in a returned batch.
  - Shared message/tool contracts now carry the complete conversation; result
    messages reference their originating call IDs. Prompt/history order, the
    five-round guard, user-only filter grounding, trusted execution context,
    and request-local sources/mentions are preserved. Exhaustion cannot return
    a seeded or historical answer.
  - Removed `IOllamaClient`, the legacy client, and their wire types/registration.
    Their HTTP behavior is covered by adapter tests; friendly error responses
    are now covered at the service boundary. Minimal failure mapping and
    cancellation propagation were included to avoid regressions when activating
    the typed adapter. Task 6 still owns the remaining logging audit and broader
    failure/cancellation checks.
  - Tests cover startup rejection, sequential multi-call execution, ID pairing,
    selection isolation from public input, grounding, partial-progress failures,
    deadlines, and metadata isolation. Integration tests load the actual
    appsettings examples and run the real resolver/adapter for both native and
    inline calls, with only the provider HTTP response stubbed.
    The full assistant suite passes 146 tests; the existing NU1902 dependency
    advisory remains unchanged.

- [x] **6. Complete safe failure handling and remove unsafe existing logs.**
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

  **Implemented 2026-09-29:**

  - `AiAssistantService` keeps provider failure presentation at the service
    boundary, clears sources and mentions after failures, preserves the existing
    Ollama messages, and lets both ordinary and typed caller cancellation escape.
    Failure logs retain only validated task/provider/model and allowlisted status
    and provider-request metadata.
  - Removed actor values, dropped-filter names, tool-result payloads, extracted
    IDs/names, JSON exception text, and exception objects from assistant logs.
    Tool execution now records only the tool name, exception type, result length,
    and outcome metadata. The booking tool client no longer logs booking IDs or
    exception details and now propagates caller cancellation.
  - Removed the assistant-specific Debug overrides from both appsettings files.
    Tests run a capturing Debug logger with sentinel prompts, arguments, results,
    completions, and exception messages to prevent payload regressions.

**Phase gate:** Ollama still completes the existing flows with the new boundary.
Successful behavior is preserved; intentional error/cancellation changes are
covered before introducing the external provider.

## Phase 2: OpenRouter adapter and deployment support

- [x] **7. Implement OpenRouter translation and mixed-provider resolution.**
  Depends on Phase 1. Add validated shared OpenRouter options and independently
  register its adapter alongside Ollama. Require its key/base URL if any configured
  task uses it, even when chat uses Ollama; require a model in each profile.
  All OpenRouter profiles share the server-side key and routing/retry policy.
  Send requests to the full `/api/v1/chat/completions` path with bearer auth,
  optional attribution and hidden visibility, `stream: false`, `max_tokens`,
  and configured routing options. Take the model and output limit from the bound
  profile, not global defaults. Never send `think`.
  Parse stringified JSON-object arguments, preserve IDs, and serialize tool
  results with `tool_call_id`. Include tools every round. Keep resolved model
  and usage diagnostic-only; public `Model` remains the configured identifier.
  **Done when:** capturing-handler tests verify URLs with either base-URL slash
  form, headers, routing, profile-specific output caps, argument types, and
  multi-call round trips. Synthetic task callers can select different models on
  one adapter or different providers in the same process without state leakage.
  No real API calls belong in these tests.

  **Implemented 2026-09-29:**

  - Added validated shared `OpenRouterOptions` for the base URL, server-side key,
    optional attribution, routing privacy/settings, and the future bounded retry
    limit. OpenRouter settings are required only when at least one task selects
    OpenRouter; all-Ollama maps still start without its key or URL.
  - Registered `OpenRouterChatModelClient` independently from Ollama. The task
    resolver can now bind different tasks to different providers or different
    models/output limits on the shared OpenRouter adapter without mutable state.
  - Added non-streaming Chat Completions translation for messages, definitions,
    assistant tool calls, matching `tool_call_id` results, routing settings,
    attribution headers, resolved-model/request diagnostics, and token usage.
    Native stringified arguments must parse as JSON objects and retain their
    original value types and IDs.
  - Capturing-handler tests cover both base-URL slash forms, bearer/attribution
    headers, hidden visibility, privacy routing, absence of `think`, per-profile
    output limits, multi-call follow-up serialization, malformed arguments, and
    all-OpenRouter/mixed-provider resolution. No live OpenRouter request is made.
  - Full OpenRouter status/envelope classification and bounded retries remain
    owned by Task 8; Task 7 surfaces non-success responses without retrying.

- [ ] **8. Implement OpenRouter errors and bounded retries.**
  Depends on 7. Handle every status/category in spec Section 7, including HTTP
  200 error envelopes before reading choices. Reject malformed/empty output,
  invalid arguments, missing names/IDs, truncated completions, and policy blocks
  without executing tools. Unknown error codes must fail explicitly.
  Allow at most one configured retry of only the current model request.
  Honor `Retry-After` seconds/dates and shared deadlines; 402 requires both the
  in-flight-budget metadata and a valid header. Never retry cancellation or
  restart completed tool execution. Never switch task, provider, or model on
  failure; keep retry/deadline state isolated per invocation.
  **Done when:** tests cover retryable/non-retryable cases, both missing 402
  conditions, invalid headers, over-budget delays, cancellation, and exhaustion.

- [ ] **9. Add metadata-only assistant telemetry.**
  Depends on 6-8. Register the `TaskMaster.AiAssistant` activity source and meter.
  Instrument chat/round duration, tool calls, failures, retries, and available
  token usage, with validated task ID and provider/requested/resolved model
  metadata as appropriate. Never use unknown input task names as metric labels.
  Keep generation IDs off metric labels and payloads out of all telemetry.
  Retain disabled-by-default Compose export and configuration-only health checks.
  **Done when:** in-process activity/meter listener tests observe the expected
  metadata without a collector, inference calls, or sensitive payloads.

- [ ] **10. Document and wire all-Ollama, OpenRouter-only, and mixed deployments.**
  Depends on 7-8. Keep base Compose on Ollama; implement the override specified
  in Section 6, removing only the Ollama dependency and placing both Ollama
  services behind an inactive profile for OpenRouter-only use. Require
  `OPENROUTER_CHAT_MODEL`; map the shared optional secret without warnings for
  all-Ollama maps. Forward task settings as `AiModels__Tasks__<id>__<setting>`.
  Document the mixed recipe on base Compose with explicit environment mappings;
  retain Ollama when any task needs it and provision every selected local model.
  Host environment entries are not automatically forwarded to containers.
  Update `.env.example` and deployment README with a verified Compose version
  supporting the reset syntax, timeout values, secrets, credit limits, model
  capability checks, start/stop commands, shared key limits, and per-task rollback.
  Never stop infrastructure or remove a key that another task still needs.
  **Done when:** all three recipes render; OpenRouter-only has no active Ollama
  services/dependency, mixed retains required services, and all-Ollama needs no
  OpenRouter key. Use synthetic profiles and fake secrets for mixed-config
  inspection, not new domain workflows or real credentials.

- [ ] **11. Close automated integration and regression coverage.**
  Depends on 7-10. Exercise task resolution and startup validation, seeded
  message serialization through the real adapters, multi-round tool execution,
  partial-progress failures, requested/resolved model behavior, and preserved
  auth/controller behavior. Re-run assistant regressions, including category
  grounding, sources, mentions, and tool authorization.
  Cover simultaneous synthetic tasks with different providers/models/budgets,
  unknown task failures, missing chat, invalid unused profiles, and any-profile
  secret requirements. Verify no cross-task transcripts/tools/credentials leak,
  failure never changes routing, and public input cannot override chat's mapping.
  Change one profile and restart; verify other task selections are unchanged.
  Review the checklist against every criterion in spec Section 12.
  **Done when:** the assistant build and test suite pass, all three deployment recipes
  validate, and default CI remains deterministic with no paid OpenRouter calls.

## Phase 3: Operator-approved non-production evaluation

- [ ] **12. Prepare a bounded evaluation deployment.**
  Depends on 11. Obtain approval before paid calls. Create a dedicated
  credit-limited key, configure alerts, choose a model supporting `tools` and
  `max_tokens`, and review upstream retention/training terms. Define acceptable
  grounding/tool-selection results, latency, spend, and an observation period
  before evaluating. Keep keys and user payloads out of committed evidence.
  **Done when:** the chat profile/model and evaluation criteria are documented, deployment
  secrets are configured, and operators have a usable rollback procedure.

- [ ] **13. Run smoke scenarios and rehearse rollback.**
  Depends on 12. Exercise TaskMaster MCP search, the existing local booking
  tool, final answers from tool results, follow-up history, and safe invalid-key
  handling. Compare representative category prompts with Ollama for correctness,
  grounding, latency, and cost. Enable server telemetry explicitly for its smoke
  scenario. Verify fresh OpenRouter-only deployment does not start/download
  Ollama. Verify mixed deployment retains it when needed; switching an existing
  stack stops it only when no profile uses it. Rehearse restoring chat's
  provider/model/budgets and infrastructure without changing other task mappings.
  Use synthetic internal harness calls for mixed-profile checks, with no public
  testing endpoint and no category/hiring implementation.
  **Done when:** non-sensitive evidence records outcomes against the agreed
  criteria, and failures are resolved or explicitly block rollout.

## Phase 4: Production opt-in

- [ ] **14. Approve, deploy, and monitor the selected task mapping.**
  Depends on 13 and explicit deployment approval. Enable OpenRouter for chat only
  in the intended environment. Monitor failures, latency, tool rounds, tokens, and spend by task,
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

Task 10 must document the exact command/environment mappings for the mixed recipe
and validate it with `docker compose config --quiet` as well. Do not use the
OpenRouter-only override for mixed profiles that require local Ollama.

Configuration validation alone does not prove service startup, tool execution,
or absence of model downloads; verify those outcomes in task 13.
