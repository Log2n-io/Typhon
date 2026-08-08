#!/usr/bin/env python3
"""
lint-test-suppressions.py — make a suppressed test say WHY, and make it expire.

WHY (issue #703 / design `Infrastructure/test-strategy.md` §5.2): `[Ignore]` in this suite currently means five
different things — "blocked on an open bug", "flaky", "too slow", "manual tool", "unstable". Because the marker
carries no type, nothing can lint it and nothing ever revisits it. Two consequences, both measured on `main`:

  * `bench/aws/shards.json` NAMES `ChaosStressTests` and `Runtime.CheckerboardTests` among its 306 classes. Both
    are class-level `[Ignore]`d, so the gate allocates a shard slot and a time budget, runs ZERO tests, and
    reports green. NUnit applies `[Ignore]` unconditionally — `--filter` cannot override it.
  * `EcsQueryFullScanFallbackTests` suppressed two tests against #591, which is CLOSED. The guard outlived its
    bug (the same shape as #398's, which outlived its bug by six weeks; when it was removed the cell was green).

So: one meaning per marker, enforced here.

    [Ignore("#N ...")]              the test CANNOT pass until #N ships          -> requires an OPEN issue
    [Category("Quarantine")]        known-red, cause not yet fixed               -> requires an OPEN issue
    [Explicit] + [Category(tier)]   can pass; too costly/noisy for the gate      -> requires a declared TIER
    (nothing)                       runs in the merge gate

`[Ignore]` meaning slow / manual / flaky is rejected: those are `[Explicit]`+tier and `Quarantine` respectively.

Checks (each maps to an issue #703 deliverable):

    IGNORE_NO_ISSUE       `[Ignore]` whose reason carries no `#N`
    IGNORE_MEANS_COST     `[Ignore]` whose reason says slow/long/manual/benchmark/stress/flaky/unstable
    IGNORE_CLOSED_ISSUE   `[Ignore("#N")]` where N is CLOSED           (needs GitHub; see --no-github)
    QUARANTINE_NO_ISSUE   `[Category("Quarantine")]` with no `#N` in its attribute block or leading comment
    EXPLICIT_NO_TIER      `[Explicit]` with no tier `[Category]` on the member or its fixture
    MANUAL_NO_REASON      tier `Manual` (never runs in CI) with no comment justifying why
    SHARD_ZERO_TESTS      a class named in `shards.json` that resolves to zero runnable tests — either the
                          fixture is class-level `[Ignore]`d, or it is class-level tagged with a tier the gate
                          excludes (Quarantine / Nightly / Manual)
    SUPPRESSION_CITES_CLOSING_ISSUE
                          a suppression citing an issue THIS change closes  (needs --closing)

`SUPPRESSION_CITES_CLOSING_ISSUE` exists because `IGNORE_CLOSED_ISSUE` has a blind spot it cannot see out of. On a PR
that closes #N *and* leaves an `[Ignore("#N")]` behind, #N is still OPEN while the PR is open, so the lint passes —
and the merge is what closes it, so the same lint reddens `main` minutes later. Green before merge, red after. The
merge gate's `closing-keywords` job already extracts the set GitHub will act on; feed it here with `--closing`.

Usage:
    python3 scripts/lint-test-suppressions.py                       # lint the repo, probe issue state
    python3 scripts/lint-test-suppressions.py --no-github           # offline: skip IGNORE_CLOSED_ISSUE
    python3 scripts/lint-test-suppressions.py --closing "718 722"   # + reject suppressions citing what this closes
    python3 scripts/lint-test-suppressions.py --root DIR --shards F # point at a fixture tree (self-tests)

Exit code: 0 when clean, 1 when any violation is found, 2 on a usage error.
"""
import argparse
import json
import os
import re
import subprocess
import sys

# `Manual` is the one tier that never runs anywhere, so it has to justify itself in source.
TIERS = ("Nightly", "Manual")

# Mirrors bench/aws/shard.py GATE_EXCLUDED. Kept as a literal rather than imported because this lint runs on a
# free runner with nothing but this repo checked out, and a cross-directory import for three strings would be
# more coupling than it buys. test_lint_suppressions.py asserts the two stay in agreement.
GATE_EXCLUDED_CATEGORIES = {"Quarantine", "Nightly", "Manual"}

