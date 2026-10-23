#!/usr/bin/env python3
"""Run per-fixture coverage to build coverage/test-affected-map.json.

INCREMENTAL by default — only re-runs coverage for fixtures whose test source
file is newer than the cached XML. Initial build: ~25 min. Steady-state rebuild
after a normal commit: seconds.

USAGE
    python3 scripts/build-test-affected-map.py            # incremental (default)
    python3 scripts/build-test-affected-map.py --force    # full rebuild
    python3 scripts/build-test-affected-map.py --suite swg  # the SWG demo's map (coverage/test-affected-map-swg.json)

SUITES (scripts/affected_suites.py): `engine` is test/Typhon.Engine.Tests and the map below; `swg` is the demo's suite,
whose map keys the engine's src/ AND the demo's own trees (server, bots, test helpers). Each suite has its own cache.

Cache: coverage/per_fixture/<fixture>.xml is the per-fixture cobertura output.
The JSON map at coverage/test-affected-map.json is re-pivoted from cached XMLs
on every run, regardless of whether any fixtures were re-collected.

Staleness rule: a fixture's cached XML is considered FRESH iff the test source
file declaring that fixture's class has mtime <= the XML's mtime. This catches
edits to the fixture itself (added/removed/changed test methods). It does NOT
invalidate the cache when src/ changes — the map is a "coverage snapshot at the
time of the last collection," and the relationship between fixtures and the
files they touch is stable across small src edits. After a refactor that moves
classes between files, run --force.
"""
from __future__ import annotations
import argparse
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from affected_suites import ENGINE, REPO_ROOT, SUITES, Suite  # noqa: E402

# Rebound by main() from --suite; the engine's by default, so every path is the one it always was.
SUITE: Suite = ENGINE
TEST_PROJ = ENGINE.test_proj
TEST_DIR = ENGINE.test_dir_path
PER_FIXTURE_DIR = REPO_ROOT / ENGINE.per_fixture_dir
MAP_PATH = REPO_ROOT / ENGINE.map_path
FIXTURES_LIST = REPO_ROOT / "coverage/fixtures.txt"
FIXTURE_CLASS_RE = ENGINE.fixture_class


def use_suite(suite: Suite) -> None:
    global SUITE, TEST_PROJ, TEST_DIR, PER_FIXTURE_DIR, MAP_PATH, FIXTURES_LIST, FIXTURE_CLASS_RE
    SUITE, TEST_PROJ, TEST_DIR = suite, suite.test_proj, suite.test_dir_path
    PER_FIXTURE_DIR, MAP_PATH = REPO_ROOT / suite.per_fixture_dir, REPO_ROOT / suite.map_path
    FIXTURES_LIST = REPO_ROOT / "coverage/fixtures.txt" if suite is ENGINE else PER_FIXTURE_DIR / "fixtures.txt"
    FIXTURE_CLASS_RE = suite.fixture_class


def repo_relative(filename: str) -> str | None:
    """A cobertura filename as a repo-relative path, or None. The build's PathMap writes `/_/` for the repo root; a path without it falls back to
    the last `src/`, which is what this script always did."""
    filename = filename.replace("\\", "/")
    if "/_/" in filename:
        return filename[filename.rfind("/_/") + 3:]
    idx = filename.rfind("src/")
    return filename[idx:] if idx != -1 else None


def discover_fixtures() -> dict[str, Path]:
    """Find test fixture class names by scanning *.cs files for `class XxxTests`.

    Returns: {fixture_name: source_test_file}.
    Falls back to FIXTURES_LIST (with no source-file info) if scanning fails.
    """
    fixtures: dict[str, Path] = {}
    if not TEST_DIR.exists():
        return fixtures
    for p in TEST_DIR.rglob("*.cs"):
        if "obj" in p.parts or "bin" in p.parts:
            continue
        try:
            text = p.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        for name in FIXTURE_CLASS_RE.findall(text):
            # If the same fixture appears in multiple files (partial classes),
            # remember the most-recently-modified one for staleness checks.
            existing = fixtures.get(name)
            if existing is None or p.stat().st_mtime > existing.stat().st_mtime:
                fixtures[name] = p
    return fixtures


