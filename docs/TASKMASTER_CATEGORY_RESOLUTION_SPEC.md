# TaskMaster Category Catalog and Hybrid Resolution Specification

> Status: **Product catalog, aliases, rich MCP catalog and search-category validation implemented; assistant snapshots, hybrid resolution and Jev integration proposed, not implemented**
>
> This is the authoritative category design specification. It consolidates the
> implemented catalog contract and the proposed hybrid AI matching design.
> The hybrid rollout has its own phases, independent of the original catalog
> project's phase numbering.
>
> Jev amendment (September 30, 2026): category-first decisions may use
> TypeSafe Jev through OpenRouter's Decisions API. This integration is
> **proposed, not implemented**, and is separate from the implemented
> OpenRouter chat adapter.
>
> Superseded proposals: interim multi-category search, object/action scoring,
> related-category expansion, and replacing LLM interpretation with a general
> deterministic resolver. Missing-category proposals remain a separate deferred
> feature, not a prerequisite for improving AI matching.
>
> Amended (proposed): [multi-category search](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md)
> reintroduces bounded search across at most three categories and 10 total
> results. Its section 5 overrides this document's single-category,
> ambiguity, and multiple-service rules; everything else here still applies.

## 1. Problem and decision

The assistant can choose a valid category that does not fit the requested work.
For example, repairing a wooden cupboard and assembling flat-pack furniture
require different skills even though both requests mention furniture.

Keep the live product-owned catalog. Add descriptions and curated aliases to
the assistant's category context, allow clarification rather than forcing a
match, and validate every search category in product-service. Then introduce a
conservative deterministic fast path for exact, unambiguous requests, retaining
semantic interpretation for other wording, using a chat model or the proposed
Jev decision engine described in section 5.

Catalog discovery and intent interpretation solve different problems. Rules
do not replace the catalog, and an enum does not prove semantic correctness.

### Goals

- Reduce wrong category selections without hiding uncertainty.
- Preserve exact, single-category searches and existing provider contracts.
- Keep taxonomy, aliases, and deterministic matching in product-service.
- Make catalog changes available without prompt edits or service restarts.
- Measure matching quality, forced matches, clarification, and latency.

### Non-goals

- Embeddings, vector databases, fuzzy spelling correction, or a larger model.
- A general keyword/rules engine, object/action scoring, or related-category
  inference.
- Searching several categories automatically (except the bounded grouped
  search in the [multi-category spec](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md))
  or dropping a category filter when a search returns no providers.
- Category deletion, merging, retirement, or new profile attribute schemas.
- Implementing missing-category proposals in this rollout.
- Changing booking, payment, provider ranking, or search-result limits.
- Resetting databases or replacing existing canonical category IDs.

## 2. Current implementation and preserved contracts

The following behavior exists today; it is not work newly introduced by this
proposal.

### Catalog ownership

`product-service` owns MongoDB `categories`. Categories exist independently of
providers. `TaskMaster.jobCategories` and application `jobCategories` remain
arrays of canonical IDs; aliases are never stored as profile category values.

`Category` contains `id`, `displayName`, `normalizedDisplayName`, `description`, `aliases`,
and server-managed creation/update timestamps and actors.

- IDs are immutable after creation. Input is trimmed and lowercased, must be
  2-64 characters, and must match `^[a-z0-9]+(?:-[a-z0-9]+)*$`.
- Display names are 2-80 characters after trimming and collapsing whitespace.
  Their lowercase normalized values are unique.
- Descriptions are required, trimmed, and 1-500 characters.
- ID and normalized display-name collisions return `409`.
- Internal normalization and audit fields are not writable through DTOs or
  exposed in public metadata.
- Application submission, direct profile creation, and application acceptance
  validate category IDs against the catalog.

### Existing HTTP and MCP interfaces

| Interface | Current contract |
|---|---|
| `GET /products/categories` | Sorted canonical ID array |
| `GET /products/categories/metadata` | Public array of `{id, displayName, description, aliases}` |
| `GET /products/admin/categories` | Administrator catalog listing |
| `POST /products/admin/categories` | Create with `id`, `displayName`, `description`, optional `aliases`; return `201` |
| `PUT /products/admin/categories/{id}` | Update display name, description and optional `aliases`; omission preserves aliases, `[]` clears; return `200` |
| MCP `get_categories` | Canonical ID array, unchanged for compatibility |
| MCP `get_category_catalog` | Versioned rich catalog with IDs, names, descriptions and aliases; no arguments |
| MCP `search_task_masters` | Optional `category` (omit for location-only searches); optional location/rate/rating filters; requires a category or location; at most 10 providers |

Admin APIs require a valid JWT with `ROLE_ADMIN`; UI visibility is not
authorization. Preserve current `400`, `401`, `403`, `404`, and `409` semantics
and the error envelope:

```json
{
  "error": "invalid_category",
  "message": "A human-readable validation message.",
  "fieldErrors": {
    "id": "A field-specific explanation."
  }
}
```

