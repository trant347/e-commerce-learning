# On-Demand AI Search and Follow-Up Context

> Status: Phase 1 tasks 1-5 and 7 implemented. Budget/deadline review (task 6)
> and rollout/cost evaluation (task 8) remain pending. Phase 2 follow-up context
> is proposed, not implemented.
>
> Created: 2026-11-01. Updated: 2026-11-07.
>
> This document records on-demand search behavior and its remaining work.
> Category, filter, and retrieval contracts remain owned by the related
> specifications linked below. Implementation progress is not release approval.

## Problem observed

A conversation can lose its meaning during a short follow-up:

1. User asks for a babysitter in Los Angeles.
2. Assistant suggests a person in Houston and asks whether the user wants that person.
3. User replies "yes."
4. Assistant asks for clarification again instead of continuing.

This is a user-reported example, not a reproduced test case. Whether the original
provider/location claims were grounded in actual search results still needs
investigation.

## Current implementation

In rich-catalog mode, `AiAssistantService.RunConversationAsync` captures a usable
category catalog snapshot before the tool loop. Capture makes no model call;
a missing or expired catalog does not block ordinary chat or unrelated tools.

The conversational model runs first and decides whether the current request
needs a marketplace search. Only a `search_task_masters` tool call invokes
`SearchOrchestrator`. The orchestrator resolves the category against the captured
catalog, then extracts and validates user-evidenced filters before retrieval
through `ToolRegistry` and product-service MCP. All model-supplied search
arguments are ignored.

If search is requested without a usable captured catalog, the request ends with
an unavailable response and no retrieval. Clarification, no-match,
not-applicable, and unavailable orchestration outcomes also end that request
with deterministic safe responses; they are not permission to search without
validation.

Successful results are attached to the original tool-call ID. The
`marketplaceSearchExecuted` flag permits one marketplace retrieval per request,
including an empty successful result. Further search calls receive a tool error
directing the model to use the existing results; unrelated tools remain usable.
The flag resets for each chat request.

`OnDemandSearchSystemPrompt` describes this behavior. The additional "do not call
search_task_masters again" system message is appended only after a live search
completes, not before the first model call. Legacy mode retains its configured
prompt and tool flow; there is no separate on-demand rollout flag yet.

### Historical eager flow

Before Phase 1 task 4, rich-catalog requests attempted category resolution
before the conversational model. A resolved category triggered filter extraction
and prefetched search; clarification/no-match outcomes returned before the
conversational model could interpret the exchange. This is no longer the current
rich-catalog flow.

### Remaining follow-up limitations

The main conversation receives both user and assistant history, subject to the
configured history limit. However, the category resolver and filter extractor
receive only user-authored history.

Consequently, those components may see:

```text
User: Find a babysitter in Los Angeles.
User: Yes.
```

They do not receive the assistant question that gives "yes" its meaning:

```text
Assistant: Would you consider this person in Houston?
```

The filter validator also checks explicit constraints across user history.
Without an explicit mechanism for superseding an earlier constraint, changing
the location can leave the old location relevant to validation.

These are plausible contributors to the reported behavior, not a confirmed
end-to-end diagnosis.

Review of the current code confirmed these points:

- `userHistory` is built from user-role turns only before category resolution
  and filter extraction run.
- Category resolution and filter extraction run only when search is requested;
  moving them into that path does not fix their missing assistant context.
- `AllExplicitHardFiltersPreserved` scans every user turn, so a changed location
  cannot supersede an earlier one and tends to force clarification.
- The main model receives only the last configured history window, but the
  category resolver and filter extractor receive all user turns from the
  request history. They can disagree about which context is active.
- Chat history is client-supplied and passed through by the BFF unchanged, so
  assistant turns in `request.History` are not trusted server state.

## Implemented on-demand search

Separate the decision to use search from the validated execution of search:

```text
User message + relevant conversation context
    |
    v
Conversational model reads the current message and bounded chat history
    |
    +-- Answer without retrieval when appropriate
    +-- Call a profile or booking tool when appropriate
    +-- Request marketplace search when needed
            |
            v
        Require a usable captured catalog; otherwise return unavailable
            |
            v
        Resolve category against that catalog
            |
            v
        Extract and validate current search constraints
            |
            +-- Return a deterministic safe outcome without retrieval
            +-- Execute product-service MCP search
                    |
                    v
                Return live results to the conversational model
```

The LLM decides **whether to search**. Application code controls **how to search
safely**.

### Keep validation inside the search path

Do not expose category resolution and unrestricted search as separate tools and
rely on the model to call them in the correct order.

The model-callable search capability enforces the existing category and
filter pipeline before invoking product-service. The model must not bypass
validation, invent category IDs, or silently relax constraints.