def collect_fixture(fixture: str, output_xml: Path) -> bool:
    """Run dotnet-coverage for one fixture. Returns True on success."""
    output_xml.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "dotnet-coverage", "collect",
        "-o", str(output_xml),
        "-f", "cobertura",
        *(["-s", str(REPO_ROOT / SUITE.coverage_settings)] if SUITE.coverage_settings else []),
        "dotnet", "test", TEST_PROJ,
        "--no-build", "-c", "Debug",
        "--filter", f"FullyQualifiedName~{fixture}.",
    ]
    proc = subprocess.run(cmd, cwd=REPO_ROOT, capture_output=True, text=True)
    ok = proc.returncode == 0 and output_xml.exists()
    if not ok and output_xml.exists():
        # dotnet-coverage writes its report even when the test run failed or timed out — then a bare 181-byte <coverage> element with no class in it.
        # Left on disk, every later incremental run found it newer than the test source, called it fresh and mapped the fixture to NOTHING; --only too,
        # since a listed fixture still goes through the freshness check. Nine fixtures sat unmapped that way. A failed run that did record coverage
        # (a failing test, not a dead one) keeps it.
        try:
            empty = "<class " not in output_xml.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            empty = True
        if empty:
            for stale in (output_xml, output_xml.with_suffix(".parsed.json")):
                stale.unlink(missing_ok=True)
    return ok


def parse_per_file_coverage(cobertura_xml: Path) -> dict[str, int]:
    """Return {src_file: hit_line_count} for this fixture's coverage.

    Cached as a sibling .parsed.json file. The cache is invalidated when the
    cobertura XML's mtime is newer than the JSON's mtime.
    """
    cache_path = cobertura_xml.with_suffix(".parsed.json")
    if cache_path.exists() and cache_path.stat().st_mtime >= cobertura_xml.stat().st_mtime:
        try:
            with cache_path.open() as fh:
                return json.load(fh)
        except (json.JSONDecodeError, OSError):
            pass  # fall through to fresh parse

    tree = ET.parse(cobertura_xml)
    root = tree.getroot()
    counts: dict[str, int] = {}
    for cls in root.findall(".//class"):
        rel = repo_relative(cls.get("filename", ""))
        if not rel or "/obj/" in rel or not rel.startswith(SUITE.covered_roots):
            continue
        hits = sum(1 for line in cls.findall(".//line") if int(line.get("hits", 0)) > 0)
        if hits > 0:
            counts[rel] = counts.get(rel, 0) + hits

    try:
        with cache_path.open("w") as fh:
            json.dump(counts, fh)
    except OSError:
        pass  # cache is best-effort
    return counts


def is_xml_fresh(xml_path: Path, test_source: Path | None) -> bool:
    """A cached XML is fresh iff it exists, the test source is known, and the
    test source's mtime <= the XML's mtime."""
    if not xml_path.exists():
        return False
    if test_source is None or not test_source.exists():
        return False  # Defensive — if we can't verify the source, re-collect.
    return test_source.stat().st_mtime <= xml_path.stat().st_mtime