`fieldErrors` is optional. Existing category errors include `invalid_category`,
`unknown_category`, `duplicate_category_id`, and
`duplicate_category_display_name`. Catalog storage failures return
`503 category_catalog_unavailable` on HTTP and an `{error, message}` tool
payload on MCP; they are not successful empty catalogs or provider results.

### Current AI behavior and gaps

- `McpCategoryEnumRefresher` calls `get_categories` and applies IDs to the
  search tool schema, at startup and on the configured refresh interval
  (currently 60 seconds), rather than necessarily on every chat request.
- The refresher retains the previous list on failures or unusable results.
  Discovery reconnects after failed calls or unavailable tools; an empty
  response alone does not trigger reconnect.
- `McpRemoteTool` supplies the enum to Ollama. Neither a schema enum nor the
  registry's scalar normalization is authoritative category validation.
- `TaskMasterMcpTools` rejects a search with neither category nor location.
  Supplied categories are normalized and checked against the live catalog by
  `CategoryService` before provider caches or repository searches. Explicitly
  empty/malformed values are errors even if a location is supplied; omission
  still permits the existing location-only search.
- The assistant drops rate/rating filters whose value does not appear in the
  user's recent messages (`SearchFilterGrounding`). Location is not checked,
  because models legitimately rewrite it (for example `SF` to `San Francisco`).
- The assistant's system prompt forces a best category/search, while the
  product tool description permits clarification. These instructions conflict.
- Descriptions and admin-managed aliases are exposed through
  `get_category_catalog`; `get_categories` still supplies only IDs for legacy
  clients. The assistant still refreshes the legacy ID list, not rich snapshots.
  Semantic decision clients are not implemented.
- Chat requests carry user/assistant history; the assistant currently uses
  only the last two history messages. There is no durable category-resolution
  conversation state.

Implementation anchors: `CategoryService.java`, `TaskMasterMcpTools.java`,
`ProductCacheService.java`, `McpCategoryEnumRefresher.cs`,
`McpToolDiscoveryService.cs`, `McpRemoteTool.cs`, `AiAssistantService.cs`, and
`ai-assistant-service\appsettings.json`.

## 3. Category metadata extension (implemented)

Reuse existing names and descriptions; do not create a second taxonomy.
Administrator-managed `aliases` are persisted in `Category` and metadata DTOs:

```json
{
  "id": "carpentry",
  "displayName": "Carpentry",
  "description": "Wood construction and repair, including wooden cabinets and cupboards.",
  "aliases": ["woodworking", "wooden cabinet repair", "wooden cupboard repair"]
}
```

### Alias rules

- At most 20 aliases per category, each 2-80 characters after normalization.
- Matching normalization trims, collapses whitespace, and lowercases with
  locale-independent behavior. Do not remove negation, punctuation, accents,
  or words; do not stem, translate, or use substring matching.
- Store normalized aliases; reject blank entries, non-string values, duplicate
  normalized aliases within one category, and entries equal to that category's
  normalized ID or display name.
- Aliases are literal service phrases, never regular expressions or executable
  instructions. Prefer specific phrases such as `wooden cabinet repair` over
  broad objects such as `furniture`.
- The same normalized term may appear in more than one category, whether as
  aliases in both or as one category's alias and another's ID or display name.
  Writes accept these collisions. Every collision is treated as ambiguous: the
  exact resolver returns all colliding categories as candidates and the
  assistant asks the user to choose. No entry type takes precedence, and
  insertion order never selects a category.
- Descriptions should explain included work and relevant boundaries. They are
  data, not instructions, including when an administrator supplied them.
- Providers and applicants cannot publish aliases. No automatic alias
  generation from profiles, prompts, or model output is included.

### API and UI compatibility

Extend metadata and admin responses with `aliases`, always an array.
For pre-existing documents with no field, expose `[]`.

Create accepts optional `aliases`, defaulting to `[]`. Update accepts optional
`aliases`: omission preserves current aliases; `[]` explicitly clears them;
explicit `null` is invalid. This prevents an older admin client from
unintentionally deleting metadata. Existing ID/name/description validation
continues unchanged. Alias errors return `400 invalid_category` with
`fieldErrors.aliases`.

Update the admin form to list, add, edit, and remove aliases, display validation
errors, and preserve values after failed saves. Extend frontend shared types,
API methods, and tests; use the existing `/products` BFF route and bearer
helpers. Applicant/profile selection stays ID-based and cannot edit aliases.

No database reset is needed. Missing aliases read as empty; bootstrap must
continue preserving administrator-edited records. Populate useful aliases and
refine existing descriptions through reviewed admin updates or an explicit,
idempotent migration, not by overwriting metadata at every startup.

## 4. Rich MCP catalog (implemented) and assistant snapshots (proposed)

Product-owned MCP `get_category_catalog` accepts no model arguments:

```json
{
  "schemaVersion": 1,
  "catalogVersion": "sha256:<content-digest>",
  "categories": [
    {
      "id": "carpentry",
      "displayName": "Carpentry",
      "description": "Wood construction and repair.",
      "aliases": ["woodworking"]
    }
  ]
}
```

