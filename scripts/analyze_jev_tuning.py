"""Select candidate Jev thresholds using tuning cases only, never held-out labels."""

import argparse
from collections import Counter
import json
from pathlib import Path


def decision(row, confidence, probability, margin):
    if row.get("failure"):
        return "Unavailable", None

    def accepted(answer):
        return (
            answer["Confidence"] >= confidence
            and answer["ChosenProbability"] >= probability
            and answer["ChosenProbability"] - answer["RunnerUpProbability"] >= margin
        )

    intent = row["intent"]
    category = row["category"]
    if not accepted(intent):
        return "NeedsClarification", None
    if intent["Choice"] == "not_applicable":
        return "NotApplicable", None
    if intent["Choice"] != "service_search" or not accepted(category):
        return "NeedsClarification", None
    if category["Choice"] == "decision:needs_clarification":
        return "NeedsClarification", None
    if category["Choice"] == "decision:no_match":
        return "NoMatch", None
    return "Resolved", category["Choice"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--fixture", type=Path, default=Path("evaluation/jev/cases.v1.jsonl"))
    parser.add_argument("--ollama-results", type=Path, help="Optional same-fixture Ollama comparison")
    args = parser.parse_args()
    cases = {
        item["id"]: item
        for item in (json.loads(line) for line in args.fixture.read_text(encoding="utf-8").splitlines())
        if item["split"] == "tuning"
    }
    rows = [json.loads(line) for line in args.results.read_text(encoding="utf-8").splitlines()]
    if len(cases) != 60 or len(rows) != 180:
        parser.error("Expected 60 tuning cases, each measured three times.")
    keys = [(row["id"], row["repeat"]) for row in rows]
    if set(keys) != {(case_id, repeat) for case_id in cases for repeat in (1, 2, 3)}:
        parser.error("Duplicate, missing or non-tuning results.")
    versions = {row["catalogVersion"] for row in rows}
    if len(versions) != 1:
        parser.error("Results span more than one catalog version.")

    unambiguous = [row for row in rows if cases[row["id"]]["expected"]["outcome"] == "Resolved"]
    abstention = [
        row for row in rows
        if cases[row["id"]]["expected"]["outcome"] in ("NeedsClarification", "NoMatch")
    ]
    eligible = []
    for c in range(21):
        for p in range(21):
            for m in range(21):
                thresholds = c / 20, p / 20, m / 20
                results = {key: decision(row, *thresholds) for key, row in zip(keys, rows)}
                automatic = sum(
                    results[(row["id"], row["repeat"])]
                    == ("Resolved", cases[row["id"]]["expected"]["categoryId"])
                    for row in unambiguous
                )
                correct_abstentions = sum(
                    results[(row["id"], row["repeat"])][0]
                    in ("NeedsClarification", "NoMatch")
                    for row in abstention
                )
                forced = sum(
                    results[(row["id"], row["repeat"])][0] == "Resolved"
                    for row in abstention
                )
                if correct_abstentions / len(abstention) >= 0.95 and forced / len(abstention) <= 0.05:
                    eligible.append((automatic, correct_abstentions, -forced, thresholds))

    print(f"Tuning catalog: {next(iter(versions))}")
    print(f"Measured runs: {len(rows)}; reported provider cost: ${sum(row['costUsd'] or 0 for row in rows):.6f}")
    print(f"Unambiguous runs: {len(unambiguous)}; abstention runs: {len(abstention)}")
    if not eligible:
        print("No threshold triplet in the predeclared 0.05 grid satisfies the abstention gates.")
        return 1
    automatic, correct_abstentions, negative_forced, thresholds = max(
        eligible, key=lambda candidate: candidate[:3] + tuple(-value for value in candidate[3])
    )
    print(f"Candidate tuning thresholds (confidence, probability, margin): {thresholds}")
    print(
        f"Tuning correct automatic: {automatic}/{len(unambiguous)}; "
        f"correct abstention: {correct_abstentions}/{len(abstention)}; "
        f"incorrect forced: {-negative_forced}/{len(abstention)}"
    )
    confusion = Counter(
        (cases[row["id"]]["expected"]["categoryId"], decision(row, *thresholds)[1])
        for row in unambiguous
        if decision(row, *thresholds)[0] == "Resolved"
        and decision(row, *thresholds)[1] != cases[row["id"]]["expected"]["categoryId"]
    )
    print(f"Wrong-category confusion pairs: {confusion.most_common(10)}")
    if args.ollama_results is not None:
        ollama = [
            json.loads(line)
            for line in args.ollama_results.read_text(encoding="utf-8").splitlines()
        ]
        ollama_keys = [(row["id"], row["repeat"]) for row in ollama]
        if (len(ollama) != 180
            or set(ollama_keys) != set(keys)
            or {row["catalogVersion"] for row in ollama} != versions
            or {row["model"] for row in ollama} != {"qwen3:8b"}):
            parser.error("Ollama comparison must use the same 180 tuning runs, catalog and model.")
        ollama_automatic = sum(
            row["outcome"] == "Resolved"
            and row["categoryId"] == cases[row["id"]]["expected"]["categoryId"]
            for row in ollama
            if cases[row["id"]]["expected"]["outcome"] == "Resolved"
        )
        ollama_abstention = sum(
            row["outcome"] in ("NeedsClarification", "NoMatch")
            for row in ollama
            if cases[row["id"]]["expected"]["outcome"] in ("NeedsClarification", "NoMatch")
        )
        ollama_forced = sum(
            row["outcome"] == "Resolved"
            for row in ollama
            if cases[row["id"]]["expected"]["outcome"] in ("NeedsClarification", "NoMatch")
        )
        print(
            f"Ollama qwen3:8b: automatic {ollama_automatic}/{len(unambiguous)}; "
            f"correct abstention {ollama_abstention}/{len(abstention)}; "
            f"incorrect forced {ollama_forced}/{len(abstention)}"
        )
        print(
            "Tuning only: Ollama has no confidence gate here; a correct category "
            "on answerable requests does not excuse forced selections."
        )
    print("Tuning only: this is NOT a frozen production policy or held-out release result.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
