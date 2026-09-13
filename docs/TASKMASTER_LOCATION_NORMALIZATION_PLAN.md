# TaskMaster Location Normalization Plan

> Status: **Proposed**
>
> Scope: Normalize TaskMaster locations in `product-service` so searches can
> match a city (`Chicago`), state (`IL` or `Illinois`), or city and state
> (`Chicago, IL`). This first version supports US city/state locations.

## Problem

TaskMaster profiles currently store location as one display string, such as
`Chicago, IL`. MongoDB search uses exact equality, so a user search for `IL`
does not match that profile even when the category is correct.

## Target design

Keep the existing `location` value for display and API compatibility, and add
internal normalized searchable fields:

```json
{
  "location": "Chicago, IL",
  "locationCity": "chicago",
  "locationStateCode": "IL"
}
```

City values are trimmed, whitespace-normalized, and stored in lowercase for
matching. US state names and abbreviations are converted to the canonical
two-letter uppercase code. Successful writes also rewrite `location` to the
canonical `City, ST` display form.

The normalized fields are server-controlled and ignored in request JSON.
Controllers currently bind directly to Mongo entities, so clients must not be
able to provide `locationCity` or `locationStateCode`; product-service always
recomputes them from the accepted location input.

Search behavior:

- `IL` or `Illinois` filters by `locationStateCode = "IL"`.
- `Chicago` filters by `locationCity = "chicago"`.
- `Chicago, IL` filters by both normalized fields.
- An ambiguous full state/city name such as `New York` searches for either
  `locationCity = "new york"` or `locationStateCode = "NY"`. A comma removes
  the ambiguity: `New York, NY` requires both fields.
- Invalid or unsupported locations return a structured validation error. MCP
  search must not silently remove an invalid location filter and broaden the
  result set; the assistant should ask the user for a supported city/state.
- The response continues returning the human-readable `location`.

## Implementation plan

1. Add a shared immutable product-service location value object and parser.
   Support canonical US state names and codes, normalize city whitespace/case,
   format `City, ST`, and return typed validation results for write and search
   inputs.
2. Add `locationCity` and `locationStateCode` to `TaskMaster` and
   `TaskMasterApplication`. Exclude both fields from request JSON and always
   recompute them server-side when applications or profiles are created or
   edited. When an application is accepted, copy all three canonical location
   fields into the new TaskMaster profile.
3. Add candidate MongoDB indexes for state
   (`{ "locationStateCode": 1 }`), city/state
   (`{ "locationCity": 1, "locationStateCode": 1 }`), category/state
   (`{ "jobCategories": 1, "locationStateCode": 1 }`), and
   category/city/state
   (`{ "jobCategories": 1, "locationCity": 1, "locationStateCode": 1 }`).
   `jobCategories` is an array, so the last two are multikey indexes. Confirm
   their query plans with representative category-plus-location searches before
   retaining all four indexes.
4. Replace exact display-string filtering in
   `TaskMasterSearchRepositoryImpl.searchWithFilters`. Replace the derived
   `TaskMasterRepository.findAllByLocation` overloads with normalized custom
   queries used by `ProductCacheService.getByLocation` and
   `getByLocationLimited`.
5. Build cache keys from the normalized search meaning rather than raw input.
   Update the keys for `getByLocation`, `getByLocationLimited`, and
   `searchWithFilters`, so `IL` and `Illinois` share the same state-search key
   while city and city/state searches remain distinct.
6. Update `TaskMasterMcpTools.search_task_masters` and its `@ToolParam`
   description to document city, state, and city/state input. Return a
   machine-readable validation result for unsupported locations rather than
   throwing or dropping the filter.
7. Update both frontend write forms to collect city and state separately,
   validate them, and send the canonical `City, ST` value. There is currently
   no React location-search UI; search behavior remains limited to the MCP tool
   and existing REST endpoint unless a separate UI feature is added.
8. Update `seed/taskMasters.json` and every mock/test document that creates a
   TaskMaster or TaskMasterApplication to use canonical US city/state values and
   populate normalized fields. Replace stored non-US samples such as `Hanoi`.
   Keep or add city-only values such as `Chicago` where they are search inputs,
   because city-only search is supported and requires coverage.
9. Add parser, repository, cache, controller, application-acceptance, MCP, and
   frontend tests for state code, full state name, city-only, combined,
   case-insensitive, canonical formatting, invalid, and ambiguous inputs.

## Compatibility and rollout

This development rollout does not include legacy-query fallback or an in-place
MongoDB migration. Recreate the product-service database after the model, seed,
and fixture changes are ready so every document starts with normalized fields.
Do not enable normalized search against a database containing legacy documents,
because MongoDB cannot match normalized fields that are absent.

All writes must persist the canonical display location and both normalized
fields atomically. Existing browser-facing routes and MCP result shapes keep the
`location` string unchanged. A production rollout with data that cannot be
discarded would require a separate idempotent backfill plan before normalized
search is enabled.
