# TaskMaster Category Management and Jev Tasks

Status: **Original catalog phase and JEV-01 through JEV-08 complete; release (JEV-09) and multi-category tasks pending.**

Source: [TaskMaster Category Resolution Specification](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md).
Multi-category extension: [TaskMaster Multi-Category Search Specification](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md).

The specifications are authoritative; the active checklist below organizes
the remaining work. The original phase numbers later in this file refer to
the completed catalog project, not the new hybrid implementation phases.
The historical requirement
to implement missing-category proposals before AI improvements is withdrawn.
No database reset is required for the hybrid rollout.

## Completed original catalog scope

The catalog phase introduced the admin-managed, database-backed catalog and
selection-only forms. It did not change AI prompts, search-tool arguments,
ranking, category limits, or natural-language resolution. Single-category
search remains the contract; the previous proposed expansion to up to three
categories has been superseded. A new, separately gated design now exists in
[multi-category search](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md).

## Finalized data-model contract

Use a dedicated `Category` model and `categories` collection, not a new
singular field on each TaskMaster.

Keep `TaskMaster.jobCategories` and application `jobCategories` as arrays of
canonical category IDs, as specified. This preserves existing profile contracts
and allows providers to offer more than one service without changing AI search.

Initial category fields are immutable canonical `id`, `displayName`,
server-managed `normalizedDisplayName`, `description`, and server-managed
creation/update timestamps and actor identity. IDs use lowercase hyphenated
ASCII slugs; both IDs and normalized display names are unique. The complete
normalization, validation, API, and error contract is recorded in the source
specification.

Aliases were excluded from the original completed phase. The first hybrid
prerequisite added them on September 30, 2026: normalized product-owned arrays,
strict admin API validation, omission-preserving updates and alias editing in
the admin UI. The next prerequisite added versioned rich MCP catalog exposure
and authoritative search-category validation, also on September 30, 2026.
Existing documents need no reset. Assistant rich snapshots and Jev
interpretation are implemented behind the disabled-by-default rollout modes;
see the source specification.
Object terms, related-category scoring, merging,
retirement, and required profile attributes remain outside the current rollout.
Removing referenced categories requires an explicit reassignment policy.

## Active plan: Jev category-first integration

### Resume here

As of October 10, 2026, **JEV-09 deterministic verification and the first live
end-to-end comparison are complete, but release is blocked by the latency gate**.
Jev category-first orchestration is wired behind the rich-catalog modes; legacy
remains the default, and multi-category search is not enabled.
The intended flow is user request -> category decision against product-owned
metadata plus typed filter extraction by the configured chat model (intended:
Ollama) -> validated MCP search -> grounded answer from the chat model.
Classification/extraction may run concurrently, but search must wait for both
validated outcomes. Product-service retrieval order is preserved; there is no
post-retrieval Jev scoring stage.
The first gated release retains one category; the MC tasks below add the
planned maximum of one primary plus two alternatives.

OpenRouter chat tasks 1-11 are complete; their evaluation tasks are separate.
Do not set `OPENROUTER_CHAT_MODEL` to `typesafe/jev-1.13`. No task in this
checklist authorizes paid inference or enabling the feature. Use fake HTTP
handlers/model responses in default tests. Do not log or commit credentials;
deployment examples and automated checks use fake secrets.

Each implementation task includes focused tests in the same increment.
Update its checkbox and completion note with the commit and any remaining
blocker before stopping. Leave unfinished tasks unchecked; do not mark a
feature complete merely because its configuration or specification exists.

### Product prerequisites (complete)

- [x] **JEV-01. Persist aliases and support admin editing.**
  Product-owned normalized aliases, strict API validation, omitted-field
  preservation, explicit clearing, public metadata and admin UI editing.
  **Completed:** `9900479`.

- [x] **JEV-02. Expose the rich catalog and validate search categories.**
  Depends on JEV-01. Add `get_category_catalog`, immutable metadata snapshots
  and deterministic versions through `CategoryCatalogService`. Validate
  canonical categories before provider-cache reads; distinguish invalid,
  unknown and unavailable-catalog failures. Preserve legacy discovery and
  location-only searches.
  **Completed:** `4d8e94c`.