The tool loop intercepts every rich-mode `search_task_masters` call, even if
catalog capture failed. Search must never fall through to ordinary dispatch
using model arguments when validation is unavailable.

Trusted conversation context, deadlines, and round budgets must not become
model-controlled arguments. Actual marketplace retrieval remains product-owned.

### Implemented placement: request-scoped search orchestrator (Option B)

Two placements were considered:

- **Option A — `IToolDefinition` wrapper.** Rejected. A tool receives only
  `ToolExecutionContext` plus model arguments and returns only a string. It has
  no access to the captured catalog snapshot, conversation context, chat client,
  round budget, or deadline without widening the trusted context, and it cannot
  end the request with a deterministic clarification/no-match/unavailable
  answer.
- **Option B — orchestrator called from the tool loop.** Implemented.

The model still sees one tool, `search_task_masters`, with the product-owned
name and schema. The orchestrator is internal and is not model-visible.

```text
Model requests search_task_masters(...)
    |
AiAssistantService tool loop          <- interception happens here (exists today)
    |
SearchOrchestrator.RunAsync(requestContext)
    |-- category decision (Jev or configured chat model) against the captured catalog
    |-- filter extraction and validation
    |-- on failure: return typed outcome, no retrieval
    |-- ToolRegistry.ExecuteAsync("search_task_masters", lockedArgs)
            |
        MCP -> product-service
```

- The request context (snapshot, conversation context, chat client, budget,
  deadline) is built by application code.
- `RunAsync` returns `Results`, `NeedsClarification`, `NoMatch`, `NotApplicable`,
  or `Unavailable`. The loop appends results as
  the tool message for the model's `toolCall.Id`; other outcomes end the request
  with deterministic `SafeAnswer` responses. `NoMatch` explains that no offered
  service fits; `NotApplicable` asks which marketplace service to search.
- The model tool call signals search intent, not trusted search arguments.
  Category and hard filters sent to product-service always come from the
  category decision and validated extraction. For example, if the user asks "Find a babysitter in
  Los Angeles, CA" and the model calls
  `search_task_masters({"category":"childcare","location":"Houston, TX","minRating":4.5})`,
  the orchestrator ignores those values: the category comes from the
  confidence-checked category decision, `location` is `Los Angeles, CA` from
  user-evidenced extraction, and no `minRating` is sent because the user never
  stated one. This prevents invented filters, silent relaxation or substitution
  of constraints, unchecked category selection, and prompt-injection influence
  on retrieval. `SearchOrchestrator.LockedSearchArguments` builds the retrieval
  arguments; the loop does not forward `toolCall.Arguments` to it.

  Illustrative model tool call (not captured from logs). The `arguments` string
  is the model-supplied search arguments:

  ```json
  {
    "role": "assistant",
    "tool_calls": [{
      "id": "call_abc123",
      "function": {
        "name": "search_task_masters",
        "arguments": "{\"category\":\"babysitting\",\"location\":\"Los Angeles, CA\",\"maxRate\":30}"
      }
    }]
  }
  ```

  Illustrative ways those arguments can be right or wrong:

  | User said | Model arguments | Problem |
  |---|---|---|
  | "Babysitter in Los Angeles, CA" | `{"category":"babysitting","location":"Los Angeles, CA"}` | Correct, but still not trusted |
  | Same | `{"category":"babysitting","location":"Los Angeles, CA","minRating":4.5}` | Invented filter |
  | Same, after the assistant suggested Houston | `{"category":"babysitting","location":"Houston, TX"}` | Substitutes the assistant's suggestion |
  | "Cheapest babysitter in LA" | `{"category":"babysitting","maxRate":15}` | Turns qualitative price wording into a number |
  | "Someone to watch my kids and clean" | `{"category":"cleaning"}` | Guesses one category instead of clarifying |

  All model arguments are discarded, including correct values; there is no
  per-field selection. The orchestrator builds retrieval arguments only from
  trusted sources (`LockedSearchArguments`: category from the category decision,
  hard filters from user-evidenced extraction). From the model tool call, the
  system keeps only:

  - the intent to search now, which triggers the orchestrator;
  - `toolCall.Id`, so the result is attached as a valid tool message.

  Optional category-hint reconciliation remains a proposal; it is not part of
  the current implementation.
- `ToolRegistry` remains a plain dispatcher. It must not intercept
  `search_task_masters`; otherwise the orchestrator's own retrieval call would
  recurse, and the registry lacks request-scoped context.
- Retrieval goes through the discovered MCP tool, not a direct product-service
  REST call, so the product service keeps ownership of the search contract and
  MCP tracing/telemetry is preserved.

Implementation anchors:

