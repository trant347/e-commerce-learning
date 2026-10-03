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
```

The offline validator checks that case IDs refer to this snapshot and that
the canonical digest matches both the frozen snapshot and reported digest;
it does not contact product-service or MCP.

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
