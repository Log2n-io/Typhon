#!/usr/bin/env python3
"""
audit-rule-coverage.py — cross-check `rules/*.md` against the tests that claim to verify them.

WHY (issue #703 Part 2 / design `Infrastructure/test-strategy.md` §5.5): a rule whose verifier cannot fail is
WORSE than a rule with no verifier, because it reports confidence. `VerifiesRuleAttribute.cs` has declared this
audit "not yet built" since it was written, and in the meantime the two views of coverage drifted apart:

    rules carrying a `verified:` field .................. 18
    rule ids named by a [VerifiesRule] test ............ 15
    nothing compared the two

Neither number was checked against reality either. LOG-06's cited verifiers hand-construct their own wire bytes
and round-trip them, so they are green in the same build as a red production probe (#389); and
`WalRecoveryTests.Recover_CommittedUoW_PromotesCorrectly` drives `WalRecovery.cs`, which has zero production
callers — a test of unreachable code, cited as evidence.

Checks:

    DUPLICATE_RULE_ID       one id defined by two blocks. `rules/README.md` requires ids unique across the whole
                            database, not per file, because any tool indexing by id silently conflates them.
    UNKNOWN_RULE_ID         [VerifiesRule("X")] where X is not a rule — a verifier pointing at nothing.
    DANGLING_VERIFIED       a `verified:` naming a test that does not exist (drift after a rename/deletion).
    VERIFIER_WITHOUT_MUTANT a rule verified by a test that ships no [RuleMutant] — nothing shows it can fail.

Ratchet: coverage counts are compared against `coverage/rule-coverage-baseline.json` and may not DECREASE. The
baseline is data, not a target; it exists so a verifier cannot be quietly deleted.

Usage:
    python3 scripts/audit-rule-coverage.py                       # audit + ratchet
    python3 scripts/audit-rule-coverage.py --update-baseline     # accept the current counts

`rules/` is in this repository (#747), so the audit always has something to audit. It used to carry a
`--skip-if-no-rules` escape hatch for fork PRs, where GitHub withholds the secret needed to check out the
private knowledge base and the rules simply were not there. A gate that can decline to run is one a reader
has to check the logs to trust; that hatch is gone, and the audit now fails if `rules/` is missing.

Exit code: 0 clean · 1 violation or ratchet regression · 2 usage error (incl. a missing rules dir).
"""
import argparse
import json
import os
import re
import sys

# A rule id is PREFIX-NN with an OPTIONAL lowercase letter suffix: `ED-05a`..`ED-05f` are distinct SUB-RULES of
# ED-05, not restatements of it. Omitting the suffix from this grammar truncates all six to "ED-05" and reports a
# 7-way duplicate that does not exist — the audit's own first false positive, and the reason the parser is tested.
RULE_ID = r"[A-Z][A-Z0-9]*-[0-9]+[a-z]?"

RULE_HEADING = re.compile(rf"^###\s+({RULE_ID})\s*:?\s*(.*)$")
SECTION_HEADING = re.compile(r"^##\s+")
FIELD = re.compile(r"^\s{2,}([a-z_]+):\s*(.*)$")
SEVERITY = re.compile(r"\[(fatal|silent|perf|UNBUILT)\]")

VERIFIES_RULE = re.compile(rf"\[VerifiesRule\s*\(\s*\"({RULE_ID})\"\s*\)\s*\]")
RULE_MUTANT = re.compile(rf"\[RuleMutant\s*\(\s*\"({RULE_ID})\"")
CS_CLASS = re.compile(r"\bclass\s+([A-Za-z0-9_]+)")
CS_METHOD = re.compile(r"\b(?:public|internal|private|protected)\s+(?:static\s+|async\s+|unsafe\s+)*"
                       r"(?:void|Task|[A-Za-z0-9_<>\[\],\. ]+?)\s+([A-Za-z0-9_]+)\s*\(")