- `AiAssistantService.RunConversationAsync`: catalog capture, tool interception,
  deterministic safe responses, original tool-call IDs, and one-search tracking.
- `AiAssistantService.OnDemandSearchSystemPrompt`: initial rich-mode instructions;
  the post-search system message is emitted inside the successful search branch.
- `SearchOrchestrator.RunAsync`: category decision, filter validation, locked MCP
  retrieval, typed outcomes, and soft-preference context.
- `SearchRequestContext` and `SearchResult` in
  `ai-assistant-service\Services\Tools\ISearchOrchestrator.cs`: internal contracts.
- `CategoryFirstIntegrationTests`: search authorization, unavailable-catalog
  isolation, repeated calls in one batch or later rounds, and per-request reset.

### Category tools

A model-visible catalog-listing tool is acceptable for informational
questions such as "what services do you offer?" It must not become the path by
which the model selects the search category.

The existing search schema exposes an optional `category` with a live enum from
`McpCategoryEnumRefresher`; rich-mode orchestration still discards that argument.
As a future proposal, `search_task_masters` may accept a model-proposed category
as a hint, validated against the captured catalog and reconciled with the category
decision. Do not make `category` required, because that encourages the model
to fill it with a guess. Model choice must not bypass the confidence policy.

## Proposed Phase 2: follow-up context

Moving search alone does not solve context loss. The system also needs to
interpret responses such as "yes," "that person," and "try Houston instead."

For the example above:

- The assistant question establishes what "yes" refers to.
- The user's reply can confirm the proposed location or candidate, if the
  question is unambiguous.
- The resulting active constraints should replace a superseded location rather
  than combine Los Angeles and Houston as simultaneous requirements.
- If the reply is ambiguous, ask a focused clarification.

Assistant messages may provide reference-resolution context, but they must not
be treated as user authorization or factual marketplace evidence on their own.

Proposed rules:

- Because history is client-supplied, an assistant turn may help interpret what
  "yes" refers to, but every hard-filter value must still have evidence in a
  user-authored turn. If the user confirms a value that appears only in an
  assistant turn, ask a focused clarification or use a structured
  suggestion/quick-reply. Durable server-side conversation state belongs to the
  hiring-agent work.
- Represent active constraints as a server-built snapshot with one value per
  field (`location`, `minRate`, `maxRate`, `minRating`) and the supporting user
  turn. Later explicit values supersede earlier ones. Validate against that
  snapshot instead of requiring every historical hard filter to be preserved.
- Category resolution, filter extraction, and the main model should share one
  bounded context window.

## Safety and grounding boundaries

- Provider existence, names, rates, locations, and availability require live
  tool evidence; an earlier assistant statement is not proof.
- Do not automatically broaden a Los Angeles search to Houston. A location
  change requires user direction or confirmation.
- Preserve validated category selection before retrieval and product-service
  result order. Do not introduce post-retrieval Jev scoring.
- Distinguish interpreting a confirmation from authorizing a mutation. A casual
  chat "yes" must not bypass the hiring agent's proposed durable approval rules.
- Ordinary conversation and unrelated booking tools should not fail solely
  because the category catalog is unavailable. Marketplace search should report
  that failure honestly when invoked.

## Tradeoffs

On-demand search adds an initial tool-selection model round for search requests,
but avoids category-resolution work for messages that do not need search.

| Request type | Historical eager flow | Current on-demand flow |
|---|---|---|
| Provider search | category decision -> extraction -> answer (3) | tool selection -> category decision -> extraction -> answer (4) |
| Non-search | category decision -> main model (2) | main model (1) |

These counts include category-decision rounds even when Jev, rather than the
chat model, performs them. A shared repair allowance can add one round.
`RequestRoundBudget(MaxToolRounds)` currently allows five total decision/model
rounds under one request deadline. With a repaired search, the final answer can
consume the last round, leaving no room for another conversational tool round.
Task 6 must review budget sufficiency; this table is not a latency measurement.
Task 8 must compare latency and token use for the changed flow before release.
Follow-up quality still depends on sufficient relevant context being available;
this change does not by itself provide durable conversation memory.

For a future hiring agent, search becomes a capability invoked at the
appropriate workflow step rather than a prerequisite for every chat turn.
Durable goals, approvals, actions, and negotiation state remain a separate
concern described in `AI_HIRING_AGENT_SPEC.md`.

## Remaining design and release questions

1. How should confirmed active constraints and superseded values be represented?
   Proposed: a server-built, per-field active-constraint snapshot with
   supporting user-turn evidence (see above).
2. How should assistant questions and user confirmations be linked without
   treating assistant-generated text as authoritative evidence? Proposed:
   assistant turns are interpretation context only; hard values still require
   user-authored evidence or a structured confirmation.
