#!/usr/bin/env python3
"""Validate the T5 linearizability model against a KNOWN-RACY build (#705 T5 / design §9).

The sibling of `axis-mutation-probe.py`, and it exists for the same reason with the sign flipped. That probe asks
"does the covering array explore?"; this one asks "can the model DETECT a race?" — because §9 of
`claude/design/Infrastructure/test-strategy.md` names the trap explicitly:

    "T5's model finds nothing after substantial seed-hours → either the model is too weak (likely) or the
     concurrency bugs are already gone (unlikely at 23 % of the taxonomy). Audit the model against a
     KNOWN-RACY build before believing it."

A model that has never rejected anything is not evidence, however many seeds it has burned.

## Both halves are measured, and that is the point

    baseline (unmutated src)  → is the model GREEN?
    mutated  (race planted)   → does the model go RED, and after how many seeds?

Only "baseline green AND mutated red" proves detection. A probe that checked the mutated half alone would report
success on a model that fails on everything — which is exactly the state of the world TODAY:

    #400 is OPEN, and the model reproduces it on the default configuration within milliseconds.

So the baseline is currently RED **for a real reason**, and this script says so rather than pretending. That is a
distinct exit status (BASELINE_RED, 2), not a pass and not a failure of the probe itself. When #400 is fixed the
baseline should turn green and the full two-sided verdict becomes available; until then the honest statement is
"the model demonstrably detects a real race, and the planted-race check is pending a green baseline".

## Safety

Refuses to run on a dirty `src/` tree, and ALWAYS restores the file and rebuilds — the rebuild matters as much as
the revert, because leaving a mutated binary in `bin/` poisons the next `--no-build` run with phantom failures that
look like unrelated regressions (learned the hard way in #704).
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
TEST_PROJ = "test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj"

# The model's own fixture. Quarantined against #400, so it must be selected by NAME rather than by the default
# gate filter, which excludes it.
MODEL_FILTER = "FullyQualifiedName~LinearizabilityTests.ParallelOperations_AreLinearizable&FullyQualifiedName!~Deep"
MODEL_TEST = "ParallelOperations_AreLinearizable"

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_BASELINE_RED = 2


@dataclass
class RaceMutation:
    """One deliberately planted race, plus why the model should be able to see it."""

    name: str
    rationale: str
    path: str
    find: str
    replace: str
    seeds: int = 3  # how many seeded attempts the model gets before we call it undetected
    tags: list = field(default_factory=list)


MUTATIONS = [
    RaceMutation(
        name="unguarded-changeset",
        rationale=(
            "Remove the ChangeSet concurrent-mutation guard's rejection, leaving the shared Dictionary mutated from "
            "several threads with nothing objecting. This is #400's mechanism with its detector taken away, so a model "
            "that cannot see it cannot see #400 either — it would be relying entirely on the guard, and would report "
            "nothing the moment the guard were relaxed. The model has to catch the CONSEQUENCE (a lost or duplicated "
            "entity in the final population), not the assertion."
        ),
        path="src/Typhon.Engine/Storage/internals/ChangeSet.cs",
        find="        throw new InvalidOperationException(\n"
        "            $\"ChangeSet concurrent mutation: thread {me} entered {member} while thread {prev} was still inside a mutating method.",
        replace="        _mutationDepth++;\n"
        "        if (false) throw new InvalidOperationException(\n"
        "            $\"ChangeSet concurrent mutation: thread {me} entered {member} while thread {prev} was still inside a mutating method.",
        tags=["#400"],
    ),
]


def run(cmd, **kw):
    return subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, shell=False, **kw)


def src_is_dirty() -> str:
    r = run(["git", "status", "--porcelain", "--", "src/"])
    return r.stdout.strip()


def apply_mutation(m: RaceMutation) -> str:
    """Applies the mutation and returns the ORIGINAL file text for verbatim restoration."""
    p = REPO / m.path
    original = p.read_text(encoding="utf-8")
    n = original.count(m.find)
    if n != 1:
        raise SystemExit(
            f"::error::mutation '{m.name}' is STALE — its anchor occurs {n} times in {m.path}, expected exactly 1.\n"
            f"  anchor: {m.find[:120]}...\n"
            "A mutation that no longer applies probes nothing; update the anchor or retire the mutation."
        )
    p.write_text(original.replace(m.find, m.replace), encoding="utf-8")
    return original


def build() -> bool:
    r = run(["dotnet", "build", TEST_PROJ, "-c", "Debug"])
    if r.returncode != 0:
        print(r.stdout[-4000:])
        print(r.stderr[-2000:])
    return r.returncode == 0


def parse_outcomes(results_dir: Path) -> dict:
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


def model_detects(results_root: Path, label: str, seeds: int) -> tuple[bool, int]:
    """Run the model up to `seeds` times. Returns (detected, seeds_used).

    Each attempt gets a distinct TYPHON_TEST_SEED so a race that only shows on some interleavings still gets its
    chances, and the SEED COUNT is reported — "detected on attempt 1" and "detected on attempt 3" are different
    statements about how sharp the model is, and collapsing them to a boolean throws that away.
    """
    for attempt in range(1, seeds + 1):
        out = results_root / f"{label}-{attempt}"
        if out.exists():
            shutil.rmtree(out)
        out.mkdir(parents=True, exist_ok=True)

        env_seed = str(0x5EED_0001 + attempt * 7919)
        r = subprocess.run(
            [
                "dotnet", "test", TEST_PROJ, "-c", "Debug", "--no-build",
                "--filter", MODEL_FILTER,
                "--logger", "trx",
                "--results-directory", str(out),
            ],
            cwd=REPO,
            capture_output=True,
            text=True,
            env={**_env(), "TYPHON_TEST_SEED": env_seed},
        )
        outcomes = parse_outcomes(out)
        matched = {k: v for k, v in outcomes.items() if MODEL_TEST in k}
        if not matched:
            print(f"    attempt {attempt}: the filter selected NO test — the probe measured nothing")
            print(r.stdout[-1500:])
            return (False, attempt)

        failed = [k for k, v in matched.items() if v != "Passed"]
        print(f"    attempt {attempt} (seed {env_seed}): {len(matched)} selected, {len(failed)} failed")
        if failed:
            return (True, attempt)

    return (False, seeds)


def _env() -> dict:
    import os

    return dict(os.environ)


def probe(m: RaceMutation, results_root: Path, baseline_green: bool) -> bool:
    print(f"\n=== {m.name} ===")
    print(f"    {m.rationale}")

    original = apply_mutation(m)
    try:
        if not build():
            print("::error::the MUTATED tree does not build — the mutation is malformed, not the model")
            return False

        detected, seeds_used = model_detects(results_root, f"mutated-{m.name}", m.seeds)
    finally:
        (REPO / m.path).write_text(original, encoding="utf-8")
        # Rebuild inside the finally: reverting the SOURCE while leaving a mutated DLL in bin/ makes the next
        # --no-build run report failures that no longer exist in the tree (#704 learned this the expensive way).
        build()

    if not baseline_green:
        print(
            f"    mutated build: {'DETECTED' if detected else 'not detected'} after {seeds_used} seed(s) — but the "
            "BASELINE is red, so this is not evidence of detection either way."
        )
        return False

    if detected:
        print(f"    VERDICT: detected the planted race after {seeds_used} seed(s), on a green baseline. ✅")
        return True

    print(
        f"::error::the model did NOT detect '{m.name}' in {m.seeds} seeds on a green baseline. The model is too weak: "
        "it is passing a build that is deliberately racy, so its green results carry no information."
    )
    return False


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--all", action="store_true", help="run every stored mutation (default)")
    ap.add_argument("--list", action="store_true", help="list the mutations and exit")
    args = ap.parse_args()

    if args.list:
        for m in MUTATIONS:
            print(f"{m.name}: {m.rationale}")
        return EXIT_OK

    dirty = src_is_dirty()
    if dirty:
        print("::error::src/ has uncommitted changes; the probe edits and restores source files and refuses to run "
              "where that could destroy work.\n" + dirty)
        return EXIT_FAIL

    results_root = REPO / "coverage" / "linearizability-probe"
    if results_root.exists():
        shutil.rmtree(results_root)
    results_root.mkdir(parents=True, exist_ok=True)

    print("=== baseline (unmutated src) ===")
    if not build():
        print("::error::the unmutated tree does not build")
        return EXIT_FAIL

    baseline_detected, baseline_seeds = model_detects(results_root, "baseline", 1)
    baseline_green = not baseline_detected
    if baseline_green:
        print("    baseline: model GREEN — a planted race can now be attributed to the mutation. ✅")
    else:
        print(
            "    baseline: model RED on unmutated source. Expected while #400 is open — the model reproduces the "
            "shared-ChangeSet race on the default configuration. The planted-race check needs a green baseline to "
            "mean anything, so it is reported below but not counted."
        )

    ok = True
    for m in MUTATIONS:
        ok &= probe(m, results_root, baseline_green)

    if not baseline_green:
        print(
            "\nRESULT: BASELINE_RED. The model demonstrably rejects a REAL race (#400) on unmutated source, which is "
            "stronger evidence of detection power than any planted fault — but the planted-race gate stays pending "
            "until #400 is fixed and the baseline turns green."
        )
        return EXIT_BASELINE_RED

    print("\nRESULT: " + ("all mutations detected ✅" if ok else "at least one mutation went UNDETECTED ❌"))
    return EXIT_OK if ok else EXIT_FAIL


if __name__ == "__main__":
    sys.exit(main())