# Identifier-shaped tokens inside a `verified:` line. Prose ("both already tag this rule", "handle-zeroing") is
# lowercase or hyphenated and never matches; NOT COVERED is handled separately as a first-class value.
VERIFIED_TOKEN = re.compile(r"\b([A-Z][A-Za-z0-9]{3,}(?:_[A-Za-z0-9_]+)?)\b")
NOT_COVERED = "NOT COVERED"


class Rule:
    def __init__(self, rule_id, title, path, line):
        self.id = rule_id
        self.title = title
        self.path = path
        self.line = line
        self.severities = set(SEVERITY.findall(title))
        self.fields = {}

    @property
    def verified(self):
        return self.fields.get("verified", "")

    @property
    def is_covered_by_doc(self):
        return bool(self.verified) and NOT_COVERED not in self.verified


def parse_rules(rules_dir):
    """[Rule] across every rules/*.md except README."""
    rules = []
    for fn in sorted(os.listdir(rules_dir)):
        if not fn.endswith(".md") or fn == "README.md":
            continue
        path = os.path.join(rules_dir, fn)
        with open(path, encoding="utf-8", errors="replace") as fh:
            lines = fh.read().splitlines()
        current = None
        for i, line in enumerate(lines):
            m = RULE_HEADING.match(line)
            if m:
                current = Rule(m.group(1), m.group(2), path, i + 1)
                rules.append(current)
                continue
            if SECTION_HEADING.match(line) or line.startswith("### "):
                current = None
                continue
            if current is not None:
                fm = FIELD.match(line)
                if fm:
                    # First occurrence wins; continuation lines are indented prose and are not re-parsed as fields.
                    current.fields.setdefault(fm.group(1), fm.group(2).strip())
    return rules


def parse_tests(tests_dir):
    """(verifies, mutants, identifiers) — rule id -> [(path, line)], plus every class/method name in the tree."""
    verifies, mutants = {}, {}
    identifiers = set()
    for dirpath, _, filenames in os.walk(tests_dir):
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fn)
            with open(path, encoding="utf-8", errors="replace") as fh:
                lines = fh.read().splitlines()
            for i, line in enumerate(lines):
                for rid in VERIFIES_RULE.findall(line):
                    verifies.setdefault(rid, []).append((path, i + 1))
                for rid in RULE_MUTANT.findall(line):
                    mutants.setdefault(rid, []).append((path, i + 1))
                identifiers.update(CS_CLASS.findall(line))
                identifiers.update(CS_METHOD.findall(line))
    return verifies, mutants, identifiers


# ── checks ──────────────────────────────────────────────────────────────────

class Finding:
    def __init__(self, check, where, message, hint, blocking=True):
        self.check = check
        self.where = where
        self.message = message
        self.hint = hint
        self.blocking = blocking


def check_duplicate_ids(rules):
    seen = {}
    for r in rules:
        seen.setdefault(r.id, []).append(r)
    out = []
    for rid, group in sorted(seen.items()):
        if len(group) > 1:
            locs = ", ".join(f"{os.path.basename(g.path)}:{g.line}" for g in group)
            out.append(Finding(
                "DUPLICATE_RULE_ID", locs,
                f"rule id {rid} is defined {len(group)} times",
                "rules/README.md: 'Rule IDs must be unique across the entire database, not just within a file' — "
                "any tool indexing by id (this audit, [VerifiesRule], a citation in a design doc) silently "
                "conflates them. Rename all but one, as SQ-01..05/PS-01 already were."))
    return out


def check_unknown_ids(rules, verifies, mutants):
    known = {r.id for r in rules}
    out = []
    for rid, sites in sorted({**verifies, **mutants}.items()):
        if rid not in known:
            path, line = sites[0]
            out.append(Finding(
                "UNKNOWN_RULE_ID", f"{path}:{line}",
                f"[VerifiesRule/RuleMutant(\"{rid}\")] names a rule that does not exist",
                "The rule was renamed or deleted and the attribute was not. A verifier pointing at nothing is "
                "counted as coverage by every reader who greps for the id."))
    return out