# Reasons that describe COST or INSTABILITY rather than "the feature does not exist yet". `[Ignore]` is the wrong
# marker for every one of them: cost is `[Explicit]`+tier, instability is `Quarantine` (which shard.py already
# excludes everywhere). Matched case-insensitively against the reason text.
COST_WORDS = re.compile(
    r"\b(too\s+long|long[- ]running|slow|takes\s+~?\d|manual(ly)?|on\s+demand|benchmark|perf(ormance)?|"
    r"stress|torture|saturat\w*|flaky|instable|unstable|intermittent\w*)\b",
    re.IGNORECASE,
)

ISSUE_REF = re.compile(r"#(\d+)")
ATTR_LINE = re.compile(r"^\s*\[")
COMMENT_LINE = re.compile(r"^\s*//")
CLASS_DECL = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:internal|public|private|protected)?\s*"
                        r"(?:sealed\s+|abstract\s+|partial\s+|static\s+|unsafe\s+)*class\s+([A-Za-z0-9_]+)")
IGNORE_ATTR = re.compile(r"\[Ignore\s*\(\s*(?:@?\$?)\"(.*?)\"\s*(?:,|\))", re.DOTALL)
IGNORE_BARE = re.compile(r"\[Ignore\s*\]")
EXPLICIT_ATTR = re.compile(r"\[Explicit\b")
CATEGORY_ATTR = re.compile(r"\[Category\s*\(\s*\"([^\"]+)\"\s*\)\s*\]")
# `[Test]`, `[TestCase(...)]`, `[TestCaseSource(...)]` — anything that makes a member a runnable case. Used to tell
# "this fixture declares tests, all of them excluded" from "this file has no tests here", which must stay silent.
TEST_ATTR = re.compile(r"\[Test(?:Case(?:Source)?)?\s*[\(\]]")


class Violation:
    def __init__(self, check, path, line, message, hint):
        self.check = check
        self.path = path
        self.line = line
        self.message = message
        self.hint = hint


class AttributeGroup:
    """One `[...]` block plus the declaration it decorates and the comment lines leading into it."""

    def __init__(self, path):
        self.path = path
        self.attr_lines = []      # (lineno, text)
        self.comments = []        # text of the comment lines immediately above the block
        self.decl_line = 0
        self.decl_text = ""
        self.enclosing_class = ""

    @property
    def start_line(self):
        return self.attr_lines[0][0] if self.attr_lines else self.decl_line

    @property
    def attr_text(self):
        return "\n".join(t for _, t in self.attr_lines)

    @property
    def is_class(self):
        return CLASS_DECL.match(self.decl_text) is not None

    def line_of(self, pattern):
        """Line number of the first attribute line matching `pattern` (for a precise annotation)."""
        for lineno, text in self.attr_lines:
            if pattern.search(text):
                return lineno
        return self.start_line

    def categories(self):
        return set(CATEGORY_ATTR.findall(self.attr_text))

    def issue_refs(self):
        """Issue numbers named in the attributes OR in the comment block leading into them."""
        return {int(n) for n in ISSUE_REF.findall(self.attr_text + "\n" + "\n".join(self.comments))}


def parse_file(path, text):
    """
    Split a C# file into attribute groups. Deliberately line-based rather than a real parser: the inputs are
    attribute blocks on their own lines, and a lexer would be far more machinery than the job needs. Comment
    lines INSIDE a block are kept (the existing fixtures interleave them), and comment lines immediately above
    a block are carried along, because that is where the quarantine issue references already live.
    """
    lines = text.splitlines()
    groups = []
    class_at = []            # (lineno, name) for every class declaration, in order

    for i, raw in enumerate(lines):
        m = CLASS_DECL.match(raw)
        if m:
            class_at.append((i + 1, m.group(1)))

    i = 0
    pending_comments = []
    while i < len(lines):
        line = lines[i]
        if COMMENT_LINE.match(line):
            pending_comments.append(line.strip())
            i += 1
            continue
        if not line.strip():
            pending_comments = []
            i += 1
            continue
        if not ATTR_LINE.match(line):
            pending_comments = []
            i += 1
            continue

        g = AttributeGroup(path)
        g.comments = list(pending_comments)
        pending_comments = []
        # Collect the contiguous attribute block, tolerating comment lines between attributes.
        while i < len(lines) and (ATTR_LINE.match(lines[i]) or COMMENT_LINE.match(lines[i])):
            if ATTR_LINE.match(lines[i]):
                g.attr_lines.append((i + 1, lines[i]))
            else:
                g.comments.append(lines[i].strip())
            i += 1
        # The next non-blank line is what the block decorates.
        while i < len(lines) and not lines[i].strip():
            i += 1
        if i < len(lines):
            g.decl_line = i + 1
            g.decl_text = lines[i]
        # Enclosing class = the last class declared at or before this block.
        enclosing = ""
        for lineno, name in class_at:
            if lineno <= g.start_line + 1:
                enclosing = name
            else:
                break
        g.enclosing_class = enclosing
        groups.append(g)

    return groups


