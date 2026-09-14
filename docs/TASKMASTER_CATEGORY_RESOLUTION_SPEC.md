# TaskMaster Category Resolution Specification

> Status: **Proposed**
>
> Purpose: Replace prompt-level category special cases with a product-owned,
> deterministic way to map natural-language service requests to TaskMaster
> categories.

## Problem

The `search_task_masters` tool currently requires the AI model to choose one
exact category value. This is unreliable when categories overlap or the user
describes an object rather than a trade.

For example:

- User request: `fix my wooden cupboard`
- Model-selected category: `home-repair`
- Relevant TaskMaster category: `carpentry`

The location and budget filters can be correct while the exact category filter
still removes the appropriate provider. Adding examples such as `cupboard ->
carpentry` to the system prompt fixes individual phrases, but it does not scale
as categories and user vocabulary grow.

## Goals

- Make category resolution deterministic and owned by `product-service`.
- Allow natural-language descriptions such as `repair a wooden cabinet`.
- Support aliases, common objects, skills, and related terminology without
  changing the AI prompt for every phrase.
- Search more than one plausible category when appropriate.
- Preserve exact category searches for callers that already know the category.
- Keep category taxonomy and search behavior consistent for every client.
- Let TaskMaster applicants propose a missing service without allowing them to
  create public marketplace categories directly.

## Non-goals

- General-purpose semantic web search.
- Replacing TaskMaster categories with unrestricted model-generated labels.
- Requiring a larger language model to make category selection reliable.
- Silently searching every category when the request cannot be resolved.

## Interim multi-category approach

Before implementing the product-owned resolver, extend
`search_task_masters` so the AI model can select up to three plausible
categories instead of requiring one exact category.

Example:

```json
{
  "categories": [
    "carpentry",
    "furniture-assembly",
    "home-repair"
  ],
  "location": "IL",
  "maxRate": 70
}
```

Product-service searches the supplied categories with MongoDB `$in` semantics:

```javascript
{
  "jobCategories": {
    "$in": ["carpentry", "furniture-assembly", "home-repair"]
  }
}
```

Category matching uses OR semantics. Location, rate, and rating filters continue
to use AND semantics with the category condition. A TaskMaster matching more
than one requested category appears only once.

Interim constraints:

- Accept between one and three unique categories.
- Require every value to be a canonical category published by
  `get_categories`.
- Preserve the supplied category order and rank matches for the first category
  ahead of matches found only through later alternatives.
- Reject unknown categories and requests exceeding the category limit.
- Keep the singular `category` parameter temporarily for backward
  compatibility, but reject requests that supply both forms.
- Include the category or categories that matched each result in tool
  diagnostics so ranking can be understood.
- Build cache keys from the ordered, normalized category list plus the other
  normalized filters.

This improves recall when categories overlap, but it remains model-dependent.
The model may still omit the correct category or choose categories that are too
broad. It is therefore an incremental mitigation, not a replacement for the
product-owned resolver described below.

## Proposed category taxonomy

Product-service owns metadata for each canonical category:

```json
{
  "id": "carpentry",
  "displayName": "Carpentry",
  "description": "Wood construction, modification, and repair.",
  "aliases": [
    "woodworking",
    "wood repair",
    "cabinet repair",
    "cupboard repair"
  ],
  "objects": [
    "cabinet",
    "cupboard",
    "wooden table",
    "wooden furniture"
  ],
  "relatedCategories": [
    "furniture-assembly",
    "home-repair"
  ]
}
```

Canonical IDs remain the values stored in `TaskMaster.jobCategories`. Aliases
and related terms are search metadata, not additional stored profile
categories.

The first implementation may keep this taxonomy in a versioned product-service
resource file. If categories later become administratively managed, the same
contract can move to MongoDB without changing MCP clients.

## Taxonomy ownership and governance

The marketplace owns canonical categories, aliases, object terms, and category
relationships. Individual TaskMasters do not create or modify shared taxonomy
entries.

TaskMasters provide:

- One or more canonical categories that describe their services.
- A free-text profile description and experience details.
- A category proposal when no existing category is appropriate.

Product-service and marketplace administrators control:

- Canonical category IDs and display names.
- Category descriptions and required profile attributes.
- Search aliases, object terms, and related-category relationships.
- Approval, rejection, merging, and retirement of categories.
- Reclassification of profiles assigned to an incorrect category.

Aliases apply marketplace-wide. They must not be generated automatically from
one TaskMaster's profile because inaccurate or intentionally misleading text
could affect search results for every provider. An AI or analytics process may
suggest aliases from unresolved searches, but an administrator must approve
them before publication.

## Missing-category proposal workflow

TaskMaster registration should continue requiring a category decision, but the
form must provide a `Category not listed` option. Selecting it requires:

- A proposed category name.
- A description of the service and typical work performed.
- Optional examples of objects, skills, or customer phrases associated with
  the service.

The applicant can submit the application without selecting an existing
category, but the resulting TaskMaster profile must not be published or
searchable until an administrator resolves the proposal.

Suggested proposal states:

```text
PENDING
APPROVED_NEW_CATEGORY
MAPPED_TO_EXISTING
REJECTED
```

Administrative outcomes:

- `APPROVED_NEW_CATEGORY`: Create a canonical category and assign it to the
  application.
- `MAPPED_TO_EXISTING`: Assign an existing canonical category. The
  administrator may also add the proposed wording as an approved alias.
- `REJECTED`: Do not create the category or publish the profile. Store an
  applicant-visible reason.