def check_dangling_verified(rules, identifiers):
    out = []
    for r in rules:
        if not r.is_covered_by_doc:
            continue
        tokens = [t for t in VERIFIED_TOKEN.findall(r.verified) if t != NOT_COVERED.split()[0]]
        missing = [t for t in tokens if t not in identifiers]
        if tokens and len(missing) == len(tokens):
            out.append(Finding(
                "DANGLING_VERIFIED", f"{os.path.basename(r.path)}:{r.line}",
                f"{r.id} `verified:` names {missing}, none of which exist in the test sources",
                "The rule claims a verifier that is not there — a rename or deletion left the citation behind. "
                "Point it at the real test, or set `verified: NOT COVERED — <why>`, which is honest and is what "
                "the ratchet counts."))
    return out


def check_verifier_without_mutant(rules, verifies, mutants):
    """
    Reported, not blocking — the RATCHET is what blocks (see `ratchet`).

    There are 15 cited verifiers and none of them had a mutant when this audit was written. Turning that into an
    immediate hard failure would force 15 mutants in one push, and a mutant written to satisfy a gate rather than
    to genuinely falsify its verifier is the same false green the gate exists to remove — it would simply move the
    dishonesty from `[VerifiesRule]` to `[RuleMutant]`. So the debt is listed every run and the count may only go
    UP. A new `[VerifiesRule]` still effectively requires a mutant, because adding one without a mutant leaves
    `rules_with_mutant` behind `rules_with_verifier` and the next baseline bump makes that visible.
    """
    known = {r.id: r for r in rules}
    out = []
    for rid, sites in sorted(verifies.items()):
        if rid in mutants or rid not in known:
            continue
        path, line = sites[0]
        out.append(Finding(
            "VERIFIER_WITHOUT_MUTANT", f"{path}:{line}",
            f"{rid} has a [VerifiesRule] test but no [RuleMutant] — nothing shows the verifier can fail",
            "Add a [RuleMutant(\"<id>\")] companion that drives the same assertion path with a deliberately "
            "violating input and requires it to fail (RuleMutants.AssertDetects). This is the TLA+ mutant "
            "discipline the gate already applies to 3 specs, generalised: a conformance test that has never "
            "rejected anything is not evidence. REPORTED, NOT BLOCKING — the ratchet blocks a decrease.",
            blocking=False))
    return out


# ── matrix + ratchet ────────────────────────────────────────────────────────

def build_matrix(rules, verifies, mutants):
    by_id = {}
    for r in rules:
        by_id.setdefault(r.id, r)
    rows = []
    for rid in sorted(by_id):
        r = by_id[rid]
        rows.append({
            "id": rid,
            "file": os.path.basename(r.path),
            "severity": "".join(f"[{s}]" for s in sorted(r.severities)),
            "doc_verified": r.is_covered_by_doc,
            "test_verifies": len(verifies.get(rid, [])),
            "test_mutants": len(mutants.get(rid, [])),
        })
    return rows


def counts(rows):
    return {
        "rules_total": len(rows),
        "rules_with_verifier": sum(1 for r in rows if r["test_verifies"]),
        "rules_with_mutant": sum(1 for r in rows if r["test_mutants"]),
        "fatal_rules_with_verifier": sum(1 for r in rows if r["test_verifies"] and "[fatal]" in r["severity"]),
    }


