"""Score held-out Jev results with a frozen policy file; never selects thresholds."""

import argparse
from collections import Counter
import json
from pathlib import Path

from analyze_jev_tuning import decision


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--policy", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, default=Path("evaluation/jev/cases.v1.jsonl"))
    args = parser.parse_args()

    policy = json.loads(args.policy.read_text(encoding="utf-8"))
    cases = {
        item["id"]: item
        for item in (json.loads(line) for line in args.fixture.read_text(encoding="utf-8").splitlines())
        if item["split"] == "heldout"
    }
    rows = [json.loads(line) for line in args.results.read_text(encoding="utf-8").splitlines()]
    keys = [(row["id"], row["repeat"]) for row in rows]
    if len(cases) != 60 or len(rows) != 180 or set(keys) != {
        (case_id, repeat) for case_id in cases for repeat in (1, 2, 3)
    }:
        parser.error("Expected each of the 60 held-out cases measured exactly three times.")
    if {row["catalogVersion"] for row in rows} != {policy["evaluatedCatalogVersion"]}:
        parser.error("Results do not use the policy's evaluated catalog version.")
    if {row["model"] for row in rows} != {policy["resolvedModel"]}:
        parser.error("Results do not use the policy's resolved model.")

    thresholds = policy["minimumConfidence"], policy["minimumProbability"], policy["minimumMargin"]
    outcomes = [(row, cases[row["id"]], decision(row, *thresholds)) for row in rows]
    answerable = [item for item in outcomes if item[1]["expected"]["outcome"] == "Resolved"]
    abstain = [item for item in outcomes if item[1]["expected"]["outcome"] in ("NeedsClarification", "NoMatch")]
    not_applicable = [item for item in outcomes if item[1]["expected"]["outcome"] == "NotApplicable"]

    automatic = sum(result == ("Resolved", case["expected"]["categoryId"]) for _, case, result in answerable)
    correct_abstentions = sum(result[0] in ("NeedsClarification", "NoMatch") for _, _, result in abstain)
    forced = sum(result[0] == "Resolved" for _, _, result in abstain)
    not_applicable_correct = sum(result[0] == "NotApplicable" for _, _, result in not_applicable)
    unavailable = sum(result[0] == "Unavailable" for _, _, result in outcomes)
    wrong = Counter(
        (case["expected"]["categoryId"], result[1])
        for _, case, result in answerable
        if result[0] == "Resolved" and result[1] != case["expected"]["categoryId"]
    )
    misses = Counter(case["group"] for _, case, result in answerable
                     if result != ("Resolved", case["expected"]["categoryId"]))
    misses.update(case["group"] for _, case, result in abstain if result[0] not in ("NeedsClarification", "NoMatch"))

    gates = [
        ("correct automatic >= 90%", automatic / len(answerable) >= 0.90, automatic, len(answerable)),
        ("correct abstention >= 95%", correct_abstentions / len(abstain) >= 0.95, correct_abstentions, len(abstain)),
        ("incorrect forced <= 5%", forced / len(abstain) <= 0.05, forced, len(abstain)),
    ]
    print(f"Policy {policy['version']}; catalog {policy['evaluatedCatalogVersion']}")
    print(f"Held-out runs: {len(rows)}; reported provider cost: ${sum(row['costUsd'] or 0 for row in rows):.6f}")
    for name, passed, numerator, denominator in gates:
        print(f"{'PASS' if passed else 'FAIL'} {name}: {numerator}/{denominator} ({numerator / denominator:.1%})")
    print(f"NotApplicable correct: {not_applicable_correct}/{len(not_applicable)}; unavailable: {unavailable}")
    print(f"Wrong-category pairs: {wrong.most_common(10)}")
    print(f"Misses by group: {dict(misses)}")
    print("Category-stage gates only; end-to-end latency and baseline comparison are not measured here.")
    return 0 if all(passed for _, passed, _, _ in gates) else 1


if __name__ == "__main__":
    raise SystemExit(main())
