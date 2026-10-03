"""Offline structural validation for the REVIEW-PENDING JEV-08 v1 fixture."""

import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path


GROUPS = {
    "explicit_categories_aliases": "EXP",
    "paraphrases": "PAR",
    "overlapping_categories": "OVL",
    "negation_multiple_services": "NEG",
    "unsupported_requests": "UNS",
    "clarification_followups": "FOL",
}
SPLITS = {"tuning": "T", "heldout": "H"}
OUTCOMES = {"Resolved", "NeedsClarification", "NoMatch", "NotApplicable"}
HERE = Path(__file__).resolve().parent
OBSERVED_DIGEST = "sha256:2268f76bdc93f84a1113a2b0efb1e6a9bc281d34e4756c253101e941f0208b62"


def normalized(text):
    return " ".join(text.casefold().split())


def validate():
    snapshot = json.loads((HERE / "catalog.v1.json").read_text(encoding="utf-8"))
    catalog = snapshot["categories"]
    errors = []
    if set(snapshot) != {"schemaVersion", "catalogVersion", "categories"} or snapshot["schemaVersion"] != 1:
        errors.append("catalog: expected schemaVersion 1 and only public snapshot fields")
    if len(catalog) != 36:
        errors.append(f"catalog: expected 36 categories, found {len(catalog)}")
    if any(set(entry) != {"id", "displayName", "description", "aliases"} for entry in catalog):
        errors.append("catalog: expected only id, displayName, description and aliases")
    if any(entry["aliases"] != [] for entry in catalog):
        errors.append("catalog: expected empty aliases")
    if [entry["id"] for entry in catalog] != sorted({entry["id"] for entry in catalog}):
        errors.append("catalog: IDs must be unique and sorted")
    names = {entry["id"]: entry["displayName"] for entry in catalog}
    canonical = [
        {
            "id": entry["id"],
            "displayName": entry["displayName"],
            "description": entry["description"],
            "aliases": sorted(entry["aliases"]),
        }
        for entry in sorted(catalog, key=lambda entry: entry["id"])
    ]
    digest = "sha256:" + hashlib.sha256(
        json.dumps(canonical, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    if digest != snapshot["catalogVersion"] or digest != OBSERVED_DIGEST:
        errors.append(f"catalog: computed {digest} does not match frozen and observed digests")
    counts = Counter()
    ids = set()
    prompts = set()

    for line_number, line in enumerate((HERE / "cases.v1.jsonl").read_text(encoding="utf-8").splitlines(), 1):
        try:
            case = json.loads(line)
        except json.JSONDecodeError as exc:
            errors.append(f"line {line_number}: invalid JSON: {exc}")
            continue
        if not isinstance(case, dict):
            errors.append(f"line {line_number}: case must be an object")
            continue
        case_id = case.get("id")
        group = case.get("group")
        split = case.get("split")
        messages = case.get("messages")
        expected = case.get("expected")
        if set(case) != {"id", "group", "split", "messages", "expected"}:
            errors.append(f"line {line_number}: unexpected or missing case fields")
        if not isinstance(case_id, str) or not case_id:
            errors.append(f"line {line_number}: missing ID")
        elif case_id in ids:
            errors.append(f"line {line_number}: duplicate ID {case_id}")
        else:
            ids.add(case_id)
        if group not in GROUPS or split not in SPLITS:
            errors.append(f"line {line_number}: invalid group or split")
        else:
            counts[group, split] += 1
            if not re.fullmatch(rf"JEV08-{GROUPS[group]}-{SPLITS[split]}\d{{2}}", case_id or ""):
                errors.append(f"line {line_number}: ID does not match group and split")
        if not isinstance(messages, list) or not messages or any(
            not isinstance(message, str) or not message.strip() for message in messages
        ):
            errors.append(f"line {line_number}: messages must be nonempty user turns")
        else:
            if len(messages) != (2 if group == "clarification_followups" else 1):
                errors.append(f"line {line_number}: unexpected number of user turns")
            prompt = normalized(messages[-1])
            if prompt in prompts:
                errors.append(f"line {line_number}: duplicate final prompt")
            prompts.add(prompt)
        if not isinstance(expected, dict) or expected.get("outcome") not in OUTCOMES:
            errors.append(f"line {line_number}: invalid expected outcome")
            continue
        if expected["outcome"] == "Resolved":
            category_id = expected.get("categoryId")
            if set(expected) != {"outcome", "categoryId"} or category_id not in names:
                errors.append(f"line {line_number}: Resolved requires one frozen catalog ID")
            elif group == "explicit_categories_aliases" and isinstance(messages, list) and messages and isinstance(
                messages[-1], str
            ) and normalized(names[category_id]) not in normalized(messages[-1]):
                errors.append(f"line {line_number}: explicit case must use frozen display name")
        elif set(expected) != {"outcome"}:
            errors.append(f"line {line_number}: abstention must not include category ID")

    if sum(counts.values()) != 120:
        errors.append(f"expected 120 cases, found {sum(counts.values())}")
    for group in GROUPS:
        for split in SPLITS:
            if counts[group, split] != 10:
                errors.append(f"{group}/{split}: expected 10, found {counts[group, split]}")
    return errors


if __name__ == "__main__":
    problems = validate()
    if problems:
        print("\n".join(problems), file=sys.stderr)
        sys.exit(1)
    print("JEV-08 v1 fixture valid: 120 cases; 20 per group; 10 per split per group; frozen catalog digest verified.")
