# TaskMaster Multi-Category Search Specification

> Status: **Proposed, not implemented**
>
> This document reintroduces bounded multi-category search as the separate
> design required by section 10 of the
> [category resolution specification](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md).
> Where the two conflict, this document is authoritative for how many
> categories one provider search may cover and how ambiguous requests with a
> small number of plausible categories are handled. Everything else in the
> resolution specification (catalog ownership, aliases, snapshots,
> authoritative validation, exact matching) still applies.

## 1. Problem and decision

The assistant must currently choose exactly one category per search. Many
requests plausibly belong to more than one category, and a single forced
choice can hide the providers the user actually needs. For example, "fix my
refrigerator" could reasonably match `home-repair` or `electrical` in the
current catalog; choosing only one returns an empty or wrong result set even
when suitable providers exist.

**Decision:** one provider search may cover **one primary category and up to
two alternative categories (at most three in total)**. Product-service runs
the search, returns results **grouped by category** with the primary category
first, and caps the combined result at **10 TaskMasters in total**, shared
fairly across the groups. The assistant presents the groups and invites the
user to narrow the search, instead of asking a clarifying question before
searching.

### Goals

- Improve the chance that at least one searched category contains providers
  suitable for the request.
- Keep the answer small and grounded: the same 10-TaskMaster ceiling as a
  single-category search.
- Keep the change backward compatible: single-category requests and response
  shapes are unchanged.
- Make the number of searched categories observable and bounded.
- Keep category validation authoritative in product-service.

### Non-goals

- Searching more than three categories, or searching every category when
  unsure.
- Dropping the category filter or broadening into an unfiltered search when a
  category returns no providers.
- Related-category inference, object/action scoring, embeddings, or any
  deterministic keyword-to-category rules engine.
- Changing product-service retrieval ranking, sort order, filter semantics, or
  the location normalization contract. The assistant preserves grouped search
  order and does not perform a post-retrieval Jev reranking pass.
- Changing the public `ChatResponse` shape or the frontend.
- New durable conversation state.

## 2. Current behavior (baseline)

- MCP `search_task_masters` accepts one optional `category` string plus
  optional `location`, `minRate`, `maxRate`, and `minRating`, and returns a
  JSON array of at most 10 TaskMasters (`TaskMasterMcpTools.MAX_RESULTS`).
- `ProductCacheService.searchWithFilters` caches per
  `canonical category + location + filters + limit` after validating supplied
  category IDs against the product-owned catalog, even on cache hits;
  `TaskMasterSearchRepositoryImpl.searchWithFilters` matches `jobCategories`
  exactly and applies `limit` with no explicit sort. Result order is therefore
  the repository's natural order, and this spec does not change it.
- The assistant exposes `category` to the model as a string with a live `enum`
  (`McpCategoryEnumRefresher` → `McpRemoteTool.SetParameterEnum`).
  `McpRemoteTool.NormalizeSchema` preserves only `type`, `description`, and
  `enum` for each property.
- `ToolRegistry.FlattenToString` flattens every argument to a string. A
  multi-element array is joined with commas. If the model sends
  `"category": ["a", "b"]`, product-service receives `"a,b"` and now rejects it
  with `invalid_category`; it no longer silently returns `[]`. The proposed
  assistant gate below still needs to reject this shape before MCP execution
  and explain the separate primary/alternative arguments.
- `AiAssistantService.ExtractMentions` reads a top-level array, an object's
  `taskMasters` array, or a single object with `id`, but not grouped results.
- The system prompt (`PromptOptions.SystemPrompt`) instructs the model to
  "pick the single best match".

## 3. Tool contract (product-service)

### 3.1 Parameters

Extend the existing MCP tool `search_task_masters`; do not add a second
search tool.

| Parameter | Type (MCP) | Rules |
|---|---|---|
| `category` | string, optional | Primary category ID. Unchanged meaning. Required whenever `alternativeCategories` is supplied. |
| `alternativeCategories` | string, optional | New. Comma-separated category IDs, at most 2 after normalization. |
| `location`, `minRate`, `maxRate`, `minRating` | unchanged | Applied identically to every searched category. |

`alternativeCategories` is a string rather than a list because the assistant
passes every tool argument as a string (section 4.2). Category IDs match
`^[a-z0-9]+(?:-[a-z0-9]+)*$` and can never contain a comma, so the encoding
is unambiguous.

