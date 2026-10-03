"""Score a complete, three-repeat JEV-08 evaluation without making model calls."""

import argparse
from collections import Counter
import json
import math
from pathlib import Path


def read_jsonl(path):
    with path.open(encoding="utf-8") as source:
        for line_number, line in enumerate(source, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except json.JSONDecodeError as error:
                    raise ValueError(f"{path}:{line_number}: invalid JSON") from error


def percentile(values, fraction):
    ordered = sorted(values)
    index = math.ceil(len(ordered) * fraction) - 1
    return ordered[max(index, 0)]


def summarize(cases, rows):
    heldout = [row for row in rows if cases[row["id"]]["split"] == "heldout"]
    unambiguous = [
        row for row in heldout
        if cases[row["id"]]["expected"]["outcome"] == "Resolved"
    ]
    abstention = [
        row for row in heldout
        if cases[row["id"]]["expected"]["outcome"] in ("NeedsClarification", "NoMatch")
    ]
    correct = lambda row: (
        row["outcome"] == cases[row["id"]]["expected"]["outcome"]
        and (
            row["outcome"] != "Resolved"
            or row["categoryId"] == cases[row["id"]]["expected"]["categoryId"]
        )
    )
    confusion = Counter(
        (cases[row["id"]]["expected"]["categoryId"], row["categoryId"])
        for row in unambiguous
        if row["outcome"] == "Resolved" and not correct(row)
    )
    durations = [row["durationMs"] for row in heldout]
    return {
        "heldoutRuns": len(heldout),
        "unambiguousRuns": len(unambiguous),
        "correctAutomaticRate": sum(map(correct, unambiguous)) / len(unambiguous),
        "abstentionRuns": len(abstention),
        "correctAbstentionRate": sum(
            row["outcome"] in ("NeedsClarification", "NoMatch") for row in abstention
        ) / len(abstention),
        "incorrectForcedSelectionRate": sum(
            row["outcome"] == "Resolved" for row in abstention
        ) / len(abstention),
        "noMatchRate": sum(row["outcome"] == "NoMatch" for row in heldout) / len(heldout),
        "clarificationRate": sum(
            row["outcome"] == "NeedsClarification" for row in heldout
        ) / len(heldout),
        "exactMethodRate": sum(row["method"] == "exact" for row in heldout) / len(heldout),
        "confusionPairs": [
            {"expected": expected, "selected": selected, "count": count}
            for (expected, selected), count in confusion.most_common()
        ],
        "latencyP50Ms": percentile(durations, 0.5),
        "latencyP95Ms": percentile(durations, 0.95),
        "costUsd": round(sum(row["costUsd"] for row in rows), 8),
        "modelCalls": sum(row["modelCalls"] for row in rows),
    }


def score(fixture, results, catalog, policy, baseline, candidate):
    cases = {row["id"]: row for row in read_jsonl(fixture)}
    if len(cases) != 120:
        raise ValueError("The fixture must contain 120 unique cases.")
    if not any(case["split"] == "heldout" for case in cases.values()):
        raise ValueError("The fixture must include held-out cases.")
    if not any(case["split"] == "tuning" for case in cases.values()):
        raise ValueError("The fixture must include tuning cases.")

    predictions = {}
    for row in read_jsonl(results):
        case_id = row["id"]
        mode = row["mode"]
        repeat = row["repeat"]
        if case_id not in cases or mode not in (baseline, candidate) or repeat not in (1, 2, 3):
            raise ValueError(f"Unknown case, mode or repeat in evaluation result: {case_id}.")
        key = (mode, case_id, repeat)
        if key in predictions:
            raise ValueError(f"Duplicate result: {key}.")
        if row["catalogVersion"] != catalog:
            raise ValueError(f"Catalog version mismatch: {key}.")
        if mode == candidate and row["policyVersion"] != policy:
            raise ValueError(f"Policy version mismatch: {key}.")
        outcome = row["outcome"]
        if outcome not in ("Resolved", "NeedsClarification", "NoMatch", "NotApplicable"):
            raise ValueError(f"Invalid outcome: {key}.")
        category = row.get("categoryId")
        if (outcome == "Resolved") != (isinstance(category, str) and bool(category)):
            raise ValueError(f"Category ID must occur exactly when resolved: {key}.")
        for field in ("durationMs", "costUsd", "modelCalls"):
            value = row[field]
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                raise ValueError(f"Invalid {field}: {key}.")
            if not math.isfinite(value) or value < 0:
                raise ValueError(f"Invalid {field}: {key}.")
        if not isinstance(row["modelCalls"], int):
            raise ValueError(f"Invalid modelCalls: {key}.")
        if row["method"] not in ("exact", "llm", "jev", "none"):
            raise ValueError(f"Invalid method: {key}.")
        predictions[key] = row

    expected_count = len(cases) * 3
    by_mode = {
        mode: [
            predictions[(mode, case_id, repeat)]
            for case_id in cases
            for repeat in (1, 2, 3)
            if (mode, case_id, repeat) in predictions
        ]
        for mode in (baseline, candidate)
    }
    for mode, rows in by_mode.items():
        if len(rows) != expected_count:
            raise ValueError(f"{mode}: expected {expected_count} results; found {len(rows)}.")

    baseline_metrics = summarize(cases, by_mode[baseline])
    candidate_metrics = summarize(cases, by_mode[candidate])
    candidate_metrics["measuredGates"] = {
        "correctAutomaticAtLeast90Percent":
            candidate_metrics["correctAutomaticRate"] >= 0.90,
        "correctAbstentionAtLeast95Percent":
            candidate_metrics["correctAbstentionRate"] >= 0.95,
        "incorrectForcedSelectionAtMost5Percent":
            candidate_metrics["incorrectForcedSelectionRate"] <= 0.05,
        "latencyP95AtMost120PercentOfBaseline":
            candidate_metrics["latencyP95Ms"] <= baseline_metrics["latencyP95Ms"] * 1.2,
    }
    return {
        "catalogVersion": catalog,
        "policyVersion": policy,
        "note": "Measured category/latency gates only; filter, grounding, contracts, and approval gates are separate.",
        baseline: baseline_metrics,
        candidate: candidate_metrics,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, help="Measured JSONL results, never model prompts")
    parser.add_argument("--fixture", type=Path, default=Path("evaluation/jev/cases.v1.jsonl"))
    parser.add_argument("--catalog-version", required=True)
    parser.add_argument("--policy-version", required=True)
    parser.add_argument("--baseline", default="legacy")
    parser.add_argument("--candidate", default="catalog-llm-jev")
    args = parser.parse_args()
    if args.baseline == args.candidate:
        parser.error("Baseline and candidate must differ.")
    try:
        report = score(
            args.fixture, args.results, args.catalog_version, args.policy_version,
            args.baseline, args.candidate,
        )
    except (ValueError, KeyError, TypeError, OSError, ZeroDivisionError) as error:
        parser.error(str(error))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