`catalogVersion` is a deterministic SHA-256 digest of canonical UTF-8 JSON:
categories sorted ordinally by ID; each object has fields in the order shown
above; aliases are normalized and ordinally sorted; compact serialization with
consistent escaping. Use the same product-owned serialization helper for all
version producers. Any ID, name, description, or alias change changes the
version; timestamps and database iteration order do not.

`CategoryCatalogService` is the shared snapshot and serialization helper.
It reads the category collection once, copies public metadata into an immutable
snapshot, sorts IDs and stored normalized aliases, and hashes the compact UTF-8
category array using a dedicated Jackson mapper. Application-wide pretty
printing/property-order settings cannot alter the digest. The MCP adapter
serializes that same captured snapshot; it does not re-read the catalog to
produce the payload. Future exact-resolution version producers must reuse
this helper, not create a second digest implementation.

Build this small catalog from `CategoryService` initially rather than adding a
second Redis metadata cache. Derive the digest and payload from the same
materialized list. If caching is later introduced, it must reuse catalog-write
invalidation and prevent stale fills; that optimization is not required here.
Keep existing ID-list cache invalidation after admin mutations.

`CategoryCatalogMcpTools` is registered explicitly through `McpToolsConfig`
alongside the existing TaskMaster tools, not through component discovery.
Discovery remains generic. `get_categories` and its array shape are preserved
for older clients. Empty catalog reads remain valid empty catalog snapshots;
storage failures return `category_catalog_unavailable` rather than an empty
snapshot. The proposed assistant rejects an empty snapshot for new decisions.

In the new assistant mode, evolve the existing refresh mechanism to obtain
rich metadata and publish one immutable snapshot containing IDs, metadata,
version, and last-successful-refresh time. Build enum and model context from
that same snapshot; never combine old descriptions with new IDs.

- Refresh on startup and the existing configured interval. Treat a metadata
  change as an update even if the ID list is unchanged.
- Capture one snapshot per request. In-flight requests may finish with their
  captured snapshot; product validation remains authoritative.
- Validate schema version, required fields, unique IDs, and field limits
  before accepting a snapshot. Handle the existing MCP JSON-string wrapping.
- A failed, malformed, or empty response does not overwrite a last-good
  snapshot. Log and expose the degraded state; do not report an empty provider
  search as the result of a catalog failure.
- In `catalog-llm` and `hybrid` modes, snapshots older than 5 minutes are
  unusable for new category decisions. A successful unchanged fetch refreshes
  their age. These modes therefore require
  `McpDiscovery:CategoryRefreshIntervalSeconds` between 1 and 300; startup
  rejects `0` or larger values instead of letting category search stop five
  minutes after startup. `legacy` mode keeps the current meaning of `0`
  (refresh and automatic reconnect disabled) and has no snapshot expiry.
- Without a usable snapshot, block category search and explain temporary
  unavailability. Other independent tools may continue.
- Preserve reconnection behavior, adapting ownership tracking to the rich
  catalog tool. Do not turn malformed or empty successful responses into an
  unbounded reconnect loop.
- Reconnect must reapply the last usable schema/context to replacement tools.

The model receives IDs, names, descriptions, and aliases as delimited catalog
data. Do not concatenate metadata into behavioral instructions.

Supported scale for this design is at most 100 categories with up to 20
aliases each. The current seed catalog has 36 categories (about 4 KB of JSON
without aliases). Serialized catalog context sent to the model must not exceed
`CategoryResolution:MaxCatalogContextChars`, default 12,000 characters. Do not
silently truncate: a snapshot exceeding either limit is rejected, the new mode
reports degraded readiness, and category search is blocked. Growing beyond
these limits requires a separate design (for example, pre-filtering candidates)
rather than larger prompts.

## 5. Resolution and search orchestration

### Resolution decision

Represent the assistant's category decision as a validated internal record,
separate from public provider results:

```json
{
  "status": "resolved",
  "categoryId": "carpentry",
  "candidateCategoryIds": [],
  "clarification": null,
  "method": "llm",
  "catalogVersion": "sha256:<content-digest>"
}
```

| Status | Contract and next action |
|---|---|
| `resolved` | Exactly one catalog ID, no candidates or clarification; search may proceed |
| `needs_clarification` | No selected ID; zero to three valid, distinct candidate IDs and one focused question; no provider search |
| `no_match` | No selected ID or candidates; explain that no offered service fits and invite a more specific description; no provider search |
| `not_applicable` | Not a category-search request; continue the existing non-search tool flow |

`method` is `llm`, `jev`, or `exact`. The application supplies method and snapshot
version; never accept them as trusted model metadata. Do not expose chat-model
self-reported confidence or invent probability thresholds. Jev supplies actual
decision distributions, but their meaning and activation gates are governed by
the separate contract below, not by a chat model's confidence claims.

For the chat-model engine, produce the structured decision in the initial model turn used for category
routing, rather than requiring both a separate classifier call and another
model call to select the same category. A resolved decision may also carry the
existing optional search filters in a typed orchestration envelope. Those
filters remain separate from category resolution and may only reflect values
explicitly supplied by the user. Then execute the product search and use the
existing tool-result-grounded final answer generation.