The tool description must state that alternatives are optional, only for
requests that genuinely fit more than one category, ordered by decreasing
fit, and never used to pad the search.

### 3.2 Validation

Validate before any provider-cache lookup or provider repository query. Reuse the shared
`CategoryService` validation defined in section 6 of the resolution
specification; do not add a second validator.

1. Split `alternativeCategories` on commas, then trim and lowercase each
   entry, applying the ID normalization rules. Discard entries that are empty
   after trimming.
2. Remove any alternative equal to the primary, and any repeated alternative,
   keeping first-occurrence order.
3. Reject the call if:
   - alternatives are supplied without `category`: `invalid_category`;
   - more than 2 distinct alternatives remain: `too_many_categories`;
   - any ID is malformed: `invalid_category`;
   - any ID does not exist in the catalog: `unknown_category`. The message
     names the offending ID.

   Never silently drop an unknown alternative: the assistant needs the error
   to repair the call.
4. Reject a `category` value that contains a comma with `invalid_category`
   and a message telling the caller to use `alternativeCategories`. This
   preserves the current comma rejection and adds actionable guidance for the
   new alternative-category argument.

Errors use the existing MCP `{error, message}` envelope and are tool payloads,
not transport statuses. Catalog storage failures return
`category_catalog_unavailable`.

### 3.3 Execution and fair allocation

Let `C = [primary, alt1?, alt2?]` in the validated order.

1. For each category in `C`, run the existing
   `ProductCacheService.searchWithFilters(category, …, limit = 10)`. This
   reuses the existing per-category cache keys. Assembling the grouped
   response is not cached separately.
2. **Deduplicate:** a TaskMaster appearing in several searched categories is
   kept only in the earliest group in `C`, and is listed in that group's
   `alsoMatchedCategoryIds`.
3. **Allocate 10 slots round-robin:** take the next unused TaskMaster from
   each group in `C` order, repeating until 10 are selected or every group is
   exhausted. Within a group, keep the repository order. For example:
   - three full groups receive 4/3/3;
   - if one group has only 1 result, its unused slots go to the other groups;
   - a single group behaves exactly like today.
4. Keep every group in the response, including groups with zero selected
   TaskMasters, so the answer can honestly say a searched category was empty.

The queries may run sequentially; each one is an indexed query or a cache
hit. Parallel execution is an optional optimization, and only allowed if it
preserves the deterministic output above.

### 3.4 Response shape

A call **without** `alternativeCategories` returns today's plain JSON array,
byte-for-byte compatible.

A call **with** at least one valid alternative returns:

```json
{
  "searchedCategoryIds": ["home-repair", "electrical"],
  "groups": [
    {
      "categoryId": "home-repair",
      "displayName": "Home Repair",
      "taskMasters": [
        { "id": "tm-1", "name": "…", "location": "San Francisco, CA",
          "hourlyRateUsd": 45.0, "rating": 4.8,
          "alsoMatchedCategoryIds": ["electrical"] }
      ]
    },
    { "categoryId": "electrical", "displayName": "Electrical", "taskMasters": [] }
  ],
  "totalReturned": 1,
  "limit": 10
}
```

- Group order equals `searchedCategoryIds`, which equals the validated `C`
  order.
- TaskMaster objects keep their existing single-search field set.
  `alsoMatchedCategoryIds` appears only when the TaskMaster also matched a
  later searched category.
- `displayName` comes from the catalog. It is data, not an instruction.
- If every group is empty, return the object with empty groups, not `[]`.
  This is a successful empty search, not `no_match`.

### 3.5 Other entry points

REST browsing and search endpoints keep single-category semantics. Multi-
category search is an assistant capability exposed only through MCP.

## 4. Assistant behavior (ai-assistant-service)

### 4.1 Configuration and rollout switch

Add `CategorySearch:MaxCategories`:

- allowed values are `1` to `3`; startup rejects anything else;
- the default is `1`, which is exactly today's behavior;
- when the value is `1`, the assistant removes `alternativeCategories` from
  the schema offered to the model, and the tool gate (section 4.3) rejects the
  argument if it appears anyway;
- when the value is `N > 1`, the assistant allows at most `N - 1`
  alternatives.