### Assistant integration (pending)

- [x] **JEV-03. Implement rich-catalog snapshot lifecycle.**
  Depends on JEV-02. In new modes, refresh `get_category_catalog` and atomically
  publish immutable IDs, metadata, version and last-successful-refresh time.
  Build schema and decision context from the same request-captured snapshot.
  Preserve legacy refresh behavior and adapt tool ownership/reconnect handling.
  Validate schema, unique IDs, field limits, JSON-string wrapping, the
  100-category ceiling and the default 12,000-character context limit.
  **Done when:** tests cover metadata-only changes, malformed/empty responses,
  last-good retention, five-minute expiry, refresh interval validation,
  degraded readiness, cancellation and replacement tools. Unusable snapshots
  block category searches, not unrelated tools.
  **Completed:** `75c0e3b`.

- [x] **JEV-04. Add the separate Jev decision client and opt-in settings.**
  Depends on JEV-02. Implement typed Decisions request/response contracts and
  an HTTP adapter, separate from chat clients. Use server-owned intent/category
  questions, live catalog criteria and explicit abstention options. Add
  validated semantic-provider/model/endpoint/deadline/policy settings; require
  the shared key when Jev is enabled even if chat uses Ollama. Keep legacy as
  the default, with no startup inference and no automatic HTTP retries.
  **Done when:** capturing-handler tests prove the Decisions endpoint/auth and
  wire shape, strict option/distribution validation and numeric boundaries,
  safe HTTP/error-envelope handling, cancellation and bounded deadlines.
  Missing enabled settings fail startup safely; the chat profile is unchanged.
  **Completed:** `643bf01`.

- [x] **JEV-05. Wire category-first orchestration and locked search arguments.**
  Depends on JEV-03 and JEV-04. Implement the typed resolution outcomes and
  semantic-provider selection behind `CategoryResolution:Mode`. Apply the
  category gate to native and inline-recovered search calls, including searches
  requested later in an initially non-search conversation. Lock accepted IDs;
  the chat model extracts only explicit filters and produces grounded prose.
  Extract a typed hard-constraint/soft-preference envelope; validate supported
  filters, hourly USD versus total budgets, ranges and location before search.
  Never turn soft preferences into exclusion filters or invent availability.
  Preserve clarification history and user filters across replies. If extraction
  runs concurrently, join both validated outcomes before tool execution.
  **Done when:** uncertain intent, clarification, no-match and invalid
  decisions search nothing; accepted decisions use only the captured catalog.
  Empty provider results do not broaden categories. Public response/auth
  contracts and unrelated booking flows remain unchanged.
  **Completed:** `c1850cb`.

- [x] **JEV-06. Add cross-boundary failure and response-integrity coverage.**
  Depends on JEV-05. Share the request-wide deadline and existing repair/round
  budget; allow at most one eligible malformed-decision repair, never auth,
  credit or infrastructure repair. Surface safe unavailability without silent
  provider/model fallback. Add metadata-only, bounded telemetry using existing
  facilities, not new exporters.
  **Done when:** deterministic integration tests cover failures, catalog
  updates/outages, restarts/reconnect, clarification and malicious metadata.
  Sources and mentions come only from tools executed for the current request,
  never decisions, refreshes or few-shot results. Logs contain no decision
  state, criteria, probability maps or raw provider bodies.
  **Completed:** `8b314f8`. CMD test execution passed: 93 focused tests and
  410 tests in the full assistant suite.

- [x] **JEV-07. Wire deployment variables and document rollback.**
  Depends on JEV-04 and JEV-05. Forward explicit Jev settings through Compose,
  document `.env.example` and deployment recipes, and keep the Decisions model
  separate from `OPENROUTER_CHAT_MODEL`. Support Jev with Ollama chat and with
  OpenRouter chat; account for both workloads sharing the server-side key.
  **Done when:** fake-secret deployment checks cover both topologies and
  disabled/misconfigured modes without containers making inference calls.
  Rollback restores legacy/provider settings without resetting data or
  removing credentials/infrastructure still needed by another caller.
  **Completed:** `708d3d5`. Explicit Compose forwarding, fake-secret
  topology/startup checks and rollback instructions. No live inference or
  operator approval.