def class_level_groups(groups):
    """name -> the attribute group decorating that class declaration."""
    out = {}
    for g in groups:
        m = CLASS_DECL.match(g.decl_text) if g.decl_text else None
        if m:
            out[m.group(1)] = g
    return out


def scan_tree(root):
    """path -> (groups, class_groups) for every .cs file under `root`."""
    parsed = {}
    for dirpath, _, filenames in os.walk(root):
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            p = os.path.join(dirpath, fn)
            with open(p, encoding="utf-8", errors="replace") as fh:
                text = fh.read()
            groups = parse_file(p, text)
            parsed[p] = (groups, class_level_groups(groups))
    return parsed


# ── issue state ─────────────────────────────────────────────────────────────
# The closed-issue probe runs against the SAME repo, so the merge gate's built-in `github.token` is enough — this
# lint must stay usable on the free `invariants` runner, which deliberately holds no PAT and checks out nothing
# but this repo (see the job comment in merge-gate.yml).

def issue_states(numbers, repo):
    """{number: 'open'|'closed'}. A number that cannot be resolved is omitted, never guessed."""
    states = {}
    for n in sorted(numbers):
        try:
            env = dict(os.environ, MSYS_NO_PATHCONV="1")
            r = subprocess.run(["gh", "api", f"repos/{repo}/issues/{n}", "--jq", ".state"],
                               capture_output=True, text=True, env=env, timeout=30)
        except (OSError, subprocess.SubprocessError):
            return None
        if r.returncode != 0:
            continue
        states[n] = r.stdout.strip()
    return states


# ── checks ──────────────────────────────────────────────────────────────────

def check_ignores(parsed, states):
    violations = []
    for path, (groups, _) in parsed.items():
        for g in groups:
            if not IGNORE_ATTR.search(g.attr_text) and not IGNORE_BARE.search(g.attr_text):
                continue
            line = g.line_of(re.compile(r"\[Ignore"))
            m = IGNORE_ATTR.search(g.attr_text)
            reason = m.group(1) if m else ""

            refs = ISSUE_REF.findall(reason)
            if not refs:
                violations.append(Violation(
                    "IGNORE_NO_ISSUE", path, line,
                    f"[Ignore] carries no issue reference: \"{reason}\"",
                    "An [Ignore] states the test cannot pass until a specific issue ships. Name it: "
                    "[Ignore(\"#NNN - one line on what must land\")]. If the test is merely slow or noisy, it is "
                    "[Explicit] + [Category(\"Nightly\")]; if it is known-red, it is [Category(\"Quarantine\")]."))
            cost = COST_WORDS.search(reason)
            if cost:
                violations.append(Violation(
                    "IGNORE_MEANS_COST", path, line,
                    f"[Ignore] reason describes cost or instability ('{cost.group(0)}'), not a missing feature: "
                    f"\"{reason}\"",
                    "[Ignore] removes the test from EVERY run, filtered or not. Use [Explicit] + "
                    "[Category(\"Nightly\")] for cost, [Category(\"Quarantine\")] + an issue for instability. "
                    "ChaosStressTests was labelled 'too long' when it actually hung (#695), and the label made "
                    "that invisible for months."))
            if states is not None:
                for ref in refs:
                    if states.get(int(ref)) == "closed":
                        violations.append(Violation(
                            "IGNORE_CLOSED_ISSUE", path, line,
                            f"[Ignore] references #{ref}, which is CLOSED",
                            "The blocker shipped; the guard did not. Un-ignore the test and run it — if it now "
                            "passes, delete the suppression. If it still fails, that is a NEW defect and needs a "
                            "new issue, not a stale reference."))
    return violations


