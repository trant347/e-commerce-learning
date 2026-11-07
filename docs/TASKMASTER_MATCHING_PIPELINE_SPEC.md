# TaskMaster Matching Pipeline: Jev, Ollama and Search

> Status: **Partially implemented; release evaluation pending**
>
> October 2, 2026. This specification owns the end-to-end matching pipeline,
> hard-versus-soft filter handling and grounded presentation. It extends
> the category-resolution design without replacing its catalog, Choice API,
> acceptance policy or search authorization contracts.
>
> On-demand amendment (November 7, 2026): in rich-catalog mode the conversational
> model selects tools before matching starts. The implemented search placement
> and remaining budget/rollout work are tracked in
> [on-demand search](AI_SEARCH_ON_DEMAND_DISCUSSION.md). This does not change
> the category, filter, ordering, or grounding contracts below.

## 1. Decision and boundaries

Use Jev for bounded category decisions, the configured chat model (intended:
Ollama) for filter extraction and prose, and product-service for retrieval.
There is no second Jev pass after retrieval.

```text
User request and bounded chat history
    v
Conversational model: answer, use another tool, or request marketplace search
    |-- No search requested: no category decision or filter extraction
    v
SearchOrchestrator: require the request-captured usable catalog
    v
User request and relevant user-authored clarification context
    |-- Jev: intent/category decisions against the captured catalog
    |-- Configured chat model (intended: Ollama): typed filter extraction
    v
Application validation and ambiguity handling
    v
Product-owned MCP search with locked categories and supported hard filters
    v
Chat-model answer grounded in those results
```

The current orchestrator runs category resolution and filter extraction
sequentially, only after a search tool call. Proposed concurrency may run both
when they use the same captured user context and extraction does not require the category
answer. Join and validate both before search; concurrency must not authorize
early tool execution or create independent request deadlines. In hybrid mode,
an accepted exact result skips the category Jev call, not filter validation.

The [category resolution spec](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md#jev-category-first-integration-proposed)
owns snapshots, intent/category Choice questions, abstention, locked search
arguments and exact resolution. The
[multi-category spec](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md) owns bounded
alternatives, grouped search, allocation and deduplication. This document owns
filter interpretation and grounded presentation; it does not change
product-service retrieval order, caches, result limits or public response shapes.

OpenRouter chat remains supported instead of Ollama. Jev uses the separate
Decisions API, not the Chat Completions adapter, and never executes tools or
supplies provider records. No new model-executable tool, frontend feature,
durable conversation store, taxonomy or availability API is introduced.

## 2. Typed filter extraction

Use a typed internal extraction envelope separating supported hard filters
from soft preferences and retaining the user text that supports each value
for in-memory validation. Do not log that evidence. Unspecified values remain
absent; never infer a city, budget, rate threshold or rating from examples or
model defaults. Preserve explicit values across clarification using the
category spec's conversation rules; if context is missing, ask again.
No public response or MCP parameter change is implied by this envelope.

The current search supports `location`, `minRate`, `maxRate` and `minRating`.
Validate types, finite numbers, supported ranges, minimum/maximum consistency
and the existing location contract before tool execution. Distinguish hourly
USD limits from total-job budgets: "$50 total" must not become `maxRate: 50`.
The assistant's new-mode validator accepts hourly USD rates from 0 through
100,000 and ratings from 0 through 5; out-of-range hard values require
clarification rather than a broader search. It independently checks recognizable
explicit hourly limits, ratings and city/state locations against extracted hard
filters so an omitted constraint does not silently broaden retrieval.
Clarify unsupported/ambiguous units when needed for a hard constraint.
Dates and availability are not supported search filters in this proposal;
do not claim they have been verified or invent a search parameter.

Treat "must be under $50/hour" as a hard supported constraint and "preferably
under $50/hour" as a soft preference. Only hard constraints become exclusion
filters. Soft preferences remain presentation context; do not let them silently
exclude candidates. Product-service owns authoritative filter enforcement.
Unsupported hard requirements must be clarified or explicitly identified as
unverified, not silently dropped while claiming a fully compliant match.

## 3. Retrieval and category ambiguity

Retrieval cannot recover candidates excluded by a wrong category or an
over-strict filter, and final-answer presentation cannot repair that loss.
When bounded multi-category mode is enabled, apply its reviewed
alternative-selection policy to the validated distribution; never blindly
search the top three options. In single-category mode, uncertain decisions
still clarify without searching. Neither mode broadens categories or relaxes
hard filters in response to empty results.

Use the existing MCP search and at-most-10-result ceiling. Search waits for
validated category and filter outcomes; native and inline-recovered calls
must pass the same gate. An accepted category is locked for the request and
cannot be replaced by filter extraction or final-answer generation.

## 4. Candidate ordering and grounded presentation

Preserve the order returned by product-service. The chat model may explain
which returned TaskMasters appear relevant to the user's stated needs, but it
must not perform a second hidden ranking pass, remove candidates, claim a
globally best provider, or present soft preferences as verified constraints.
All claims must be grounded in public fields returned by the current search.

Jev is used only for the bounded intent/category decision before retrieval.
Candidate scoring through Jev's Score primitive is outside this integration:
it would add latency, cost, configuration and another failure boundary without
improving retrieval recall. If future evidence justifies semantic candidate
ranking, it requires a separate specification and evaluation rather than being
implicitly enabled by the category-first flow.

Decisions are not marketplace sources or mentions. Sources and mentions remain
grounded only in tools executed for the current request.

## 5. Evaluation and release gates

Label hard constraints, soft preferences, supported filter values/units and
acceptable top-three providers using frozen provider fixtures. Include
ambiguous budgets, missing location, hourly versus total price, unavailable
profile evidence, empty results and fewer than three candidates. Filter
extraction must pass every deterministic schema/unit case and must never
turn labeled soft preferences into exclusion filters.

Evaluate category decisions, filter extraction and retrieval on identical
requests and provider fixtures. Record category accuracy and abstention,
hard-filter preservation, unsupported-filter handling, grounded-response
accuracy, end-to-end p50/p95 latency, call count and cost. Include single and
grouped searches. Zero fabricated providers and zero violations of enforced
hard constraints are release requirements.

## 6. Implementation coverage and sequence

| Surface | Required coverage |
|---|---|
| Filter extraction | Typed hard/soft separation, absent values, units/ranges, location validation, clarification preservation, unsupported requirements, concurrent join before search |
| Search authorization | Locked accepted categories, validated filters, no early execution, no broadening after empty results |
| Grounded presentation | Current-request tool evidence only, unchanged candidate set/order, no unverified preference or availability claims |

1. Implement category-first prerequisites and typed filter handling through
   JEV-03 to JEV-09 in the existing implementation checklist.
2. Evaluate and document the category, filter, search and grounded-answer flow.

Use existing xUnit/integration and Playwright infrastructure with stubbed
Ollama/Jev calls in default CI; real-model evaluation is separate.
Task dependencies and completion status remain in
[TaskMaster Category Management and Jev Tasks](TASKMASTER_CATEGORY_MANAGEMENT_TASKS.md#active-plan-jev-category-first-integration).

## References

- [Category catalog and resolution](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md)
- [Bounded multi-category search](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md)
- [OpenRouter integration](OPENROUTER_INTEGRATION_SPEC.md)
- [OpenRouter: What is Jev?](https://openrouter.ai/blog/insights/what-is-jev/)
