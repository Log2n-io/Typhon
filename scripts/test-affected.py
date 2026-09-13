#!/usr/bin/env python3
"""test-affected — Run only the unit-test fixtures relevant to the files you edited.

USAGE
    python scripts/test-affected.py <file1> [<file2> ...]

WHAT IT DOES
    For each provided file path:
      - If the file is itself a test (under test/), the fixture IS the file.
      - Otherwise, look up the file in coverage/test-affected-map.json and union
        the fixtures that exercise it. Fall back to a naming-convention guess
        (Foo.cs -> FooTests, FooTest) if the map has no entry.
    Then:
      - If the union of affected fixtures is small (< THRESHOLD of total), prints
        the dotnet test --filter command and runs it.
      - If the union is too broad, prints a notice and runs the full suite instead — sharded the way the merge gate runs it
        (bench/aws/shard.py run), or as one process with --single-process.

DESIGN
    The map at coverage/test-affected-map.json is the source of truth and is
    expected to be regenerated periodically (per-fixture coverage runs — see
    scripts/build-test-affected-map.py). When the map is stale or missing, this
    script falls back to a naming-convention heuristic, which is correct for
    ~70 % of edits in this codebase.

OUTPUT
    Prints what it intends to run, then runs it. Exit code = `dotnet test` exit code.
"""
from __future__ import annotations
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TEST_PROJ = REPO_ROOT / "test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj"
MAP_PATH = REPO_ROOT / "coverage/test-affected-map.json"
THRESHOLD = 0.50  # If >50 % of fixtures are affected, just run the full suite.


def load_map():
    if not MAP_PATH.exists():
        return None
    with MAP_PATH.open() as fh:
        return json.load(fh)


def normalize_path(p: str) -> str:
    """Returns repo-relative POSIX path."""
    abs_p = Path(p).resolve()
    try:
        rel = abs_p.relative_to(REPO_ROOT)
    except ValueError:
        rel = Path(p)
    return str(rel).replace("\\", "/")


def is_test_file(rel: str) -> bool:
    return rel.startswith("test/")


def fixture_from_test_file(rel: str) -> str:
    """test/Typhon.Engine.Tests/Foo/BarTests.cs -> BarTests"""
    return Path(rel).stem


def naming_convention_guesses(rel: str) -> list[str]:
    """src/Typhon.Engine/Foo/Bar.cs -> [BarTests, BarTest, BarTests<short variants>]
    Handles partial-class file conventions: Foo.Bar.cs -> FooTests etc."""
    stem = Path(rel).stem  # e.g., "Bar" or "Foo.NodeWrapper"
    base = stem.split(".")[0]  # 'Foo' from 'Foo.NodeWrapper'
    return [f"{base}Tests", f"{base}Test", f"{stem}Tests", f"{stem}Test"]


def sibling_partial_fixtures(rel: str, coverage_map: dict) -> set[str]:
    """Fixtures of the OTHER files of the same partial class.

    `ArchetypeClusterState.Ray.cs` is the same class as `ArchetypeClusterState.cs` and its other partial files, but coverage is attributed per
    file, so a partial file nobody's fixture happened to execute when the map was built — or one added since — has no entry of its own. The
    naming-convention fallback cannot help either (there is no `ArchetypeClusterStateTests`), and the file came back "unmatched" with the
    tests that exercise the class never run. The siblings' fixtures are the right proxy: they already load and drive the class.
    """
    p = Path(rel)
    prefix = f"{p.parent.as_posix()}/{p.stem.split('.')[0]}"
    out: set[str] = set()
    for key, fixtures in coverage_map.items():
        if key == prefix + ".cs" or (key.startswith(prefix + ".") and key.endswith(".cs")):
            out.update(fixtures)
    return out


def all_fixture_names() -> set[str]:
    """Discover all test fixtures by scanning test/.../*Tests.cs files."""
    test_root = REPO_ROOT / "test/Typhon.Engine.Tests"
    if not test_root.exists():
        return set()
    return {p.stem for p in test_root.rglob("*.cs") if p.stem.endswith(("Tests", "Test"))}


def affected_fixtures(files: list[str], coverage_map: dict | None) -> tuple[set[str], list[str]]:
    """Returns (fixture set, list of unmatched files)."""
    fixtures: set[str] = set()
    unmatched: list[str] = []
    all_fixtures = all_fixture_names()

    for f in files:
        rel = normalize_path(f)
        if is_test_file(rel):
            # The file IS a test. The fixture is the file's stem (or the matching one in all_fixtures).
            stem = fixture_from_test_file(rel)
            if stem in all_fixtures:
                fixtures.add(stem)
            else:
                unmatched.append(rel)
            continue

        # Source file. Look in coverage map first.
        if coverage_map and rel in coverage_map:
            for fx in coverage_map[rel]:
                fixtures.add(fx)
            continue

        # A partial-class file with no entry of its own: the fixtures of its sibling partials.
        if coverage_map:
            siblings = sibling_partial_fixtures(rel, coverage_map)
            if siblings:
                fixtures.update(siblings)
                continue

        # Fallback: naming convention.
        guesses = naming_convention_guesses(rel)
        matched = [g for g in guesses if g in all_fixtures]
        if matched:
            fixtures.update(matched)
        else:
            unmatched.append(rel)

    return fixtures, unmatched


