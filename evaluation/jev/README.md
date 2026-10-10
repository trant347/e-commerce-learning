# JEV-08 offline fixture v1

**Operator reviewed for evaluation, not production policy/release.**
On October 3, 2026, the operator explicitly confirmed review and approval
of the 120 case labels and 36-category snapshot for evaluation. This is not
approval of production thresholds, live inference spending, or release.

`cases.v1.jsonl` contains 120 non-sensitive, hand-labeled single-category
decisions: 20 cases in each of six groups, split 10 tuning / 10 heldout per
group. `messages` are successive user-authored turns, in order; the last
entry is the prompt being evaluated. `expected.categoryId` is present only
for `Resolved`. `NotApplicable` means no marketplace service search is
requested, not an unsupported marketplace service. Clarification and no-match
cases must not trigger provider search.

`catalog.v1.json` freezes the **public evaluation metadata** used by this
fixture: 36 category IDs, display names, descriptions and empty aliases.
The explicit categories/aliases group exercises display names only. Alias
examples (including the spec's conditional `woodworking` example) require
review against a catalog with actual aliases before addition. **Zero alias
coverage** is a documented limitation of this evaluation fixture.

The snapshot was reconstructed offline from
`product-service/src/main/resources/seed/categories.json`, substituting the
two reported live descriptions below and adding empty aliases. Its SHA-256
digest **matches** the observed public live catalog metadata digest reported
on October 3, 2026:
`sha256:2268f76bdc93f84a1113a2b0efb1e6a9bc281d34e4756c253101e941f0208b62`.
The comparison uses the `CategoryCatalogService` rules: sort by category ID
and alias, serialize compact JSON with ordered fields
`id`, `displayName`, `description`, `aliases`, then SHA-256 the UTF-8 bytes.
The observed digest was **not independently confirmed from MCP**; digest
agreement is not independent proof of current live metadata. Reported live
`education` describes "General educational support and learning services."
and `tutoring` describes
"Individual academic instruction and test preparation.", unlike the seed.
Accordingly, no case labels language teaching as either category.

The approved labels include overlapping pairs requiring careful interpretation:
`carpentry`/`furniture-assembly`, `cleaning`/`organizing`,
`automotive`/`car-detailing`, `babysitting`/`childcare`,
`gardening`/`landscaping`, `fitness`/`personal-training`, and
`home-repair`/`home-improvement`. **Before any paid run**, verify that the
current product-owned catalog digest exactly matches the frozen digest above;
if it changes, re-freeze the snapshot and re-review labels before inference.
The model, policy, acceptance criteria and any further paid evaluation require
separate gates. No production-policy approval or release approval is implied.
JEV-08 is blocked; see the tuning status below.

Validate offline from the repository root with:

```text
python evaluation\jev\validate.py
python evaluation\jev\validate.py --catalog v2
```

## Catalog v2 (October 3, 2026): reviewed descriptions and aliases

`catalog.v2.json` freezes the revised product seed metadata after it was
applied live: every description now states included work and the boundary
with its overlapping neighbor, and 187 aliases (no alias shared between
categories or equal to any ID/display name) were added. Its digest, computed
from the live public metadata and identical to the seed file, is
`sha256:9ef188cab7be51a6c5a78667a1977c949ec727b491dc02ccb3f6dc164d961c6a`.
The serialized assistant catalog context is about 11,000 of the 12,000
allowed characters.

Metadata was written from general service knowledge and checked only for
consistency with **tuning-split** label conventions (for example,
one-to-one academic help is Tutoring, group learning is Education, a single
evening is Babysitting, ongoing care is Childcare). Held-out prompts were not
read. Alias wording does not copy fixture prompts. Live `education` now
includes language teaching, as the seed always did; no case labels language
teaching, so labels are unaffected.

**Operator re-review of the v2 snapshot is required** before any inference
against it. The operator reviewed and approved catalog v2 for evaluation on
October 3, 2026.

### v2 tuning results (October 3, 2026, tuning split only)

Same 60 tuning cases, three repeats each (180 runs per model), catalog v2.
Jev reported cost **$0.050513**; one key-usage check hit HTTP 429 and the
runner resumed without repeating completed runs. Raw results remain private.

| Tuning-only measure | Jev v1 | Jev v2 | Ollama v1 | Ollama v2 |
|---|---:|---:|---:|---:|
| Correct automatic selections (gate at least 90%) | 88/108 | **102/108 (94.4%)** | 108/108 | 108/108 |
| Correct safe abstentions (gate at least 95%) | 63/66 | **63/66 (95.5%)** | 9/66 | 17/66 |
| Incorrect forced selections (gate at most 5%) | 3/66 | **3/66 (4.5%)** | 48/66 | 49/66 |

Best v2 Jev grid candidate: confidence **0.0**, probability **0.0**, margin
**0.1** (tolerance 0.001); no wrong-category selections. This is the first
candidate to meet all three tuning gates, but the thresholds were selected on
these same tuning runs and abstention sits at the gate boundary. It is **not a
frozen policy or release result**: freezing a policy and a held-out run with
`catalog.v2.json` require separate operator approval. Ollama still forces a
category on most requests that need abstention.

### v2 held-out result with frozen policy (October 3, 2026): FAIL

Policy `policy.v2.json` (`jev-policy-v2-20261003`) was committed before any
held-out inference. All 60 held-out cases were then run three times
(180 runs, reported cost **$0.050516**) and scored only with that policy:

```text
python scripts\score_jev_heldout.py C:\private\jev-heldout-v2.jsonl --policy evaluation\jev\policy.v2.json
```

| Held-out measure | Result | Gate |
|---|---:|---|
| Correct automatic selections | 96/108 (88.9%) | **FAIL** (at least 90%) |
| Correct safe abstentions | 54/66 (81.8%) | **FAIL** (at least 95%) |
| Incorrect forced selections | 3/66 (4.5%) | PASS (at most 5%) |
| NotApplicable correct | 6/6 | not gated |

There were no wrong-category selections or unavailable results. Post-hoc
diagnosis (not a result): 21 of the 24 failing runs came from the **intent**
question answering `not_applicable` while the category question chose the
labelled category or correct abstention; the remaining 3 were one
overlapping-category case forced to a category. The catalog changes cannot
address the intent question. Because this diagnosis used held-out
outcomes, these held-out cases are now **consumed**: any intent-question or
policy change must be measured on new, independently written held-out cases,
not by rescoring this set. JEV-08 remains blocked and Jev stays disabled.

### v3: clarified intent question and fresh held-out cases (October 3, 2026): PASS

Commit `077cac0` rewrote the intent question:

- it reads the clarification history and current request as one conversation;
- booking or hiring a provider, and requests for services the marketplace may
  not offer, count as `service_search`;
- questions about an existing booking, payment or account, how-it-works
  questions and general chat count as `not_applicable`.

`cases.v2.jsonl` keeps the 60 v1 tuning cases and adds 60 fresh held-out
cases (`H11`-`H20`), written without access to the intent wording or v1
held-out prompts and reviewed by the operator. Validate it with
`python evaluation\jev\validate.py --catalog v2 --cases v2`.

The tuning rerun (180 runs, $0.050580) selected margin 0.65, the lowest value
on a 0.65-0.80 plateau, with 108/108 automatic, 65/66 abstention and 0/66
forced. Margins of 0.50 or lower fail the abstention gate. `policy.v3.json`
(`jev-policy-v3-20261003`) was committed in `1b512d3` before held-out
inference. Held-out (180 runs, $0.051165), scored only with that policy:

| Held-out measure | Result | Gate |
|---|---:|---|
| Correct automatic selections | 111/111 (100%) | PASS (at least 90%) |
| Correct safe abstentions | 60/60 (100%) | PASS (at least 95%) |
| Incorrect forced selections | 0/60 (0%) | PASS (at most 5%) |
| NotApplicable correct | 9/9 | not gated |

There were no wrong-category pairs, unavailable results or malformed held-out
decisions; one held-out run needed one repair. Two tuning runs were
`MalformedDecision` and are included in the tuning figures. Jev call latency
in the runner was p50 179 ms, p95 242 ms, max 351 ms.

Limitations: these are category-stage gates only. End-to-end latency and the
legacy-baseline comparison are not measured. With 60 held-out cases, a perfect
score still allows a true error rate of a few percent.

The offline validator checks that case IDs refer to this snapshot and that
the canonical digest matches both the frozen snapshot and reported digest;
it does not contact product-service or MCP.

## JEV-09 deterministic end-to-end verification (October 10, 2026)

Deterministic release-path coverage now verifies all four required scenarios
with stubbed model/network boundaries:

- a metadata-only catalog refresh changes the next Jev decision and locked
  search category without restarting the assistant or rebuilding an unchanged
  category enum;
- ambiguous and uncertain decisions execute no provider search;
- explicit hard filters survive a clarification reply and remain locked;
- provider answers, sources and mentions come only from the current validated
  search result.

The complete `ai-assistant-service.Tests` project passed 415/415 tests. The
frozen fixture passed
`python evaluation\jev\validate.py --catalog v2 --cases v2`. Base and
OpenRouter Compose rendering passed, and
`scripts\Test-AiAssistantDeployments.ps1` verified fake-secret startup
topologies and rollback without starting containers or calling models.

### Live end-to-end comparison (October 10, 2026): FAIL

An explicitly approved run used the 60 fresh v2 held-out cases, three repeats
per case, frozen catalog/policy, local `qwen3:8b`, and one deterministic
provider fixture per category. Raw request-level results and reports remain
private in the session workspace. The committed scorer contains no prompts or
provider response bodies.

The first full run exposed a real filter-extraction boundary: for requests with
no filters, `qwen3:8b` returned `{}` because the prompt did not state the exact
empty envelope. The prompt now requires
`{"hard":{},"soft":[],"unsupportedHard":[]}`; focused live and automated tests
passed before the comparison was rerun. The discarded pre-fix run is not part
of the measurements below.

| Held-out end-to-end measure | Legacy | Jev category-first | Gate |
|---|---:|---:|---|
| Correct automatic resolution | 85/111 (76.6%) | **108/111 (97.3%)** | Jev at least 90%: PASS |
| Correct abstention | 9/60 (15.0%) | **60/60 (100%)** | Jev at least 95%: PASS |
| Incorrect forced selection | 51/60 (85.0%) | **0/60 (0%)** | Jev at most 5%: PASS |
| Correct NotApplicable | 0/9 | **9/9** | Reported, not separately gated |
| Grounding violations | 19/180 | **0/180** | Zero: PASS |
| Search-safety violations | 51/69 | **0/69** | Zero: PASS |
| End-to-end p50 | 1.248 s | **1.658 s** | Reported |
| End-to-end p95 | 1.689 s | **2.424 s** | Jev at most 1.2x baseline: **FAIL (1.435x)** |
| Jev calls | 0 | 181 (one repair) | Reported |
| Measured candidate cost | $0 | $0.051165 | Within approved ceiling |

Three Jev runs for the same gardening request did not reach search after filter
extraction; they count as automatic-resolution misses. This does not change the
passing quality rate, but should be included in latency/flow diagnosis.

Total evaluation-key usage was $0.102899 because the discarded pre-fix run also
consumed $0.0517. This remained below the explicitly approved $0.50 ceiling.
No production opt-in or multi-category enablement occurred.

JEV-09 remains open. Reduce and remeasure warm end-to-end p95 without weakening
the passing quality, abstention, grounding or search-safety behavior. Keep
`CategoryResolution:Mode=legacy` and
`CategoryResolution:SemanticProvider=ChatModel` until the latency gate and
operator release decision pass.

## Measured tuning status (October 3, 2026)

An operator-approved dedicated key had a **$2 lifetime limit with no reset**
and a **$1.50 stop threshold**. The 180 tuning attempts were recorded locally
in the session folder, **not committed**; no raw results are included here.
Reported successful-response costs totaled **$0.026881**. The key endpoint
reported **$0.027478248**, including unreported malformed attempts. One prompt
initially received two malformed responses; resumed evaluation subsequently
produced a parsed result. This retry history is an **integrity caveat** when
interpreting the tuning outcomes, not an independent clean replicate.

The tuning-only grid selection (confidence **0.0**, probability **0.0**,
margin **0.3**, numeric tolerance **0.001**) gave **88/108 correct automatic
(81.5%)**, below the 90% category-accuracy gate; **63/66 correct abstentions
(95.5%)** and **3/66 forced selections (4.5%)**. No candidate in the tested
grid passed both the category-accuracy and abstention gates. These are tuning
measurements, **not held-out or release results**; do not freeze this policy.

**JEV-08 is blocked** pending review of metadata, fixture labels and/or the
model, followed by a new operator-approved evaluation. Review overlapping
category pairs above and explicit-category, paraphrase and clarification
follow-up misses in particular. Do not run held-out evaluation or claim release
from these tuning figures. If the catalog digest changes, re-freeze and
re-review before any further inference.

## Local Ollama comparison (October 3, 2026)

The same 60 approved tuning cases were run three times each (180 runs) with
the frozen catalog digest above. The local `qwen3:8b` comparison used the
existing `CategoryDecisionService`/`OllamaChatModelClient` path, with **no
API key or paid calls**. Compared with Jev's best safe tuning grid:

| Tuning-only measure | Jev | Local Ollama `qwen3:8b` |
|---|---:|---:|
| Correct automatic selections | 88/108 | 108/108 |
| Correct safe abstentions | 63/66 | 9/66 |
| Incorrect forced selections | 3/66 | 48/66 |

For the remaining nine cases requiring abstention, Ollama predicted
`NotApplicable`, which was not a correct abstention. Measured **category-stage** p50/p95
durations were approximately **165/235 ms** for Jev versus **383/2183 ms**
for Ollama. These are neither end-to-end latencies nor a controlled latency
study. Ollama classified answerable requests better in this tuning comparison
but was dramatically less safe on ambiguous or unsupported requests; **neither
approach meets both release gates**. No held-out evaluation or policy freeze
follows from these tuning-only results.

To recompute the local comparison from **private, uncommitted** result files
(offline analysis, no inference), run from the repository root:

```text
python scripts\analyze_jev_tuning.py C:\private\jev-tuning.jsonl --fixture evaluation\jev\cases.v1.jsonl --ollama-results C:\private\ollama-tuning.jsonl
```

## Paid runner (future approved tuning only)

`scripts\JevEvaluationRunner\JevEvaluationRunner.csproj` accepts six
positional arguments, in order: `tuning|heldout`, repeats (`1`–`3`),
`maxCases` (`1`–`60`), frozen catalog JSON path, cases JSONL path, and a
**private output JSONL path**. It requires `OPENROUTER_API_KEY` in the
process environment; never put the key or raw results in version control.
The following is **usage syntax, not authorization to run it now**:

```text
dotnet run --project scripts\JevEvaluationRunner\JevEvaluationRunner.csproj -- tuning 3 60 evaluation\jev\catalog.v1.json evaluation\jev\cases.v1.jsonl C:\private\jev-tuning.jsonl
```

Before a paid run, validate the frozen snapshot and confirm the current
product-owned catalog digest matches it. The runner checks the local live
public catalog against the snapshot before paid calls. It also checks key
usage before and after requests: the dedicated key must have **exactly $2
lifetime limit, no reset**, and spending stops at **$1.50**. It makes paid
calls serially, permits one repair of a malformed decision, and resumes by
skipping completed case/repeat pairs already recorded in the output. Keep
that output private and uncommitted. **Do not run `heldout` while the tuning
accuracy gate fails.** Any further paid run requires new operator approval;
the documented tuning shortfall does not freeze a production policy.

## Offline scorer input (measured results only)

`scripts\score_jev_evaluation.py` reads one JSON object per line from a
**measured-results JSONL** file. Each row has:

| Field | Value |
|---|---|
| `id` | Case ID from `cases.v1.jsonl` |
| `mode` | `legacy` (baseline) or `catalog-llm-jev` (candidate) |
| `repeat` | Integer `1`, `2`, or `3` |
| `outcome` | `Resolved`, `NeedsClarification`, `NoMatch`, or `NotApplicable` |
| `categoryId` | Frozen catalog ID, present **iff** outcome is `Resolved` |
| `catalogVersion` | Pinned catalog digest, identical across compared modes |
| `policyVersion` | Reviewed policy version, required for candidate rows |
| `durationMs` | Nonnegative finite measured duration in milliseconds |
| `costUsd` | Nonnegative finite measured USD cost |
| `modelCalls` | Nonnegative integer count of measured model calls |
| `method` | `exact`, `llm`, `jev`, or `none` |

Supply all 120 case IDs for each mode and each repeat (720 result rows).
From the repository root, once measured results and a reviewed policy exist:

```text
python scripts\score_jev_evaluation.py evaluation\jev\measured-results.jsonl --fixture evaluation\jev\cases.v1.jsonl --catalog-version sha256:2268f76bdc93f84a1113a2b0efb1e6a9bc281d34e4756c253101e941f0208b62 --policy-version REVIEWED_POLICY_VERSION --baseline legacy --candidate catalog-llm-jev
```

The command only scores input rows; it makes no model requests. A report
generated from fabricated results **does not count as live evaluation**.
The operator approved this fixture's cases and catalog labels for evaluation
on October 3, 2026; that approval alone does not authorize further paid runs.