This is an internal orchestration change, not a new model-executable domain
tool or a change to the public `ChatResponse` shape. Use typed parsing and
validation; extending the Ollama client for structured output, if needed,
belongs in its existing contract/client layer. Native and inline-recovered
tool calls must not bypass the same category-search gate. If a
`not_applicable` flow later requests provider search, obtain a valid category
decision before executing it.

### Jev category-first integration (proposed)

Use Jev only for the narrow category decision. Keep the existing configured
`chat` model for conversation, explicit filter extraction and final answers.
Jev is made by TypeSafe and reached through OpenRouter; it is not an OpenAI
chat model and does not generate prose or native assistant tool calls.

This is an alternative semantic engine within this feature, not a new
model-executable domain tool, public endpoint, browser-selected model, or
replacement taxonomy. Keep the rollout mode names in section 8 for compatibility;
`catalog-llm` denotes the semantic stage even when that stage uses Jev.

#### Separate protocol boundary

Implement a dedicated internal decision-client contract and HTTP adapter.
Do not pass Jev through `IChatModelClient`, `ITaskChatModelClient`, or
`OpenRouterChatModelClient`: those contracts expect chat messages and tool calls.
Share credential/options helpers where appropriate, not incompatible DTOs.

Use plain HTTP with the Decisions API, not the TypeSafe SDK/System One surface:

```text
POST https://openrouter.ai/api/alpha/decisions
Authorization: Bearer <server-side OpenRouter key>
Content-Type: application/json
```

The request contains `model`, `state`, and named `questions`. Unlike chat
completions, it does not contain `messages`, `tools`, `tool_choice`, `think`,
or `max_tokens`. Do not derive this endpoint by appending a path to the chat
base URL ending in `/api/v1`.

Build `state` from the complete current user request, relevant user-authored
clarification history, and one immutable rich catalog snapshot from section 4.
Exclude few-shot results, provider records, credentials, actor IDs, authorization
scopes, and other execution metadata. Delimit catalog descriptions/aliases as
untrusted data; neither metadata nor user text can override the fixed questions.

Ask two independent Choice questions in one request:

- `intent`: distinguish a marketplace service-search request from
  `not_applicable` conversation/booking requests.
- `category`: choose among current canonical category IDs, plus explicit
  abstention outcomes `decision:needs_clarification` and `decision:no_match`.
  Colons cannot occur in canonical IDs, so these outcomes cannot collide with
  catalog entries. Criteria for each real ID include its current display name,
  description and reviewed aliases, not a hardcoded alias map.

Each question has `type: "choice"`, fixed server-owned `instructions`, and
an object-valued `criteria` map. Questions are answered independently; do not
write a category question that assumes it can inspect the intent answer.
Interpret both answers in application code after validating the response.

The response uses `answers.<question>` with `type`, `choice`, `confidence`,
and `probabilities`, plus the resolved `model` and token/cost `usage`.
For example, a category answer may select `carpentry` and contain a probability
for every option supplied in that request. These are typed decisions, not
`choices[].message` or stringified chat-tool arguments.

Require the expected question names and answer types; reject missing,
unexpected, duplicate or unknown options, nonfinite/out-of-range numbers,
incomplete distributions, and inconsistent selected options. Probability sums
must be valid within a documented numeric tolerance tested at the boundary.
An invalid decision must never reach provider search. Requested model stays
configuration-owned; a dated resolved model is diagnostic metadata only.

#### Application-owned interpretation and search gate

For a validated `not_applicable` intent, continue the existing independent
tool flow. If a later chat tool call requests marketplace search, apply the
same category gate before execution. An uncertain intent must not authorize
search.

For service-search intent, accept a real category only when it is in the
captured snapshot and passes the reviewed confidence/probability/margin policy.
Jev Choice confidence describes the distribution of supplied alternatives; it
is not proof of semantic correctness or permission to perform an action.
Tune and freeze thresholds against labeled tuning data, then report held-out
results. No guessed production threshold is prescribed by this amendment.

An explicit `decision:no_match` passing its reviewed acceptance gate yields
`no_match`. An explicit clarification outcome, insufficient certainty, multiple
requested services under the single-category policy, or conflicting relevant
context yields `needs_clarification`; execute no provider search. Candidate
IDs are validated snapshot IDs, bounded by the existing contract, never a
license to search every probable option. Jev returns no clarification prose:
generate one focused question using the chat model or reviewed application
templates, without provider claims.

Lock an accepted category for this request. The chat model may extract
explicit user filters and generate the grounded answer, but it must not
reclassify, broaden, substitute or omit that category. Both native and
inline-recovered search calls must pass the gate. Product-service still
validates the canonical ID before caches/search. An empty successful search
does not trigger another category decision.

In `hybrid` mode, a unique exact result skips Jev; an exact miss uses Jev when
selected as the semantic engine. Preserve exact-collision clarification and
the existing snapshot-version reconciliation rules. Proposed multi-category
search is a separate amendment; do not interpret Jev probabilities as automatic
OR searches or implement that feature merely to add Jev.