def main():
    ap = argparse.ArgumentParser(formatter_class=argparse.RawDescriptionHelpFormatter, description=__doc__)
    ap.add_argument("--force", action="store_true",
                    help="Re-collect every fixture, ignoring any cached XML.")
    ap.add_argument("--only", nargs="+", default=None,
                    help="Re-collect only these fixtures (overrides incremental check). Useful for fixing failed runs.")
    ap.add_argument("--suite", choices=sorted(SUITES), default=ENGINE.name, help="Which suite's map to build (default: engine).")
    args = ap.parse_args()
    use_suite(SUITES[args.suite])

    fixtures = discover_fixtures()
    if fixtures:
        FIXTURES_LIST.parent.mkdir(parents=True, exist_ok=True)
        FIXTURES_LIST.write_text("\n".join(sorted(fixtures.keys())) + "\n")
    elif FIXTURES_LIST.exists():
        # Defensive fallback — no source-file info, so all are treated as needing re-run.
        fixtures = {line.strip(): None for line in FIXTURES_LIST.read_text().splitlines() if line.strip()}
    if not fixtures:
        print("could not discover fixtures (scan failed AND no cached list)")
        return 1

    only_set = set(args.only) if args.only else None

    fixture_file_hits: dict[str, dict[str, int]] = {}
    failed: list[str] = []
    skipped_count = 0
    collected_count = 0

    fixture_names = sorted(fixtures.keys())
    print(f"# {len(fixture_names)} fixtures discovered (mode: {'force' if args.force else 'incremental'})")

    for i, fixture in enumerate(fixture_names, 1):
        xml_path = PER_FIXTURE_DIR / f"{fixture}.xml"
        test_source = fixtures.get(fixture)

        # Decide whether to (re-)collect.
        needs_collect = True
        if not args.force:
            if only_set is not None and fixture not in only_set:
                # In --only mode, leave non-listed fixtures alone (use cached if present).
                needs_collect = False if xml_path.exists() else True
            elif is_xml_fresh(xml_path, test_source):
                needs_collect = False

        if needs_collect:
            print(f"[{i}/{len(fixture_names)}] {fixture}: collecting...")
            ok = collect_fixture(fixture, xml_path)
            if not ok:
                print(f"[{i}/{len(fixture_names)}] {fixture}: FAILED")
                failed.append(fixture)
                continue
            collected_count += 1
        else:
            skipped_count += 1

        if not xml_path.exists():
            failed.append(fixture)
            print(f"[{i}/{len(fixture_names)}] {fixture}: no cached XML and no run — skipping")
            continue

        try:
            counts = parse_per_file_coverage(xml_path)
        except ET.ParseError as e:
            print(f"[{i}/{len(fixture_names)}] {fixture}: parse error {e}")
            failed.append(fixture)
            continue
        fixture_file_hits[fixture] = counts

    print(f"\n# Collected fresh: {collected_count}, used cache: {skipped_count}, failed: {len(failed)}")

    # Pivot to file -> [primary fixtures].
    RATIO_THRESHOLD = 0.50
    MIN_HITS = 5

    file_fixtures: dict[str, list[tuple[str, int]]] = defaultdict(list)
    for fix, counts in fixture_file_hits.items():
        for f, hits in counts.items():
            file_fixtures[f].append((fix, hits))

    out: dict[str, list[str]] = {}
    for f, pairs in file_fixtures.items():
        pairs = [(fx, h) for fx, h in pairs if h >= MIN_HITS]
        if not pairs:
            continue
        max_hits = max(h for _, h in pairs)
        threshold = max(MIN_HITS, max_hits * RATIO_THRESHOLD)
        primary = sorted([fx for fx, h in pairs if h >= threshold])
        if primary:
            out[f] = primary

    MAP_PATH.parent.mkdir(parents=True, exist_ok=True)
    with MAP_PATH.open("w") as fh:
        json.dump(out, fh, indent=2, sort_keys=True)
    print(f"\nSaved {MAP_PATH} ({len(out)} files mapped)")

    lengths = sorted(len(v) for v in out.values())
    if lengths:
        print(f"Fixtures per file — min/median/p75/p95/max: "
              f"{lengths[0]}/{lengths[len(lengths)//2]}/{lengths[len(lengths)*3//4]}/"
              f"{lengths[int(len(lengths)*0.95)]}/{lengths[-1]}")
        print(f"  ≤5 fixtures (clean local edit): {sum(1 for l in lengths if l <= 5)} files")
        print(f"  ≥50 fixtures (cross-cutting → full suite): {sum(1 for l in lengths if l >= 50)} files")
    if failed:
        print(f"\nFailed fixtures: {len(failed)}")
        for f in failed[:20]:
            print(f"  {f}")
        if len(failed) > 20:
            print(f"  ... and {len(failed) - 20} more")
        print("(retry just these with: scripts/build-test-affected-map.py --only " + " ".join(failed[:5]) + " ...)")


if __name__ == "__main__":
    sys.exit(main() or 0)