### Evaluation and release (operator gates)

- [x] **JEV-08. Review metadata, prepare evaluation and freeze acceptance policy.**
  Fixture/metadata preparation depends on JEV-02; live evaluation depends on
  JEV-06 and JEV-07 plus explicit operator approval and a spend cap.
  Review descriptions/aliases and version at least 120 labeled prompts across
  the six specification groups, with separate tuning and held-out data.
  Pin model/catalog/policy and evaluate single-category Jev against the
  applicable legacy/catalog-grounded baselines; report accuracy, abstention,
  confusion pairs, latency and cost using the specification's release gates.
  **Done when:** reviewed thresholds and numeric tolerance are frozen and
  held-out results are recorded. Do not guess production confidence thresholds.
  If approval or a gate is missing, record the blocker and keep Jev disabled.
  **Completed (category-stage gates):** Fixture `339d6a3`; catalog v2 with
  reviewed descriptions and aliases `8f06e7f`; clarified intent question
  `077cac0`; fresh operator-reviewed held-out cases `c841df6`; frozen policy
  `evaluation/jev/policy.v3.json` (`1b512d3`: confidence 0, probability 0,
  margin 0.65, tolerance 0.001). Held-out with the frozen policy: 111/111
  correct automatic, 60/60 correct abstention, 0/60 forced, 9/9
  NotApplicable; Jev call p50 179 ms, p95 242 ms. Results `809c9bd`; history
  of the failed v1 tuning and v2 held-out runs is in `evaluation/jev/README.md`.
  Not measured: end-to-end latency and the legacy-baseline comparison; cover
  them in JEV-09. Jev stays disabled until the operator approves release.

- [ ] **JEV-09. Verify and release the single-category end-to-end flow.**
  Depends on JEV-06, JEV-07 and JEV-08. Add deterministic end-to-end coverage
  for metadata edits affecting new decisions, ambiguity searching nothing,
  preserved filters and grounded provider responses. Stub model network calls.
  Deploy product capabilities first; enable the assistant only with separate
  operator approval after the gates pass.
  **Done when:** the approved flow and rollback are verified and documented.
  Multi-category support stays disabled until the separate MC gates pass.
  **Deterministic verification complete (October 10, 2026):**
  `CategoryFirstIntegrationTests` now covers a metadata-only catalog refresh
  changing the next Jev decision and locked search without restart or enum
  rebuild. Existing cases cover ambiguity searching nothing, clarification
  history preserving hard filters, and responses/sources/mentions grounded
  only in the current search result. All 415 AI assistant tests pass; the v2
  120-case fixture validates; base and OpenRouter Compose rendering plus
  `scripts\Test-AiAssistantDeployments.ps1` pass, including rollback checks.
  **Live comparison completed (October 10, 2026):** 60 fresh held-out cases,
  three repeats each, with the frozen v2 catalog/policy, `qwen3:8b`, identical
  provider fixtures and private raw results. Jev passed automatic resolution
  (108/111, 97.3%), abstention (60/60), forced-selection (0/60), grounding
  (180/180) and no-search safety (zero violations). Its p50/p95 was
  1.658/2.424 seconds versus legacy 1.248/1.689 seconds. The p95 ratio was
  **1.435x**, failing the required maximum of 1.2x. The candidate's measured
  Jev cost was $0.051165; total key usage including the discarded pre-fix run
  was $0.102899, within the approved $0.50 ceiling.
  **Release blocker:** reduce and remeasure warm end-to-end p95 without
  weakening quality/safety gates. No production opt-in was performed;
  `legacy` remains the default.

## Planned extension: up to three categories

These tasks are separate from the first single-category Jev release. The
current Jev contract selects one category; probabilities and clarification
candidates are not automatic permission to search three categories.