#### Configuration, failures and deployment

Proposed application settings, **not currently wired**:

| Setting | Required behavior |
|---|---|
| `CategoryResolution:SemanticProvider` | `ChatModel` or `Jev`; default `ChatModel`; independent of rollout mode |
| `CategoryResolution:Jev:Model` | Explicit decision-model slug, initially `typesafe/jev-1.13`; never taken from `OPENROUTER_CHAT_MODEL` |
| `CategoryResolution:Jev:Endpoint` | Explicit absolute HTTPS Decisions endpoint; production uses `https://openrouter.ai/api/alpha/decisions` |
| `CategoryResolution:Jev:TimeoutSeconds` | Positive per-decision budget within the request-wide chat deadline |
| Decision acceptance policy | Validated, versioned thresholds/tolerance and evaluated model/catalog combination required before enabling Jev mode |
| `OpenRouter:ApiKey` | Shared server-side secret; required for enabled Jev even when `chat` uses Ollama |

Keep `AiModels:Tasks:chat` unchanged. A Jev selection does not add a production
chat-task mapping or inherit chat output limits. Snapshot enabled settings at
startup; public input cannot change them. Legacy mode performs no Jev calls
and does not require Jev credentials.

When implemented, add explicit Compose environment forwarding for these
settings. A proposed deployment variable such as
`JEV_CATEGORY_MODEL=typesafe/jev-1.13` must map to
`CategoryResolution__Jev__Model`; merely putting it in `.env` has no effect.
Keep `OPENROUTER_CHAT_MODEL` reserved for a tool-capable conversational model.
Jev plus Ollama chat requires the base/mixed topology, not the OpenRouter-only
chat override. Jev plus OpenRouter chat can share the same credit-limited key;
its budget covers both workloads.

Do not make inference calls at startup. Missing enabled configuration fails
startup safely; invalid/revoked keys fail on requests. Validate non-2xx and
HTTP 200 error envelopes before interpreting answers. Handle the documented
Decisions statuses, including payload-too-large, edge timeout and provider
overload, rather than assuming chat status coverage is complete.
Keep automatic HTTP retries disabled in the initial Jev client. At most one
malformed-decision repair may consume the existing shared repair/tool-round
budget; authentication, policy, exhausted-credit and infrastructure failures
are not output-repair candidates. Never reset the end-to-end deadline or
silently fall back to another model/provider. Propagate caller cancellation.
Failure produces explicit safe unavailability and no provider search.

Reuse metadata-only telemetry conventions. Never log state, criteria,
probability maps, raw response/error bodies, credentials or user payloads.
Keep task/provider/model/outcome labels bounded; snapshot versions, resolved
models, generation IDs and optional numeric usage remain diagnostic metadata
as appropriate. Decisions do not become marketplace `sources` or mentions.

Rollback restores the semantic provider/mode and restarts the assistant while
leaving the chat profile unchanged. Retain the shared OpenRouter key and local
Ollama infrastructure whenever another enabled caller still requires them.

#### Implementation sequence and completion gate

1. Deliver the existing rich-catalog/alias and authoritative-validation
   prerequisites from hybrid phase 1; older clients remain compatible.
2. Add the typed Jev decision boundary, validated opt-in settings and capturing
   HTTP-handler tests for endpoint/auth, request shape, strict answer parsing,
   errors, cancellation and deadlines. No live calls in default CI.
3. Wire category-first orchestration, locked search arguments, abstention,
   clarification/history and metadata integrity behind the rollout switch.
4. Add explicit environment forwarding and deployment/rollback guidance;
   validate Jev with Ollama chat and with OpenRouter chat using fake secrets.
5. Run operator-approved, spend-bounded evaluation; freeze acceptance policy
   before enabling the feature. Approval of a chat smoke test is not approval
   of an additional Jev evaluation.

Until these steps are implemented and their gates pass, Jev end-to-end testing
is not supported. OpenRouter integration task 12 prepares evaluation of the
existing chat foundation, not implementation of these category prerequisites.

Implementation progress (September 30, 2026): alias persistence, strict API
validation, legacy omission versus clearing, public metadata arrays, admin
editing, deterministic rich MCP catalog snapshots and authoritative
search-category validation are implemented. Assistant rich-snapshot handling
and the Jev decision client/search gate remain unimplemented.

### Hybrid phase 1: catalog-grounded semantic interpretation

1. Capture a usable catalog snapshot and relevant conversation context.
2. Use the configured semantic engine to select one category only when the
   requested work supports it. Use descriptions and aliases as context, not
   hardcoded mapping examples; Jev follows its separate decision contract above.
3. Validate the decision's shape, selected ID, and candidate IDs in application
   code. Reject unknown IDs and contradictory outcomes.
4. For ambiguity, unsupported work, or multiple independent services, return
   clarification/no-match without searching. Do not pick the first candidate.
5. For a valid selection, execute `search_task_masters` with one canonical ID
   and only explicitly stated optional filters.