def check_closing_keywords(parsed, closing):
    """Flag suppressions that cite an issue THIS change closes.

    The gap this fills is a timing one, and it is invisible from inside a PR. `IGNORE_CLOSED_ISSUE` asks whether the
    cited issue is closed *right now*; on a PR that both closes #N and leaves an `[Ignore("#N")]` in the tree, #N is
    still OPEN, so the lint passes — and the merge is what closes it, so the same lint fails on `main` minutes later.
    Green before merge, red after, with nothing in between to catch it.

    Measured: PR #721 closed #718 while `ViewCreatedBeforeTheSpawns_ConvergesWithOneCreatedAfter` stayed quarantined
    against it. `invariants` passed on the PR and reddened `main` at 22:32.

    `[Ignore]` ONLY, deliberately. Its `#N` comes from the reason string, a single field whose reference IS the
    blocker by construction. `Quarantine` records its issue in free prose — the attribute block or the comment above
    it — and that prose legitimately discusses more than one issue: `ChaosStressTests.CreateDeleteRecreate_RapidLifecycle`
    is quarantined against #696 while its comment also names #695 (the livelock it was retargeted from) and #716 (a
    suspected mechanism). Treating every nearby reference as the target flags that comment, which is well written and
    exactly what a quarantine note should say. A checker that cries wolf on good prose gets switched off, so the
    narrow, precise half is the one worth having.

    The gap that leaves is real and worth its own issue: `Quarantine` has no machine-readable target field, which is
    also why nothing today catches a quarantine whose issue has closed (`IGNORE_CLOSED_ISSUE` covers `[Ignore]` alone).
    """
    if not closing:
        return []

    violations = []
    for path, (groups, _class_groups) in parsed.items():
        for g in groups:
            ignore_m = IGNORE_ATTR.search(g.attr_text)
            if not ignore_m:
                continue

            refs = {int(n) for n in ISSUE_REF.findall(ignore_m.group(1))}
            line = g.line_of(re.compile(r"\[Ignore"))

            for ref in sorted(refs & closing):
                violations.append(Violation(
                    "SUPPRESSION_CITES_CLOSING_ISSUE", path, line,
                    f"[Ignore] cites #{ref}, which this change CLOSES",
                    "Pick one, because they contradict each other. If the suppression is obsolete, delete it and let "
                    "the test run. If it describes work this change does NOT do, that work needs its own issue — "
                    "split it out and repoint the marker there. Leaving both in one commit is green here and red on "
                    "main, because the merge is what closes the issue: IGNORE_CLOSED_ISSUE cannot see it from inside "
                    "the PR. If the closing keyword itself is the mistake, defuse it (`#N` in backticks, or 'PR #N')."))
    return violations


def check_quarantine(parsed):
    violations = []
    for path, (groups, class_groups) in parsed.items():
        for g in groups:
            if "Quarantine" not in g.categories():
                continue
            refs = g.issue_refs()
            if not refs and g.enclosing_class in class_groups:
                refs = class_groups[g.enclosing_class].issue_refs()
            if not refs:
                violations.append(Violation(
                    "QUARANTINE_NO_ISSUE", path, g.line_of(CATEGORY_ATTR),
                    "[Category(\"Quarantine\")] with no issue reference",
                    "Quarantine means known-red and excluded from every tier. Without an issue it is a permanent "
                    "hiding place. Put #NNN in the attribute block or the comment directly above it."))
    return violations


def check_explicit(parsed):
    violations = []
    for path, (groups, class_groups) in parsed.items():
        for g in groups:
            if not EXPLICIT_ATTR.search(g.attr_text):
                continue
            cats = set(g.categories())
            if g.enclosing_class in class_groups:
                cats |= class_groups[g.enclosing_class].categories()
            tiers = cats & set(TIERS)
            line = g.line_of(EXPLICIT_ATTR)
            if not tiers:
                violations.append(Violation(
                    "EXPLICIT_NO_TIER", path, line,
                    "[Explicit] with no tier category",
                    f"[Explicit] removes a test from the gate; without a tier it runs NOWHERE. Add "
                    f"[Category(\"{TIERS[0]}\")] so the nightly picks it up, or [Category(\"Manual\")] plus a "
                    f"comment saying why CI cannot run it. OlcBTreeStressTests sat [Explicit] with no tier and "
                    f"had no running evidence in CI for four months."))
            elif "Manual" in tiers:
                justification = " ".join(g.comments)
                if g.enclosing_class in class_groups:
                    justification += " " + " ".join(class_groups[g.enclosing_class].comments)
                if len(justification.strip()) < 20:
                    violations.append(Violation(
                        "MANUAL_NO_REASON", path, line,
                        "[Category(\"Manual\")] with no comment justifying why CI cannot run it",
                        "Manual is the one tier that never runs anywhere. State the reason in a comment above it "
                        "(requires an env var / dedicated hardware / wall-clock assertions that cannot hold on a "
                        "shared runner / it is a developer tool, not a test)."))
    return violations


