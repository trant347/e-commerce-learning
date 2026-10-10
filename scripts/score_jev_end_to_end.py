"""Score the private JEV-09 held-out end-to-end comparison without model calls."""

import argparse
from collections import Counter
import json
import math
from pathlib import Path


MODES = ("legacy", "catalog-llm-jev")


def read_jsonl(path):
    with path.open(encoding="utf-8-sig") as source:
        for line_number, line in enumerate(source, 1):
            if not line.strip():
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"{path}:{line_number}: invalid JSON") from error


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(math.ceil(len(ordered) * fraction) - 1, 0)]


def correct_resolution(case, row):
    expected = case["expected"]
    return (
        row["outcome"] == expected["outcome"]
        and (
            row["outcome"] != "Resolved"
            or row["categoryId"] == expected["categoryId"]
        )
    )


def grounded(row):
    if row["searchCalls"] == 0:
        return row["sourceCount"] == 0 and row["mentionCount"] == 0
    return (
        row["searchCalls"] == 1
        and row["sourceCount"] == 1
        and row["mentionCount"] == 1
    )


def summarize(cases, rows):
    unambiguous = [
        row for row in rows
        if cases[row["id"]]["expected"]["outcome"] == "Resolved"
    ]
    abstention = [
        row for row in rows
        if cases[row["id"]]["expected"]["outcome"]
        in ("NeedsClarification", "NoMatch")
    ]
    not_applicable = [
        row for row in rows
        if cases[row["id"]]["expected"]["outcome"] == "NotApplicable"
    ]
    confusion = Counter(
        (
            cases[row["id"]]["expected"]["categoryId"],
            row.get("categoryId"),
        )
        for row in unambiguous
        if not correct_resolution(cases[row["id"]], row)
    )
    durations = [row["durationMs"] for row in rows]
    return {
        "runs": len(rows),
        "correctAutomatic": sum(
            correct_resolution(cases[row["id"]], row) for row in unambiguous
        ),
        "unambiguousRuns": len(unambiguous),
        "correctAutomaticRate": sum(
            correct_resolution(cases[row["id"]], row) for row in unambiguous
        ) / len(unambiguous),
        "correctAbstention": sum(
            row["outcome"] in ("NeedsClarification", "NoMatch")
            for row in abstention
        ),
        "abstentionRuns": len(abstention),
        "correctAbstentionRate": sum(
            row["outcome"] in ("NeedsClarification", "NoMatch")
            for row in abstention
        ) / len(abstention),
        "incorrectForcedSelections": sum(
            row["outcome"] == "Resolved" for row in abstention
        ),
        "incorrectForcedSelectionRate": sum(
            row["outcome"] == "Resolved" for row in abstention
        ) / len(abstention),
        "correctNotApplicable": sum(
            row["outcome"] == "NotApplicable" for row in not_applicable
        ),
        "notApplicableRuns": len(not_applicable),
        "groundedRuns": sum(grounded(row) for row in rows),
        "groundingViolations": sum(not grounded(row) for row in rows),
        "searchSafetyViolations": sum(
            row["searchCalls"] != 0
            for row in abstention + not_applicable
        ),
        "resolvedWithoutSearch": sum(
            row["searchCalls"] == 0 for row in unambiguous
        ),
        "latencyP50Ms": percentile(durations, 0.50),
        "latencyP95Ms": percentile(durations, 0.95),
        "costUsd": round(sum(row["costUsd"] for row in rows), 8),
        "modelCalls": sum(row["modelCalls"] for row in rows),
        "ollamaCalls": sum(row["ollamaCalls"] for row in rows),
        "jevCalls": sum(row["jevCalls"] for row in rows),
        "searchCalls": sum(row["searchCalls"] for row in rows),
        "confusionPairs": [
            {"expected": expected, "selected": selected, "count": count}
            for (expected, selected), count in confusion.most_common()
        ],
    }


def score(fixture_path, result_path):
    cases = {
        row["id"]: row
        for row in read_jsonl(fixture_path)
        if row["split"] == "heldout"
    }
    if len(cases) != 60:
        raise ValueError(f"Expected 60 held-out cases; found {len(cases)}.")

    predictions = {}
    catalog_version = None
    policy_version = None
    for row in read_jsonl(result_path):
        key = (row["mode"], row["id"], row["repeat"])
        if row["mode"] not in MODES or row["id"] not in cases:
            raise ValueError(f"Unknown result key: {key}.")
        if row["repeat"] not in (1, 2, 3) or key in predictions:
            raise ValueError(f"Invalid or duplicate result key: {key}.")
        catalog_version = catalog_version or row["catalogVersion"]
        policy_version = policy_version or row["policyVersion"]
        if row["catalogVersion"] != catalog_version:
            raise ValueError(f"Catalog version mismatch: {key}.")
        if row["policyVersion"] != policy_version:
            raise ValueError(f"Policy version mismatch: {key}.")
        predictions[key] = row

    expected_count = len(cases) * 3
    by_mode = {}
    for mode in MODES:
        rows = [
            predictions[(mode, case_id, repeat)]
            for case_id in cases
            for repeat in (1, 2, 3)
            if (mode, case_id, repeat) in predictions
        ]
        if len(rows) != expected_count:
            raise ValueError(
                f"{mode}: expected {expected_count} results; found {len(rows)}."
            )
        by_mode[mode] = summarize(cases, rows)

    baseline = by_mode["legacy"]
    candidate = by_mode["catalog-llm-jev"]
    candidate["latencyRatioP95"] = (
        candidate["latencyP95Ms"] / baseline["latencyP95Ms"]
    )
    candidate["measuredGates"] = {
        "correctAutomaticAtLeast90Percent":
            candidate["correctAutomaticRate"] >= 0.90,
        "correctAbstentionAtLeast95Percent":
            candidate["correctAbstentionRate"] >= 0.95,
        "incorrectForcedSelectionAtMost5Percent":
            candidate["incorrectForcedSelectionRate"] <= 0.05,
        "zeroGroundingViolations":
            candidate["groundingViolations"] == 0,
        "zeroSearchSafetyViolations":
            candidate["searchSafetyViolations"] == 0,
        "latencyP95AtMost120PercentOfBaseline":
            candidate["latencyRatioP95"] <= 1.20,
    }
    return {
        "catalogVersion": catalog_version,
        "policyVersion": policy_version,
        "legacy": baseline,
        "catalog-llm-jev": candidate,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument(
        "--fixture",
        type=Path,
        default=Path("evaluation/jev/cases.v2.jsonl"),
    )
    args = parser.parse_args()
    try:
        report = score(args.fixture, args.results)
    except (KeyError, OSError, TypeError, ValueError, ZeroDivisionError) as error:
        parser.error(str(error))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