6. Ground the final answer exclusively in actual results.

Allow at most one repair attempt for malformed model output or an unknown ID,
within the existing overall tool-round budget. If repair fails, return a safe,
explicit inability to resolve the request and log the cause. Never substitute
the first category, a broad category, or an unfiltered search.

Align system prompt, tool descriptions, and few-shot examples: searching is
mandatory before making provider claims, not before asking a clarification.
Remove object-to-category special cases after reviewed catalog descriptions
and evaluation cases cover those distinctions. Examples must reference actual
catalog IDs and must not supply a second, hardcoded taxonomy.

### Hybrid phase 2: conservative exact-match fast path

Add product-owned MCP `resolve_category_exact(query)`:

```json
{
  "status": "resolved",
  "categoryId": "carpentry",
  "candidateCategoryIds": [],
  "catalogVersion": "sha256:<content-digest>"
}
```

The input is the complete current user message, not a model-extracted keyword.
Require a nonblank string of at most 2,000 characters. The chat API currently
has no message length limit, and this feature does not add one: the limit
applies only to resolver input. Longer chat messages skip this fast path and
use the hybrid phase 1 semantic path. Direct invalid calls return
`invalid_category_query`.
Resolver failures use the same `{error, message}` envelope as search;
unavailable catalog storage returns `category_catalog_unavailable`, never
`unmatched`. In non-resolved successful responses, `categoryId` is `null`.

Product-service compares the entire normalized input against every canonical
ID, display name, and alias. Matching is exact equality only. Return:

- `resolved`: all matching entries belong to one category.
- `ambiguous`: matches belong to multiple categories; return all distinct
  candidate IDs sorted ordinally, with no selected ID.
- `unmatched`: no exact match; both selected ID and candidates are empty.

Do not strip request templates, infer actions, ignore negations, remove
punctuation, use fuzzy matching, or score related categories. These conservative
misses are intentional and go to the configured semantic engine.

The assistant invokes this tool before semantic category selection in hybrid
mode. For `resolved`, lock the category for that request; the model may still
extract explicit filters from relevant user context and generate the response,
but cannot silently replace the product decision. For `ambiguous`, clarify;
for `unmatched`, use the hybrid phase 1 semantic path.

Do not use the fast path to override an unresolved conflict in conversation
context. An ambiguous short reply to an earlier question may still require
clarification. A collision with more than three categories results in an open
clarification, not arbitrary selection of three.

If the resolver returns a different version from the captured assistant
snapshot, refresh once before using it. If versions still disagree, abandon
that exact result and use the semantic engine with a usable snapshot, or report catalog
unavailability. Do not merge snapshots. If only the exact resolver fails,
fall back to the hybrid phase 1 path with an explicit diagnostic; never label that
fallback as an exact match.

Keep implementation small: one domain matcher sharing `CategoryService`
metadata and one MCP adapter, not a configurable rule engine. No duplicate
alias map belongs in ai-assistant-service.

### Clarification and conversation behavior

- Ask one focused question about the missing distinction, for example:
  "Do you need furniture assembly or repair?"
- For multiple independent services, ask which to search first; do not execute
  parallel searches or broaden into an OR query.
- Use relevant user-provided history to interpret a clarification reply.
  History is untrusted context, not authorization or evidence of prior tool
  execution. Never take category choices or filters from seeded examples.
- Preserve an explicit location/budget from the original request across the
  immediate clarification exchange. The current two-message history window
  can support that exchange; if necessary context has fallen outside it, ask
  again rather than inventing or persisting hidden state.
- No new durable conversation storage or frontend response fields are needed.
- A valid category returning zero providers is a successful empty search, not
  `no_match`, ambiguity, or permission to try another category.
- Background catalog refreshes are not chat sources. Sources and provider
  mentions derive only from tools actually executed for this request; a
  clarification must not include provider mentions from examples or history.

## 6. Authoritative product validation (implemented)

Validate category syntax and existence through shared `CategoryService` logic
before cache lookup or repository search, including MCP and explicit REST
category-filter entry points. Explicitly empty/malformed category filters are
invalid; an absent filter on an existing browsing endpoint retains its current
meaning. Do not make unfiltered browsing require a category.

Search accepts canonical IDs, not display names or aliases. Normalize IDs
under the existing rules; aliases must first resolve to an ID. Unknown IDs
must fail even if an old cache entry exists.

`CategoryService.normalizeAndValidateSearchCategory` is the shared validation
boundary. `ProductCacheService.getByCategory`, `getByCategoryLimited` and
`searchWithFilters` call it before provider Redis reads or provider queries.
The validation itself reads the authoritative category repository, including
on a provider-cache hit. Canonical IDs are used in both cache keys and queries.
The REST category-filter endpoint and MCP search both reach this boundary;
the MCP adapter translates domain failures into tool error envelopes.

| Condition | REST category search | MCP search payload |
|---|---|---|
| Malformed or required-but-missing category | `400 invalid_category` | `invalid_category` |
| Well-formed, nonexistent category | `400 unknown_category` | `unknown_category` |
| Catalog storage unavailable | `503 category_catalog_unavailable` | `category_catalog_unavailable` |
| Valid category, no providers | Existing success with empty results | `[]` |