def _method_groups_of(groups, cls):
    """Every attribute group in `groups` that decorates a MEMBER of class `cls` (i.e. not the class itself)."""
    return [g for g in groups if g.enclosing_class == cls and not g.is_class]


def _every_test_is_gate_excluded(groups, cls):
    """
    True when `cls` declares at least one test and the gate would run NONE of them, purely from method-level
    markers. Returns (True, reason) or (False, None).

    This is the method-level twin of the class-level checks below, and it exists because the class-level ones
    missed a real case: `WorkbenchFixtureGenerator` carries no class-level suppression at all — its single
    `[Test]` is `[Explicit] + [Category("Manual")]` — so the static lint called it clean while the gate's
    run-time integrity check failed on it, on the expensive runner, after a full suite run. A fixture whose
    every METHOD is excluded resolves to exactly the same zero tests as one excluded wholesale.
    """
    method_groups = _method_groups_of(groups, cls)
    tests = [g for g in method_groups if TEST_ATTR.search(g.attr_text)]
    if not tests:
        return (False, None)   # no [Test] members parsed — say nothing rather than guess

    reasons = set()
    for g in tests:
        excluded = g.categories() & GATE_EXCLUDED_CATEGORIES
        if excluded:
            reasons.add(f'[Category("{sorted(excluded)[0]}")]')
            continue
        if EXPLICIT_ATTR.search(g.attr_text):
            reasons.add("[Explicit]")
            continue
        if IGNORE_ATTR.search(g.attr_text) or IGNORE_BARE.search(g.attr_text):
            reasons.add("[Ignore]")
            continue
        return (False, None)   # this one WOULD run, so the class is not empty to the gate

    return (True, " + ".join(sorted(reasons)))


def check_shards(parsed, shards_path):
    """
    A class NAMED by a shard must resolve to at least one test the gate will actually run.

    Three ways a named class silently resolves to zero, all checked here rather than only by shard.py's run-time
    integrity check — that one is authoritative but only reports on the expensive gate runner, while this is the
    cheap fast feedback on a free runner:

      * a class-level `[Ignore]` — NUnit applies it unconditionally, so no filter can reach the fixture;
      * a class-level tier category the gate EXCLUDES (`Quarantine` / `Nightly` / `Manual`, see shard.py's
        GATE_EXCLUDED) — the filter reaches the fixture and then excludes every test in it;
      * EVERY `[Test]` in the fixture individually excluded, with nothing at class level at all.

    The third was missing until #705 and cost a full gate run to discover. Checking only the class level encodes
    an assumption — that a fixture is suppressed wholesale or not at all — which the suite does not honour, and
    a check whose blind spot is the thing it exists to detect is the false green this lint was written to remove.
    """
    violations = []
    if not shards_path or not os.path.exists(shards_path):
        return violations
    with open(shards_path, encoding="utf-8") as fh:
        shards = json.load(fh)
    named = {c.split(".")[-1] for s in shards for c in s.get("classes", []) if not c.startswith("<")}

    for path, (groups, class_groups) in parsed.items():
        # Method-level exclusion. Runs for every named class, INCLUDING ones that have a class-level attribute
        # group — nearly all of them do, because `[TestFixture]` is itself a class-level attribute. Gating this on
        # "has no class group" was the first attempt and it fired on nothing at all.
        # Reported only when the class-level checks below would not fire, so one fixture never yields two
        # violations for the same zero tests.
        for cls in sorted({g.enclosing_class for g in groups if g.enclosing_class}):
            if cls not in named:
                continue
            cg = class_groups.get(cls)
            if cg is not None and (IGNORE_ATTR.search(cg.attr_text) or IGNORE_BARE.search(cg.attr_text)
                                   or (cg.categories() & GATE_EXCLUDED_CATEGORIES)):
                continue   # the class-level check below owns this one

            all_excluded, reason = _every_test_is_gate_excluded(groups, cls)
            if all_excluded:
                first = _method_groups_of(groups, cls)[0]
                violations.append(Violation(
                    "SHARD_ZERO_TESTS", path, first.start_line,
                    f"{cls} is named by a CI shard but EVERY [Test] in it is {reason} - the shard runs zero tests "
                    f"and reports green",
                    "Nothing is suppressed at CLASS level here, so a class-level check sees a healthy fixture and "
                    "this only surfaces in shard.py's run-time integrity check, on the gate runner, after a full "
                    "suite run. Drop the class from shards.json - shard 0 is a negative-filter catch-all, so "
                    "nothing escapes the gate by being unnamed."))

        for cls, g in class_groups.items():
            if cls not in named:
                continue
            if IGNORE_ATTR.search(g.attr_text) or IGNORE_BARE.search(g.attr_text):
                violations.append(Violation(
                    "SHARD_ZERO_TESTS", path, g.line_of(re.compile(r"\[Ignore")),
                    f"{cls} is named by a CI shard but the whole fixture is [Ignore]d - the shard runs zero tests "
                    f"and reports green",
                    "NUnit applies [Ignore] unconditionally; --filter cannot override it, so CI budgets a shard "
                    "slot for nothing. Either un-ignore the fixture, or drop the class from shards.json - but a "
                    "named class that runs nothing is a false green."))
                continue

            excluded = g.categories() & GATE_EXCLUDED_CATEGORIES
            if excluded:
                violations.append(Violation(
                    "SHARD_ZERO_TESTS", path, g.line_of(CATEGORY_ATTR),
                    f"{cls} is named by a CI shard but the whole fixture is [Category(\"{sorted(excluded)[0]}\")], "
                    f"which the gate excludes - the shard runs zero tests and reports green",
                    "The gate filter excludes these categories outright (shard.py GATE_EXCLUDED), so a shard that "
                    "names such a class budgets a slot for nothing. Drop the class from shards.json when it moves "
                    "to an excluded tier - shard 0 is a catch-all, so nothing escapes the gate by being unnamed."))
    return violations