def write_matrix(rows, c, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("# Rule coverage matrix\n\n")
        fh.write("Generated by `scripts/audit-rule-coverage.py` (#703). Do not hand-edit.\n\n")
        fh.write(f"| rules | with a `[VerifiesRule]` test | with a `[RuleMutant]` | `[fatal]` with a verifier |\n")
        fh.write(f"|---|---|---|---|\n")
        fh.write(f"| {c['rules_total']} | {c['rules_with_verifier']} | {c['rules_with_mutant']} | "
                 f"{c['fatal_rules_with_verifier']} |\n\n")
        fh.write("## Verified rules\n\n| rule | file | severity | doc `verified:` | tests | mutants |\n")
        fh.write("|---|---|---|---|---|---|\n")
        for r in rows:
            if not (r["test_verifies"] or r["doc_verified"]):
                continue
            fh.write(f"| `{r['id']}` | {r['file']} | {r['severity']} | {'yes' if r['doc_verified'] else '—'} | "
                     f"{r['test_verifies']} | {r['test_mutants']} |\n")


def ratchet(c, baseline_path, update):
    if update:
        os.makedirs(os.path.dirname(baseline_path), exist_ok=True)
        with open(baseline_path, "w", encoding="utf-8") as fh:
            json.dump(c, fh, indent=2, sort_keys=True)
            fh.write("\n")
        print(f"baseline written: {baseline_path}")
        return []
    if not os.path.exists(baseline_path):
        return [Finding("RATCHET_MISSING", baseline_path,
                        "no coverage baseline on disk",
                        "Create it with --update-baseline. Without a baseline the ratchet cannot detect a "
                        "verifier being deleted, which is the one thing it exists to prevent.")]
    with open(baseline_path, encoding="utf-8") as fh:
        base = json.load(fh)
    out = []
    for key in ("rules_with_verifier", "rules_with_mutant", "fatal_rules_with_verifier"):
        was, now = base.get(key, 0), c.get(key, 0)
        if now < was:
            out.append(Finding(
                "RATCHET_REGRESSION", baseline_path,
                f"{key} decreased: {was} -> {now}",
                "Rule coverage may not go down. If a verifier was retired ON PURPOSE (it could not be given an "
                "honest mutant), say so in the rule's `verified:` field and lower the baseline in the same "
                "commit — deliberately, not as a side effect."))
    return out


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.dirname(here)

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rules-dir", default=os.path.join(repo, "rules"))
    ap.add_argument("--tests-dir", default=os.path.join(repo, "test"))
    ap.add_argument("--baseline", default=os.path.join(repo, "coverage", "rule-coverage-baseline.json"))
    ap.add_argument("--matrix", default=os.path.join(repo, "coverage", "rule-coverage.md"))
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args(argv)

    if not os.path.isdir(args.rules_dir):
        print(f"ERROR: --rules-dir {args.rules_dir} does not exist", file=sys.stderr)
        return 2

    rules = parse_rules(args.rules_dir)
    verifies, mutants, identifiers = parse_tests(args.tests_dir)

    findings = []
    findings += check_duplicate_ids(rules)
    findings += check_unknown_ids(rules, verifies, mutants)
    findings += check_dangling_verified(rules, identifiers)
    findings += check_verifier_without_mutant(rules, verifies, mutants)

    rows = build_matrix(rules, verifies, mutants)
    c = counts(rows)
    write_matrix(rows, c, args.matrix)
    findings += ratchet(c, args.baseline, args.update_baseline)

    print(f"rules={c['rules_total']}  with-verifier={c['rules_with_verifier']}  "
          f"with-mutant={c['rules_with_mutant']}  fatal-with-verifier={c['fatal_rules_with_verifier']}")
    print(f"matrix: {os.path.relpath(args.matrix, repo)}")

    if not findings:
        print("rule-coverage audit: clean")
        return 0

    by_check = {}
    for f in findings:
        by_check.setdefault(f.check, []).append(f)
    for check in sorted(by_check):
        fs = by_check[check]
        tag = "" if fs[0].blocking else "  [reported, non-blocking — the ratchet blocks a decrease]"
        print(f"\n=== {check} — {len(fs)} finding(s) ==={tag}")
        print(f"    {fs[0].hint}\n")
        for f in fs:
            print(f"  {f.where}: {f.message}")
            if os.environ.get("GITHUB_ACTIONS"):
                level = "error" if f.blocking else "warning"
                print(f"::{level}::{check}: {f.message} ({f.where})")

    blocking = [f for f in findings if f.blocking]
    print(f"\nrule-coverage audit: {len(findings)} finding(s) across {len(by_check)} check(s); "
          f"{len(blocking)} blocking")
    return 1 if blocking else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