MCP errors use `{error, message}` with optional field details, not an empty
array or a provider-shaped object. They are tool payloads, not claims about MCP
transport HTTP statuses. Translate expected domain exceptions explicitly and
log infrastructure failures through existing service mechanisms.

The following assistant repair behavior remains proposed: the assistant may
refresh and repair an unknown-category decision once; this
shares the repair budget in section 5, rather than adding another loop.
Cancellation propagates, and the existing round limit remains an upper bound.
Do not expose raw tool errors or internal names in the user-facing answer.

Do not trust a model-supplied snapshot version as authorization. Continue
passing immutable `ToolExecutionContext` separately from model arguments.
Search cache keys remain canonical ID plus existing normalized filters;
metadata-only edits do not change the provider query and need no new
taxonomy-versioned provider-result cache.

## 7. Observability and evaluation

Record resolution status, method, catalog version/age, duration, repair count,
and degraded/fallback reason using existing logging and metrics facilities.
Use bounded metric labels; versions, raw queries, user IDs, and category IDs
must not become unbounded metric dimensions. Do not add raw prompt logging or
new telemetry exporters for this feature.

Build a reviewed, versioned fixture of at least 120 prompts: at least 20 each
for explicit categories/aliases, paraphrases, overlapping categories,
negation/multiple services, unsupported requests, and clarification follow-ups.
Freeze the catalog and label acceptable IDs or required abstention before
comparing systems. Separate tuning examples from held-out evaluation cases.

| Example | Expected behavior |
|---|---|
| `carpentry` / `woodworking` | Exact carpentry when uniquely configured |
| `repair my wooden cupboard` | Semantic engine selects carpentry with appropriate metadata |
| `assemble an IKEA wardrobe` | Furniture assembly, not carpentry |
| `clean my upholstered sofa` | Matching cleaning category if offered; otherwise no-match/clarify, not furniture assembly |
| `help with furniture` | Clarification |
| `not assembly; repair my wooden table` | No substring fast path; interpret requested repair |
| `clean my house and fix a leaking tap` | Ask which service to search first |
| An alias shared by two categories | Clarify, never first-match selection |
| Unsupported service | No-match/clarification, no unrelated provider search |
| Known category with no providers | Honest empty results, no category substitution |
| Original request with explicit budget, followed by a clarification reply | Preserve that budget; do not invent other filters |

Initial release gates (targets, not measured claims):

- 100% of deterministic matcher and validation contract cases pass.
- 100% of invalid-ID, missing-catalog, and malformed-decision tests prevent
  provider searches.
- On held-out unambiguous requests, correct automatic resolution is at least
  90%; unnecessary clarification counts as a miss, preventing abstention from
  artificially inflating accuracy.
- On held-out requests requiring abstention, incorrect forced selection is at
  most 5%; correct abstention is at least 95%.
- Report confusion pairs, no-match rate, clarification rate, and fast-path
  coverage separately. Do not hide regressions in one aggregate score.
- Compare `legacy`, `catalog-llm`, and `hybrid` modes with the same chat model,
  catalog, hardware and prompts; run each prompt three times and report
  variability. Report the semantic engine separately. Jev comparisons pin the
  decision model and acceptance policy, include its additional cost/latency,
  and cannot be presented as a same-model-only comparison.
- Product exact-matcher execution p95 is at most 20 ms on a warm local fixture
  at the supported ceiling (100 categories with 20 aliases each), excluding
  network/Mongo fetch.
- Quality and end-to-end latency are measured with the current catalog plus
  reviewed aliases, and again with a synthetic catalog at the supported
  ceiling that stays within `CategoryResolution:MaxCatalogContextChars`.
- Warm end-to-end service-search p95 is at most 1.2 times baseline, measured
  from request receipt through final answer with identical provider data.
  Report catalog fetch, resolver round trip, and model time separately.
- If accuracy or latency gates fail, keep the new mode disabled and revise
  the implementation; do not claim improvement based only on valid IDs.

## 8. Phased implementation and rollout

Use one assistant rollout setting, `CategoryResolution:Mode`, with values
`legacy`, `catalog-llm`, and `hybrid`; default to `legacy` until gates pass.
Reject unknown configuration values at startup, and in `catalog-llm` or
`hybrid` mode reject a category refresh interval outside 1-300 seconds
(section 4). This is a migration switch,
not a separate taxonomy configuration system.

### Hybrid phase 1: metadata, ambiguity, and validation

1. **Implemented:** alias persistence/DTO validation and backward-compatible admin editing.
2. **Implemented:** rich catalog MCP tool and authoritative search validation.
   Existing discovery and search success shapes are preserved.
3. Review descriptions and aliases for overlapping services.
4. Add assistant snapshot handling, typed decisions, clarification behavior,
   and prompt/tool alignment behind `catalog-llm`.
5. Cover failure paths and evaluate `legacy` versus `catalog-llm`.