# ── reporting ───────────────────────────────────────────────────────────────

def report(violations, repo_root, skipped):
    if skipped:
        print(f"NOTE: {skipped} (that check did NOT run - do not read this pass as covering it)")

    if not violations:
        print("test-suppression lint: clean")
        return 0

    by_check = {}
    for v in violations:
        by_check.setdefault(v.check, []).append(v)

    for check in sorted(by_check):
        vs = by_check[check]
        print(f"\n=== {check} - {len(vs)} violation(s) ===")
        print(f"    {vs[0].hint}\n")
        for v in vs:
            rel = os.path.relpath(v.path, repo_root).replace("\\", "/")
            print(f"  {rel}:{v.line}: {v.message}")
            # GitHub Actions annotation - surfaces the violation on the PR's Files tab.
            if os.environ.get("GITHUB_ACTIONS"):
                print(f"::error file={rel},line={v.line}::{check}: {v.message}")

    print(f"\ntest-suppression lint: {len(violations)} violation(s) across {len(by_check)} check(s)")
    return 1


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(here)

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=os.path.join(repo_root, "test"),
                    help="directory of C# test sources to lint (default: test/)")
    ap.add_argument("--shards", default=os.path.join(repo_root, "bench", "aws", "shards.json"),
                    help="shards.json to cross-check (default: bench/aws/shards.json)")
    ap.add_argument("--repo", default=os.environ.get("LINT_REPO", "log2n-io/Typhon"),
                    help="owner/name used to resolve issue state")
    ap.add_argument("--no-github", action="store_true",
                    help="skip IGNORE_CLOSED_ISSUE (offline / no gh)")
    ap.add_argument("--closing", default="",
                    help="issue numbers this change will close (whitespace- or comma-separated, '#' optional). Any "
                         "suppression citing one is rejected: the merge is what closes them, so IGNORE_CLOSED_ISSUE "
                         "cannot see the conflict from inside the PR. Fed by the merge gate's closing-keywords job.")
    args = ap.parse_args(argv)

    if not os.path.isdir(args.root):
        print(f"ERROR: --root {args.root} is not a directory", file=sys.stderr)
        return 2

    closing = {int(n) for n in re.findall(r"\d+", args.closing)}

    parsed = scan_tree(args.root)

    violations = []
    violations += check_closing_keywords(parsed, closing)
    violations += check_quarantine(parsed)
    violations += check_explicit(parsed)
    violations += check_shards(parsed, args.shards)

    skipped = None
    states = None
    if args.no_github:
        skipped = "IGNORE_CLOSED_ISSUE skipped (--no-github)"
    else:
        refs = set()
        for _, (groups, _) in parsed.items():
            for g in groups:
                m = IGNORE_ATTR.search(g.attr_text)
                if m:
                    refs |= {int(n) for n in ISSUE_REF.findall(m.group(1))}
        states = issue_states(refs, args.repo)
        if states is None:
            skipped = "IGNORE_CLOSED_ISSUE skipped (gh CLI unavailable)"

    violations += check_ignores(parsed, states)

    return report(violations, repo_root, skipped)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
