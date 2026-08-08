#!/usr/bin/env python3
"""Prove the covering array explores beyond the author's imagination (#704).

The claim #704 has to earn is this one, from its own acceptance list:

    "A deliberately planted axis-specific bug is caught by a cell nobody hand-wrote."

That is not something a test can assert about itself. It needs a real defect planted in PRODUCTION code, and it needs
two outcomes together:

    1. the named covering-array cell FAILS   — the array found it, and
    2. the pre-existing HAND-WRITTEN tests still PASS — the old fixtures did not.

Requiring (2) is what makes this evidence rather than a tautology. A mutation both sets catch proves only that the
mutation is detectable at all; it says nothing about whether parameterising the axis bought anything. The mutations
below are chosen precisely because the hand-written fixtures step over them.

This is the same positive-evidence discipline the repo already applies to TLA+ (`run-tlc.sh --expect-violation`
requires proof that TLC evaluated an invariant and found it violated) and, since #703, to rule verifiers
(`RuleMutants.AssertDetects` requires the verifier's OWN marker in the failure). "Not green" is not a verdict.

Why source mutation rather than a fault-injection switch: #703 ruled out shipping violation code into production —
it would put a branch in a hot path and would only ever prove the test detects an *injected* fault. So the mutation is
applied to the working tree, run, and reverted.

SAFETY
------
The script refuses to run when `src/` has uncommitted changes, because its cleanup path rewrites those files. That
check is not a nicety: a revert over someone's work in progress would destroy it.

It also REBUILDS after reverting, and that matters as much as the revert. An earlier version restored only the source
and left the mutated `Typhon.Engine.dll` in `bin/`. The next `dotnet test --no-build` then ran the mutated engine
against a clean tree: `git status` clean, the code reading correctly, and nine unexplained failures in a fixture that
passed in isolation. A poisoned build artifact with no visible cause is a worse bug than the one being probed, so the
rebuild lives in the `finally` alongside the revert.

Usage
-----
    python3 scripts/axis-mutation-probe.py --list
    python3 scripts/axis-mutation-probe.py --all
    python3 scripts/axis-mutation-probe.py --only slot-pinning

Exit code 0 iff every selected mutation produced both outcomes.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
TEST_PROJ = "test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj"


@dataclass
class Mutation:
    """One planted defect, plus what must happen to each side of the evidence."""

    name: str
    rationale: str
    path: str
    find: str
    replace: str
    filter: str                        # vstest --filter selecting BOTH the matrix fixture and the hand-written control
    must_fail: list = field(default_factory=list)      # substrings; at least one FAILED test must match each
    must_pass: list = field(default_factory=list)      # substrings; every matching test must have PASSED, and >0 must match


# ── The mutations ──────────────────────────────────────────────────────────────────────────────────────────────────
#
# Each `find` string is matched EXACTLY and must occur exactly once. A refactor that moves the code makes the mutation
# stale, and a stale mutation must fail loudly rather than silently probe nothing — the same reason #703's shard
# integrity check exists.

MUTATIONS = [
    Mutation(
        name="slot-pinning",
        rationale=(
            "Pin the schema-migration source slot to 0. Every entity then migrates from slot 0 of its old cluster, so "
            "entity N reads entity 0's bytes. A fixture that migrates ONE entity cannot see this — with one entity the "
            "only correct source slot IS 0, which makes the placement half of the re-cluster a tautology. "
            "SchemaEvolutionStorageModeTests migrates a single entity per test and stays green; the covering array's "
            "cells migrate 200 across more than one cluster and do not."
        ),
        path="src/Typhon.Engine/Ecs/public/DatabaseEngine.cs",
        find="var src = oldChunkBase + oldLayout.ComponentOffset(slot) + oldPos.SlotIndex * oldLayout.ComponentSize(slot);",
        replace="var src = oldChunkBase + oldLayout.ComponentOffset(slot) + 0 * oldLayout.ComponentSize(slot);",
        filter="FullyQualifiedName~SchemaEvolutionMatrixTests|FullyQualifiedName~SchemaEvolutionStorageModeTests",
        must_fail=["SchemaEvolutionMatrixTests"],
        must_pass=["SchemaEvolutionStorageModeTests"],
    ),
]


def run(cmd, **kw):
    return subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, shell=False, **kw)


def src_is_dirty() -> str:
    r = run(["git", "status", "--porcelain", "--", "src/"])
    return r.stdout.strip()


def apply_mutation(m: Mutation) -> str:
    """Returns the original file text so the caller can restore it verbatim."""
    p = REPO / m.path
    original = p.read_text(encoding="utf-8")
    n = original.count(m.find)
    if n != 1:
        raise SystemExit(
            f"::error::mutation '{m.name}' is STALE — its anchor occurs {n} times in {m.path}, expected exactly 1.\n"
            f"  anchor: {m.find}\n"
            "A mutation that no longer applies proves nothing; update the anchor or retire the mutation."
        )
    p.write_text(original.replace(m.find, m.replace), encoding="utf-8")
    return original


def parse_outcomes(results_dir: Path):
    """{fully-qualified test name: outcome} across every trx in the directory."""
    outcomes = {}
    for trx in results_dir.glob("*.trx"):
        root = ET.parse(trx).getroot()
        names = {}
        for ut in root.iter(f"{TRX_NS}UnitTest"):
            tm = ut.find(f"{TRX_NS}TestMethod")
            if ut.get("id") and tm is not None:
                names[ut.get("id")] = f'{tm.get("className")}.{tm.get("name")}'
        for r in root.iter(f"{TRX_NS}UnitTestResult"):
            name = names.get(r.get("testId"), r.get("testName", "?"))
            outcomes[name] = r.get("outcome", "?")
    return outcomes


def probe(m: Mutation, results_root: Path) -> bool:
    print(f"\n=== {m.name} ===")
    print(f"    {m.rationale}")

    results_dir = results_root / m.name
    original = apply_mutation(m)
    try:
        build = run(["dotnet", "build", TEST_PROJ, "-c", "Debug", "-v", "q", "--nologo"])
        if build.returncode != 0:
            print(f"::error::mutation '{m.name}' does not compile — it is not a behaviour change but a syntax error.")
            print(build.stdout[-2000:])
            return False

        run([
            "dotnet", "test", TEST_PROJ, "-c", "Debug", "--no-build",
            "--filter", m.filter,
            "--logger", "trx;LogFileName=probe.trx",
            "--results-directory", str(results_dir),
        ])
    finally:
        # Always, on every path. The tree must come back exactly as it was even if the run above threw or was killed.
        (REPO / m.path).write_text(original, encoding="utf-8")

        # …and so must bin/. Restoring the SOURCE is not enough: the build above baked the mutation into
        # Typhon.Engine.dll, and the next `dotnet test --no-build` anyone runs would execute the mutated engine
        # against a clean tree. That is a poisoned artifact with no visible cause — `git status` is clean, the code
        # reads correctly, and the suite fails. It cost an hour the first time; rebuilding here is the fix, and it is
        # inside the `finally` because a probe that dies mid-run is exactly when it matters most.
        print("    restoring bin/ (rebuilding against the reverted source)…")
        restore = run(["dotnet", "build", TEST_PROJ, "-c", "Debug", "-v", "q", "--nologo"])
        if restore.returncode != 0:
            print("::error::FAILED to rebuild after reverting the mutation. bin/ may still contain mutated code — "
                  "run `dotnet build test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Debug` before trusting "
                  "any --no-build run.")
            print(restore.stdout[-2000:])

    ok, lines = evaluate(m, parse_outcomes(results_dir))
    for line in lines:
        print(line)
    return ok


def evaluate(m: Mutation, outcomes: dict):
    """The verdict, as a pure function of the observed outcomes — so the evidence rules can be unit-tested without
    building the engine. Returns (ok, lines-to-print)."""
    lines = []
    if not outcomes:
        return False, [f"::error::mutation '{m.name}' produced no test results — the filter selected nothing."]

    ok = True

    # (1) The array must have caught it.
    for needle in m.must_fail:
        failed = [n for n, o in outcomes.items() if needle in n and o == "Failed"]
        total = [n for n in outcomes if needle in n]
        if not total:
            lines.append(f"::error::[{m.name}] no test matching '{needle}' ran at all — the probe is measuring nothing.")
            ok = False
        elif not failed:
            lines.append(f"::error::[{m.name}] the covering array did NOT catch the planted defect: "
                         f"{len(total)} case(s) matching '{needle}' ran and all passed.")
            ok = False
        else:
            lines.append(f"    ✓ caught by {len(failed)}/{len(total)} case(s) of '{needle}':")
            lines.extend(f"        {n}" for n in sorted(failed)[:5])

    # (2) The hand-written tests must NOT have caught it — that is what makes (1) evidence for parameterisation.
    for needle in m.must_pass:
        matched = {n: o for n, o in outcomes.items() if needle in n}
        if not matched:
            lines.append(f"::error::[{m.name}] no hand-written control matching '{needle}' ran — without it, (1) proves "
                         "only that the defect is detectable, not that the ARRAY is what detected it.")
            ok = False
        elif any(o == "Failed" for o in matched.values()):
            broke = [n for n, o in matched.items() if o == "Failed"]
            lines.append(f"::error::[{m.name}] the hand-written control ALSO caught this defect ({len(broke)} failed). "
                         "The mutation is not axis-specific, so it is not evidence for the covering array. Pick a sharper one:")
            lines.extend(f"        {n}" for n in sorted(broke)[:5])
            ok = False
        else:
            lines.append(f"    ✓ {len(matched)} hand-written control test(s) matching '{needle}' stayed green — the "
                         "array is what found it")

    return ok, lines


def main() -> int:
    ap = argparse.ArgumentParser(description="Prove the covering array catches axis-specific defects the hand-written tests miss.")
    ap.add_argument("--all", action="store_true", help="run every mutation")
    ap.add_argument("--only", action="append", default=[], help="run one mutation by name (repeatable)")
    ap.add_argument("--list", action="store_true", help="list the mutations and exit")
    ap.add_argument("--results-dir", default=None, help="where to write trx (default: a temp dir under the repo)")
    args = ap.parse_args()

    if args.list:
        for m in MUTATIONS:
            print(f"{m.name}\n    {m.rationale}\n    target: {m.path}\n")
        return 0

    selected = [m for m in MUTATIONS if m.name in args.only] if args.only else (MUTATIONS if args.all else [])
    if not selected:
        ap.error("pass --all, --only NAME, or --list")

    unknown = set(args.only) - {m.name for m in MUTATIONS}
    if unknown:
        print(f"::error::unknown mutation(s): {', '.join(sorted(unknown))}")
        return 2

    dirty = src_is_dirty()
    if dirty:
        print("::error::src/ has uncommitted changes. This script REWRITES files under src/ and restores them from an "
              "in-memory copy; running it over work in progress risks losing it. Commit or stash first.\n" + dirty)
        return 2

    results_root = Path(args.results_dir) if args.results_dir else REPO / "coverage" / "mutation-probe"
    results_root.mkdir(parents=True, exist_ok=True)

    failures = [m.name for m in selected if not probe(m, results_root)]

    print()
    if failures:
        print(f"axis mutation probe: {len(failures)}/{len(selected)} mutation(s) did NOT produce the required evidence: "
              f"{', '.join(failures)}")
        return 1

    print(f"axis mutation probe: {len(selected)}/{len(selected)} mutation(s) caught by a covering-array cell that no "
          "hand-written test covers")
    return 0


if __name__ == "__main__":
    sys.exit(main())