If Jev is selected, apply the Jev implementation sequence in section 5 after
the catalog prerequisites and before evaluating/enabling category-first behavior.
Do not mark the hybrid phase complete merely because the chat adapter exists.

Deploy product-service before enabling the new assistant mode. Deploy the
admin UI only after alias-capable product APIs are available. Older assistants
continue using `get_categories`. Enabling a new mode without its required
tools must report readiness/degraded status and block affected searches, not
silently pretend the feature is enabled.

### Hybrid phase 2: exact matching

1. Implement and register `resolve_category_exact` in product-service.
2. Add the assistant's exact-match branch, collision handling, and bounded
   fallback to the hybrid phase 1 path behind `hybrid`.
3. Evaluate all three modes and enable hybrid only after the release gates.
4. Verify metadata updates, provider-empty categories, clarification
   follow-ups, product restarts, and reconnection without restarting AI.

Rollback first disables hybrid to `catalog-llm`, or returns the assistant to
`legacy`. Keep authoritative product validation enabled in every mode.
Alias fields are additive; do not delete metadata or reset data on rollback.
Legacy assistant rollback restores the old matching behavior, not the old
unsafe unknown-category search behavior. Roll back assistant mode before
removing product capabilities.

## 9. Required implementation coverage

| Surface | Required coverage |
|---|---|
| Product category model/service/DTOs | Alias limits, normalization, omission versus clearing, existing documents, immutable IDs, admin authorization |
| Catalog/MCP registration | Legacy ID-array compatibility, rich schema, metadata-only version changes, deterministic serialization, explicit registration |
| Search service and controllers | Validation before caches, all category-filter entry points, absent versus empty filters, errors versus empty results |
| Exact resolver | Entire-input matching, punctuation/negation misses, collisions including alias/name collisions, input limits |
| Assistant refresher/discovery | Atomic snapshots, malformed/empty responses, expiry, cancellation, reconnect, replacement tools |
| Assistant orchestration/client | Typed decision validation, bounded repair, native/inline call gate, locked exact result, version mismatch, clarification history, explicit filters |
| Jev decision client and configuration | Separate Decisions endpoint/DTOs, exact question/option sets, distributions and threshold boundaries, error envelopes/statuses, any-enabled-caller key requirement, no silent fallback |
| Jev category-first flow | Locked category on every search path, uncertain/no-match decisions search nothing, unchanged chat profile/public model, no metadata leaks, disabled mode makes no paid calls |
| Frontend admin | Alias CRUD UI, omitted legacy fields, loading/save errors, no changes to applicant category semantics |
| Response integrity | No fabricated providers; no sources/mentions from background refresh or few-shot examples |
| End-to-end | Admin metadata update affects new decisions; unknown ID rejected; ambiguity searches nothing; clarification preserves user filters |

Use existing Maven, xUnit, Jest, and Playwright infrastructure. Stub Ollama for
deterministic CI behavior and capture/stub Jev/OpenRouter HTTP calls; run real-model quality/latency evaluation separately
with a recorded configuration. No new lint framework is required.

## 10. Separate deferred feature: missing-category proposals

This remains useful future work, but is not implemented or required here.
Applicants currently must choose existing categories.

A future proposal design should retain these requirements:

- Capture proposed name, service description, optional example terms, and the
  linked application.
- Track `PENDING`, `APPROVED_NEW_CATEGORY`, `MAPPED_TO_EXISTING`, or `REJECTED`,
  with reviewer, timestamp, resolved category ID, and applicant-visible reason.
- Permit administrators to create a category, map to an existing one, or
  reject with a reason. Proposed terms never automatically become aliases.
- Prevent publishing a profile until its required category decisions are
  resolved; store only canonical IDs in the resulting profile.
- Specify authorization, concurrent-review/idempotency behavior, audit,
  applicant notifications, and tests before implementation.

The previous optional one-to-three-category search and object/skill/related
category scoring are no longer scheduled requirements. Reintroducing them
would require a separate design and evidence that the conservative hybrid
approach is insufficient. Multi-category search now has that separate design
in [TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md),
gated on its own evaluation; object/skill/related-category scoring remains
superseded.

## Related documents

- [OpenRouter Jev documentation](https://openrouter.ai/docs/guides/community/jev),
  [tutorial](https://openrouter.ai/docs/guides/community/jev-tutorial), and
  [Decisions API reference](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-request):
  protocol reviewed September 30, 2026; recheck the alpha contract before coding.
- [OpenRouter integration](OPENROUTER_INTEGRATION_SPEC.md):
  implemented conversational-model foundation; Jev is not a chat-model slug.
- [Multi-category search](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md):
  proposed bounded grouped search that amends section 5's single-category rule.
- [Catalog completion record](TASKMASTER_CATEGORY_MANAGEMENT_TASKS.md):
  historical implementation checklist, not a competing future roadmap.
- [Catalog operations and original reset guide](TASKMASTER_CATEGORY_DATABASE_RESET.md):
  administration and explicitly destructive legacy-bootstrap procedures.
  Do not run the reset for this hybrid rollout.