Creating a new category should be reserved for a distinct, reusable service.
Minor wording differences should normally become aliases, and overlapping
services should normally map to an existing category.

Suggested proposal data:

```json
{
  "proposedName": "Furniture restoration",
  "serviceDescription": "Repair and refinish damaged antique furniture.",
  "exampleTerms": ["antique chair repair", "wood refinishing"],
  "status": "PENDING",
  "resolvedCategoryId": null,
  "reviewedBy": null,
  "reviewedAt": null,
  "resolutionReason": null
}
```

The proposal should be stored with the TaskMaster application or referenced by
it so approval of the application and category resolution remain auditable.
When mapped or approved, product-service writes only the resulting canonical
category ID to `TaskMaster.jobCategories`.

## Resolution contract

Add a product-domain category resolver with an immutable result:

```text
resolve(serviceDescription) ->
  normalizedQuery
  matches[]
    categoryId
    matchType
    score
```

Suggested match types:

- `EXACT_CATEGORY`: direct canonical ID or display-name match.
- `ALIAS`: an explicit configured phrase match.
- `OBJECT_SKILL`: an object and requested action match, such as
  `repair + wooden cupboard`.
- `RELATED`: a lower-priority related category.

Scores are deterministic values used for ordering; they are not model
confidence estimates. Exact category and alias matches must rank above related
category matches.

## MCP contract

The interim contract accepts either:

- `category`: one exact canonical category.
- `categories`: an ordered list of up to three exact canonical categories.

The final resolver-based contract evolves `search_task_masters` to accept
either:

- `category`: an optional exact canonical category for trusted callers.
- `serviceQuery`: the user's natural-language description of the requested
  work.

The AI assistant should normally pass `serviceQuery`, plus independently
extracted filters such as location, maximum rate, and minimum rating. It should
not be responsible for translating the request to one exact category.

Example:

```json
{
  "serviceQuery": "fix my wooden cupboard",
  "location": "IL",
  "maxRate": 70
}
```

Product-service resolves the service query and performs the category search.
The result shape should include the resolved category IDs for diagnostics while
keeping TaskMaster profile fields unchanged.

## Search behavior

1. If an exact `category` is supplied, validate and search that category.
2. Otherwise, resolve `serviceQuery` against the taxonomy.
3. Search the highest-confidence category matches using an `$in` query against
   `jobCategories`.
4. Apply location, rate, and rating filters normally.
5. Rank exact and alias category matches ahead of related-category matches.
6. Deduplicate TaskMasters that match multiple resolved categories.
7. Return a machine-readable `unresolved_service_category` result when there
   is no acceptable match. Do not remove the category condition and broaden
   the search to every provider.

For `fix my wooden cupboard`, the expected resolution is:

```json
{
  "primary": "carpentry",
  "alternatives": ["furniture-assembly", "home-repair"]
}
```

David Williams matches `carpentry`, `IL`, and the `$70/hour` maximum and should
therefore be returned.

## Cache and index considerations

- Build cache keys from the ordered resolved category IDs, taxonomy version,
  and normalized business filters.
- Equivalent requests that resolve to the same categories should reuse the
  same cache entry.
- Continue using the existing multikey category/location indexes. Confirm the
  `$in` query plans before adding new indexes.
- Evict category search caches when TaskMaster categories or taxonomy metadata
  change.

## AI assistant changes

- Pass the user's service wording to `serviceQuery`.
- Keep location, rate, and rating extraction separate.
- Remove object-to-category special cases from the system prompt after the new
  MCP contract is deployed.
- Keep only general instructions such as using marketplace tools and not
  inventing providers.
- Continue deriving response sources and mentions only from tools executed for
  the current request.

## Validation and error handling

- Reject requests that supply neither `category` nor `serviceQuery`.
- Reject unknown exact categories with a machine-readable error.
- Reject excessively long service queries using an explicit length limit.
- Normalize case, punctuation, and whitespace before matching.
- Do not silently substitute a default category.
- Log the normalized query, resolved category IDs, and match types without
  logging credentials or trusted execution metadata.

## Testing

Add coverage for:

- Exact canonical categories.
- Aliases and common object names.
- Object/action combinations such as repairing versus assembling furniture.
- Overlapping categories and deterministic ordering.
- Unknown or ambiguous requests.
- Multi-category MongoDB queries and deduplication.
- Cache-key equivalence and taxonomy-version changes.
- MCP validation and machine-readable errors.
- Registration with existing categories and with `Category not listed`.
- Admin approval as a new category, mapping to an existing category, alias
  approval, and rejection with a reason.
- Prevention of profile publication while a required category proposal is
  unresolved.
- End-to-end requests including:
  `fix my wooden cupboard, I live in IL, budget $70/hour`.

## Rollout

1. Add interim MCP and repository support for up to three ordered categories.
2. Update the AI instructions to select multiple plausible categories when a
   request overlaps trades, while keeping all categories constrained by the
   published enum.
3. Add the taxonomy model and resolver to product-service.
4. Add the category-proposal model, TaskMaster application fields, applicant
   form, and admin review workflow.
5. Extend the MCP contract with `serviceQuery` while retaining exact
   `category` compatibility.
6. Update AI assistant instructions to pass the natural-language service
   request.
7. Add tests and compare existing common queries against both the interim and
   resolver-based behavior.
8. Deploy product-service, restart AI assistant MCP discovery, and verify live
   searches.
9. Remove the temporary prompt-level cupboard/carpentry special case.
10. Remove the model-selected `categories` parameter after callers have migrated
   to `serviceQuery`, unless exact multi-category search remains useful for
   trusted non-AI clients.