- [ ] **MC-01. Align the Jev decision specification with bounded alternatives.**
  Depends on JEV-02; finalize before implementing alternative selection.
  Reconcile the Jev single-choice contract with the multi-category amendment:
  one primary, at most two distinct alternatives, explicit abstention and an
  evaluated acceptance policy. Define how Jev responses authorize alternatives
  without blindly taking the top three probabilities or conflating independent
  questions. Preserve the default single-category behavior.
  **Done when:** the source specifications agree on decision shape, selection
  policy, search authorization, bounds and evaluation labels.

- [ ] **MC-02. Implement grouped multi-category MCP search in product-service.**
  Depends on JEV-02 and MC-01. Extend the existing search tool with validated
  alternatives, authoritative checks for every ID, fair allocation,
  cross-group deduplication and at most 10 providers combined. Preserve the
  byte-compatible single-category success shape.
  **Done when:** focused tests cover every validation error, empty/uneven
  groups, 4/3/3 allocation, deduplication and grouped response metadata.

- [ ] **MC-03. Wire bounded assistant alternatives, schemas and mentions.**
  Depends on JEV-05, MC-01 and MC-02. Add
  `CategorySearch:MaxCategories` (1-3, default 1), atomic primary/items enums,
  validated Jev alternatives, native/inline search gates and grouped mentions.
  Never silently truncate or allow the chat model to replace locked choices.
  **Done when:** tests cover modes 1 and 3, array-valued primary rejection,
  duplicate/excess alternatives, grounded filters, grouped results and
  request-local sources/mentions. Mode 1 retains its existing behavior.

- [ ] **MC-04. Evaluate, verify end-to-end and gate multi-category enablement.**
  Depends on JEV-09 and MC-03. Update examples, add a deterministic grouped
  Playwright scenario with stubbed AI calls, and apply the multi-category
  specification's quality/latency gates. Paid evaluation and enabling require
  explicit operator approval, independently of the single-category release.
  **Done when:** recorded results meet the gates and rollback to
  `CategorySearch:MaxCategories=1` is verified. No data reset is required.

## Later optional hybrid fast path

- [ ] **EXACT-01. Add product-owned exact resolution and the assistant branch.**
  Depends on JEV-03 and JEV-05; not a prerequisite for the first Jev release.
  Explicitly register `resolve_category_exact`, reuse `CategoryCatalogService`
  for versions, and implement entire-input matching, collision clarification,
  version reconciliation and bounded semantic fallback behind `hybrid`.
  **Done when:** deterministic correctness/performance tests and separate
  three-mode evaluation satisfy the source specification before enablement.

## Implementation before original Phase 1

- `ProductCacheService.getCategories()` derives categories from existing
  TaskMaster profiles rather than an independent catalog.
- `TaskMasterController` exposes `GET /products/categories`; MCP
  `get_categories` reads the same cache service.
- `ApplyForTaskMaster` and `NewTaskMaster` accept free-text category tags.
- `ApplicationController` copies submitted categories into the application and
  later into an approved profile. Catalog validation must cover both steps.
- `page-header` contains a hard-coded service list that can drift from the catalog.

## Original Phase 1: Admin-owned catalog

Tasks are ordered by dependency. Each checkbox is an implementation work item.

- [x] **1. Finalize the catalog contract.** Confirm collection/field naming,
  canonical ID format, duplicate handling, editable metadata, and response/error
  shapes. Preserve the existing category-ID arrays and exact-category search.
  Record the phase boundary and revised rollout order in the source spec.

- [x] **2. Add category persistence and shared domain logic.** Create the
  product-service model, repository, and service. Centralize catalog reads,
  normalization, duplicate checks, and validation of selected IDs. A category
  must exist independently of whether any TaskMaster currently uses it.

- [x] **3. Prepare a clean-reset catalog bootstrap.** Because current product
  data is disposable, reset only the `products` logical database instead of
  converting legacy category strings. Maintain an explicit, version-controlled
  canonical catalog, seed it before mock TaskMasters, and validate every mock
  category reference. Refuse startup when legacy profiles/applications exist
  without a catalog. Document the destructive reset scope and ensure the reset
  recreates the products database user without affecting other service data.