This is the only multi-category rollout and rollback switch. Product-service
support is additive and stays deployed in every mode.

### 4.2 Schema offered to the model

Extend `McpRemoteTool` so that property overrides can replace the `type` and
add `items`. The refresher then sets, for `search_task_masters`:

```json
"alternativeCategories": {
  "type": "array",
  "items": { "type": "string", "enum": ["<live category IDs>"] },
  "maxItems": 2,
  "description": "Optional. Up to 2 other categories that also plausibly fit, best first. Omit when one category clearly fits."
}
```

The `category` enum and this `items.enum` come from the same category list
in the same refresh, and are swapped atomically under the existing schema
lock. `maxItems` follows `CategorySearch:MaxCategories - 1`.

`ToolRegistry.FlattenToString` already joins string arrays with commas. That
is the correct wire encoding for `alternativeCategories`, so no new
flattening path is needed.

### 4.3 Tool-call gate

Before executing `search_task_masters`, native and inline-recovered calls
alike, the assistant:

1. Rejects a `category` supplied as an array with more than one element. It
   returns a tool error asking for `category` plus `alternativeCategories`,
   instead of letting the value be comma-joined.
2. Removes alternatives equal to the primary and duplicate alternatives.
3. Rejects more alternatives than configured with a tool error. It never
   truncates silently.
4. Leaves `SearchFilterGrounding` unchanged: filters are grounded
   independently of categories.

A rejection costs one model round and follows the existing tool-error and
round-limit behavior. Rejections are not retried separately.

### 4.4 Prompt and examples

Replace "pick the single best match" in `PromptOptions.SystemPrompt` with
guidance equivalent to:

- Put the best-fitting category in `category`.
- Add `alternativeCategories` (at most 2) only when the request plausibly
  fits other categories, for example an object or problem that several trades
  handle. Do not add alternatives for clear requests, and never pad to reach
  the maximum.
- Keep the existing rules for location-only search and explicit filters.

Answering guidance for grouped results:

- Present results per category, primary first, using the category display
  name. Briefly state which searched categories returned no one.
- Use only TaskMasters present in the tool result. Do not claim a TaskMaster
  can do the specific job beyond their listed category.