def main():
    ap = argparse.ArgumentParser(
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=__doc__,
        epilog=(
            "EXAMPLES\n"
            "    python3 scripts/test-affected.py src/Typhon.Engine/Foo/Bar.cs\n"
            "    git diff --name-only | python3 scripts/test-affected.py -\n"
            "    git diff --name-only HEAD~3 | python3 scripts/test-affected.py - --dry-run"
        ),
    )
    ap.add_argument("files", nargs="+", help="Files you edited (src/ or test/). Use - to read from stdin.")
    ap.add_argument("--dry-run", action="store_true", help="Print the command without running.")
    ap.add_argument("--config", default="Debug", choices=["Debug", "Release"])
    ap.add_argument("--no-build", action="store_true", default=True)
    ap.add_argument("--build", action="store_true",
                    help="Build the TEST project first. Without it the last build is run, and a test binary older than your edit passes or fails "
                         "the old code.")
    ap.add_argument("--single-process", action="store_true",
                    help="When the FULL suite is chosen, run it as one dotnet test process instead of the merge gate's shards (bench/aws/shard.py).")
    args = ap.parse_args()
    if args.build:
        args.no_build = False

    # If "-" appears, read additional files from stdin
    if "-" in args.files:
        args.files = [f for f in args.files if f != "-"]
        args.files += [line.strip() for line in sys.stdin.read().splitlines() if line.strip()]
    # Filter to .cs files only (git diff often includes .csproj, .md, etc.)
    args.files = [f for f in args.files if f.endswith(".cs")]
    if not args.files:
        print("# No .cs files among inputs — nothing to test.")
        return 0

    coverage_map = load_map()
    map_status = "loaded" if coverage_map else "missing (using naming-convention fallback)"
    print(f"# coverage/test-affected-map.json: {map_status}")

    fixtures, unmatched = affected_fixtures(args.files, coverage_map)
    all_fixtures = all_fixture_names()

    print(f"# Edited files: {len(args.files)}")
    if unmatched:
        print(f"# Unmatched files (no fixture inferred): {unmatched}")

    full_suite = False
    if not fixtures:
        print("# No affected fixtures inferred → running FULL suite for safety.")
        full_suite = True
    elif len(fixtures) >= THRESHOLD * len(all_fixtures):
        print(f"# {len(fixtures)} of {len(all_fixtures)} fixtures affected (>{int(THRESHOLD*100)} %) → running FULL suite.")
        full_suite = True

    if not args.no_build:
        build = ["dotnet", "build", str(TEST_PROJ), "-c", args.config]
        print(f"# Building: {' '.join(build)}")
        if not args.dry_run:
            rc = subprocess.run(build, cwd=REPO_ROOT).returncode
            if rc != 0:
                return rc

    env = None
    if full_suite and not args.single_process:
        # The whole suite the way the merge gate runs it: 8 single-worker processes, then the serial Sensitive pass, then a retry of what failed. About
        # half the wall time of one process (46 s against 92 s on a 7950X), because one process runs the 120 [NonParallelizable] fixtures one at a
        # time — and this is the branch every edit to a foundational file lands in: 79 of the map's 462 files each reach more than 250 fixtures.
        cmd = [sys.executable, str(REPO_ROOT / "bench/aws/shard.py"), "run", "--results-dir", str(TEST_PROJ.parent / "TestResults" / "affected-shards")]
        env = {**os.environ, "SHARD_REPO": str(REPO_ROOT), "SHARD_CONFIG": args.config}
    elif full_suite:
        cmd = ["dotnet", "test", str(TEST_PROJ), "-c", args.config, "--no-build", "--filter", "TestCategory!=Quarantine"]
    else:
        # Build the filter expression.
        sorted_fix = sorted(fixtures)
        filter_expr = "|".join(f"FullyQualifiedName~{f}." for f in sorted_fix)
        print(f"# Affected fixtures ({len(sorted_fix)}): {', '.join(sorted_fix)}")
        cmd = ["dotnet", "test", str(TEST_PROJ), "-c", args.config, "--no-build", "--filter", filter_expr]

    print(f"# Running: {' '.join(cmd)}")
    if args.dry_run:
        return 0

    return subprocess.run(cmd, cwd=REPO_ROOT, env=env).returncode


if __name__ == "__main__":
    sys.exit(main())