- [x] **4. Add admin category management APIs.** Support listing, creating, and
  editing category metadata through product-service. Keep IDs immutable and
  use explicit DTOs so clients cannot set audit fields. Enforce `ROLE_ADMIN`
  server-side using trusted JWT authorities, not a username or UI-only guard.
  Return clear validation, conflict, unauthenticated, and forbidden responses.
  Keep deletion, merging, and retirement out of this phase.

- [x] **5. Switch category discovery to the catalog.** Make
  `GET /products/categories` and MCP `get_categories` return canonical IDs from
  the same catalog, preserving their current string-array response shapes.
  Add a separate metadata response for selection/admin UIs needing display names
  and descriptions. Invalidate category caches after successful catalog writes;
  ensure old profile-derived Redis entries cannot survive the cutover. A newly
  created category must appear even when it has zero providers.

- [x] **6. Enforce selection-only writes on the backend.** Validate nonempty
  category selections against the catalog during application submission, direct
  TaskMaster creation, and application acceptance. Revalidate pending
  applications before publishing their profiles. Normalize/deduplicate IDs under
  the agreed contract and reject unknown values with actionable errors. Audit
  other profile writers/imports so they cannot bypass the rule. Neither applicant
  nor admin profile creation may implicitly create catalog entries.

- [x] **7. Add the admin category management screen.** Provide category list,
  create, and metadata-edit views with loading, empty, duplicate, and failure
  states. Add an admin navigation entry and connect frontend API methods through
  the existing `/products` BFF routing. Reuse authenticated bearer-token helpers;
  backend authorization remains authoritative.

- [x] **8. Replace free-text category inputs.** Use a shared catalog-backed
  multi-select in both `ApplyForTaskMaster` and `NewTaskMaster`. Display readable
  names but submit canonical IDs. Handle loading failures and an empty catalog
  explicitly; prevent submission without a valid selection and never fall back
  to free text. No "Category not listed" submission path exists until the
  deferred missing-category proposal feature is implemented.

- [x] **9. Align category consumers.** Use catalog metadata where category
  labels are shown in application review and profile flows. Replace the
  hard-coded header service list with catalog-backed entries without expanding
  unrelated navigation behavior. Preserve existing ID-based search URLs,
  request parameters, and stored references.

- [x] **10. Add focused regression coverage.** Cover catalog persistence,
  normalized ID collisions, admin-only writes, unknown/empty selections,
  acceptance-time validation, reset/bootstrap repeatability, and cache refresh
  after changes.
  Add React coverage for catalog selection and admin management. Preserve MCP
  response compatibility and single-category search behavior. Add an E2E
  scenario: admin creates a category, applicant selects it, admin approves the
  application, and the resulting profile retains the canonical ID. Include API
  attempts to submit arbitrary categories without using the UI.

- [x] **11. Roll out safely and document operations.** Approve and load the
  initial catalog, reset the disposable products database, then enable backend
  enforcement and selection-only forms in a coordinated release. Do not retain
  legacy product data under this rollout. Update seed data/setup documentation
  and explain how administrators create categories. Clear/version the category
  cache during deployment.

### Original Phase 1 completion criteria

Admins can create and maintain the database-backed category list. Applicants
and admin-created profiles select only catalog entries, with the same rules
enforced by the backend. Existing profiles and pending applications have an
explicit reset outcome. REST, MCP category discovery, and forms share one source
of truth. AI selection and search behavior remain unchanged.

## Superseded original future work

The original Phase 2/3 checklists are superseded, not completed. See the source
specification for the proposed hybrid phases, acceptance criteria, and the
separate deferred missing-category proposal requirements.

The active JEV checklist replaces those old checklists. Multi-category search
is tracked separately above, not silently enabled by Jev integration.
Missing-category proposals, object/action scoring and a broad rules engine
remain outside this plan.