3. When a user refers to a previous candidate, should the system refresh that
   profile, repeat search, or ask for clarification?
4. Is the existing five-round shared budget sufficient for tool selection,
   category decisions, extraction, repair, and final answer generation?
5. How should the separate rollout flag and before/after cost comparison be
   evaluated without treating earlier eager-flow results as current evidence?

## Implementation tasks and sequencing

The follow-up context problem and on-demand search are separable. On-demand
search alone does not fix the reported "yes" behavior. The first
deliverable is Phase 1 below; follow-up repair and durable hiring-agent state
are explicitly out of scope for that deliverable.

### Phase 1 - On-demand search

1. **Extract the search orchestrator.** Move category resolution, filter
   extraction/validation, locked search arguments, and soft-preference context
   into a request-scoped component. (Completed)
2. **Define typed outcomes.** Return results, clarification, no-match, or
   unavailable; preserve existing deterministic safe responses. (Completed)
3. **Connect the tool loop.** Intercept `search_task_masters`, invoke the
   orchestrator, and attach results using the original tool-call ID. Keep
   retrieval through `ToolRegistry` and MCP. (Completed)
4. **Remove eager pre-loop search.** Keep catalog snapshot capture only;
   capture failure must not block ordinary conversation or unrelated tools.
   Ship this with tasks 1-3, not alone. (Completed)
5. **Update prompts and search tracking.** Remove assumptions about prefetched
   results; explicitly enforce one marketplace search per request initially.
   (Completed: `OnDemandSearchSystemPrompt`, `marketplaceSearchExecuted`, and
   the post-search reminder.)
6. **Align budgets and deadlines.** Account for tool selection, category
   resolution, extraction, and final answer within the same request budget.
   (Pending review: the current implementation already shares a five-round
   budget and deadline; sufficiency and exhaustion behavior need evaluation.)
7. **Add focused regression tests.** Cover greetings without category calls,
   validated searches, invented model filters being ignored, catalog failure
   isolation, and repeated search calls. (Completed: automated coverage in
   `CategoryFirstIntegrationTests` and updated service/adapter fixtures.)
8. **Gate rollout and compare costs.** Use a configuration flag; compare
   latency, model rounds, and token usage for search versus non-search requests.
   (Pending: rich-mode selection is not a separate on-demand rollout flag.)

### Phase 2 - Follow-up context

9. **Reproduce the reported conversation.** Establish the expected behavior
   for Los Angeles -> Houston suggestion -> "yes," including client-supplied
   history trust boundaries.
10. **Share one bounded context window.** Give the main model, category
    resolver, and filter extractor consistent conversational context.
11. **Implement active constraints and supersession.** Track user-evidenced
    values per field; later explicit values replace earlier ones.
12. **Handle ambiguous confirmations and candidate references.** Clarify
    unsupported confirmations; decide when a candidate reference requires
    profile refresh versus another search.

Phase 2 requires further behavioral decisions before implementation, especially
the confirmation evidence and candidate-reference questions listed above.

## Acceptance coverage

### Phase 1: automated coverage implemented

- A greeting or unrelated question does not invoke category resolution/search.
- A new provider search still validates category and explicit hard filters.
- Catalog failure blocks search without blocking unrelated conversation/tools.
- Model-supplied arguments cannot bypass category or constraint validation.
- Repeated search calls, including calls in the same batch and empty results,
  do not repeat retrieval; another request can search again.

### Phase 2: proposed, not verified

- A short confirmation is interpreted using the preceding assistant question.
- A confirmed location change replaces the old active location.
- An ambiguous confirmation produces a focused clarification, not a broad reset.
- Referring to a candidate does not create unsupported provider claims or an
  unnecessary new marketplace search.

## Related code and documents

- `ai-assistant-service\Services\AiAssistantService.cs`
- `ai-assistant-service\Services\Tools\SearchOrchestrator.cs`
- `ai-assistant-service\Services\Tools\ISearchOrchestrator.cs`
- `ai-assistant-service\Services\CategoryDecisionService.cs`
- `ai-assistant-service\Services\SearchFilterExtractionService.cs`
- `ai-assistant-service.Tests\CategoryFirstIntegrationTests.cs`
- [Matching pipeline contracts](TASKMASTER_MATCHING_PIPELINE_SPEC.md)
- [Category resolution contracts](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md)
- [Category/Jev checklist](TASKMASTER_CATEGORY_MANAGEMENT_TASKS.md)
- [Proposed multi-category search](TASKMASTER_MULTI_CATEGORY_SEARCH_SPEC.md)
- [Proposed hiring-agent state](AI_HIRING_AGENT_SPEC.md)