- When more than one group has results, end with one short question that helps
  the user narrow the search (for example, "Is the problem electrical, such
  as the fridge not powering on, or mechanical?").
- For ranking questions ("cheapest", "highest rated"), compare across all
  returned groups and say the ranking covers the returned results only.

Add or adjust at most one few-shot example demonstrating a grouped call and
answer, using real catalog IDs and fictitious TaskMaster data. Few-shot
content must never become chat sources or mentions (existing rule).

### 4.5 Response integrity

- Extend `ExtractMentions` to read `groups[].taskMasters[]` in addition to
  the existing shapes. Deduplicate by ID as today.
- `sources` still lists `search_task_masters` once per request, however many
  categories it covered.
- `ChatResponse` shape is unchanged; the frontend needs no change.

### 4.6 Follow-up narrowing

The user's reply to the narrowing question is an ordinary new request. The
model may then issue a single-category search using relevant recent history,
within the existing two-message history window. Explicit filters from the
original request (for example "in San Francisco") may be reused only when
they are still inside that window; otherwise the model asks again. There is
no hidden state.

## 5. Changes to the hybrid resolution design

These amend the proposed, not-yet-implemented hybrid design in the
[resolution specification](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md):

| Resolution spec rule | Amended rule |
|---|---|
| `resolved` carries exactly one catalog ID | `resolved` carries a primary `categoryId` plus 0-2 `alternativeCategoryIds`, bounded by `CategorySearch:MaxCategories` |
| Ambiguity with candidates returns `needs_clarification` and searches nothing | 2-3 plausible candidates resolve to a grouped search. `needs_clarification` remains for more than 3 plausible candidates, unsupported or unclear requests, and when multi-category search is disabled |
| Multiple independent services: ask which to search first | If there are at most 3 services and each maps to one category, search them as one grouped call; otherwise clarify |
| Exact resolver `ambiguous` collision → clarify | A collision of 2-3 categories becomes a grouped search in the resolver's ordinal order, because an exact collision has no preferred primary; more than 3 categories → clarify. Never pick an arbitrary 3 |
| Non-goal: searching several categories automatically | Allowed only within this spec's 3-category, 10-result bound |

Authoritative validation, snapshot handling, bounded repair, and the
"never broaden to an unfiltered search" rule are unchanged. Each ID in a
grouped decision is validated the same way as a single ID.

## 6. Observability

Assistant (bounded labels only; no category IDs, queries, or user IDs):

- counter `ai_assistant.category_searches`, with label
  `ai.search.category_count` ∈ {`1`, `2`, `3`};
- counter `ai_assistant.category_search.rejections`, with label
  `error.type` ∈ {`too_many_categories`, `category_array`,
  `alternatives_disabled`}.

Product-service keeps its existing Micrometer/Actuator metrics. Add a counter
`taskmaster.search.multi_category`, tagged `categoryCount` and
`emptyGroupCount` (both 0-3). Do not add tracing exporters.

Logs record counts and outcomes only, following the existing metadata-only
logging rule. Never log prompts or tool results.

## 7. Evaluation and release gates

Build a reviewed fixture of at least 60 prompts against the frozen current
catalog, labeled with the acceptable category set:

- at least 20 clear single-category requests;
- at least 20 genuinely ambiguous requests, such as "fix my refrigerator" or
  "help with my yard";
- at least 10 requests covering two independent services;
- at least 10 unsupported or off-topic requests.

Compare `MaxCategories = 1` and `MaxCategories = 3` with the same model,
catalog, provider data, and hardware. Run each prompt three times.

| Metric | Gate (target, not a measured claim) |
|---|---|
| Ambiguous requests where at least one acceptable category is searched | Improves by ≥ 15 percentage points over `MaxCategories = 1` |
| Clear requests that unnecessarily add alternatives | ≤ 10% |
| Clear-request primary accuracy | No regression greater than 2 points |
| Unsupported requests that trigger any provider search | No increase |
| Fabricated TaskMaster names in answers | 0 |
| Warm end-to-end p95 latency for grouped searches | ≤ 1.3 × single-category baseline |

If gates fail, leave `MaxCategories = 1` and revise the prompt or catalog
descriptions. Do not claim improvement based only on more results.

## 8. Implementation plan

1. **Product-service:** add parameter parsing and validation, grouped
   execution with fair allocation, the response DTO, and the metric. Use unit
   tests for:
   - normalization and deduplication;
   - each error code;
   - comma-in-`category` rejection;
   - round-robin allocation, including uneven group sizes;
   - cross-group deduplication and `alsoMatchedCategoryIds`;
   - empty groups;
   - byte-compatible single-category output.

   Register nothing new; `McpToolsConfig` already registers the tool.
2. **Assistant schema and gate:**
   - add the `MaxCategories` option and its validation;
   - add the `McpRemoteTool` type/items override;
   - have the refresher apply the items enum;
   - add the tool-call gate and grouped `ExtractMentions`;
   - add metrics.

   Use xUnit tests with a stubbed model, covering:
   - schema with 1 versus 3 categories;
   - an array-valued `category` being rejected;
   - too many alternatives being rejected;
   - mentions extracted from groups;
   - no mentions from few-shot examples.
3. **Prompt:** update the system prompt and the few-shot example, and run the
   section 7 evaluation.
4. **E2E:** one Playwright scenario with a stubbed AI response that performs
   a grouped search and renders mentions from two groups. Stub AI network
   calls as the E2E conventions require.
5. **Enable:** set `CategorySearch:MaxCategories = 3` in Compose only after
   the gates pass. Rollback is setting it back to `1`; no data changes.

Deploy product-service before enabling the assistant setting. An assistant
configured for alternatives against an older product-service would get
`alternativeCategories` ignored or rejected. Startup cannot detect this, so
the deploy order is an operational requirement.

## 9. Worked example

User: "Could you find me somebody to fix my refrigerator? I live in San
Francisco."

1. The model calls `search_task_masters` with `category: "home-repair"`,
   `alternativeCategories: ["electrical"]`, and `location: "San Francisco"`.
2. Product-service validates both IDs, runs two cached per-category searches,
   and returns two groups sharing at most 10 TaskMasters.
3. The answer lists Home Repair providers first, then Electrical providers
   (or says none were found in San Francisco), and asks whether the problem is
   electrical or mechanical.
4. The user replies "it won't turn on". The next turn may search
   `electrical` alone for San Francisco.
