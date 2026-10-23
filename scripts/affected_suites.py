"""The test suites test-affected.py and build-test-affected-map.py know about: where each project is, where its map lives, how its fixtures are named.

Two today. The ENGINE suite is the original and keeps every path it always had. The SWG demo's suite has a map of its own because its fixtures are
another project, and its subject is the demo's server, its bots and its own test helpers — a change to `SessionHarness.cs` is a change to every
fixture that waits on it, and the coverage map says which. An edit under `src/` selects from the engine's suite, never from this one.
"""
from __future__ import annotations
import re
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class Suite:
    name: str
    test_proj: str                 # repo-relative .csproj
    test_dir: str                  # repo-relative directory whose *.cs declare the fixtures
    map_path: str                  # repo-relative, committed
    per_fixture_dir: str           # repo-relative, git-ignored cache
    fixture_class: re.Pattern      # captures a fixture's class name
    covered_roots: tuple[str, ...] # source trees a coverage hit is kept for, repo-relative prefixes
    shard_plan: str | None         # repo-relative plan for bench/aws/shard.py, None for the default one
    coverage_settings: str | None = None  # repo-relative dotnet-coverage settings, None to instrument everything
    dir_rules: tuple[tuple[str, tuple[str, ...]], ...] = ()  # (path prefix, fixtures) for files the coverage map cannot see

    @property
    def test_proj_path(self) -> Path:
        return REPO_ROOT / self.test_proj

    @property
    def test_dir_path(self) -> Path:
        return REPO_ROOT / self.test_dir


# Any sequence of modifiers. The fixed shape this replaced — one optional access modifier, then one of static/sealed/abstract — never matched
# `unsafe class`, `sealed partial class` or `public unsafe class`, so those fixtures were never discovered, never collected, and never selected by
# test-affected.py: AabbClusterEnumeratorDrainTests (`unsafe class`) was invisible to the map on the day it was written.
_MODIFIERS = r"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|unsafe|partial|file)\s+)*class\s+"

ENGINE = Suite(
    name="engine",
    test_proj="test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj",
    test_dir="test/Typhon.Engine.Tests",
    map_path="coverage/test-affected-map.json",
    per_fixture_dir="coverage/per_fixture",
    fixture_class=re.compile(_MODIFIERS + r"(\w+(?:Tests?|Spec))\b", re.M),
    covered_roots=("src/",),
    shard_plan=None,
)

# The demo names most fixtures `...Checks`, and a few `...Tests`.
SWG = Suite(
    name="swg",
    test_proj="demo/SwgTatooine/SwgTatooine.Tests/SwgTatooine.Tests.csproj",
    test_dir="demo/SwgTatooine/SwgTatooine.Tests",
    map_path="coverage/test-affected-map-swg.json",
    per_fixture_dir="coverage/per_fixture/swg",
    fixture_class=re.compile(_MODIFIERS + r"(\w+(?:Checks|Tests?))\b", re.M),
    covered_roots=("demo/SwgTatooine/",),
    shard_plan="bench/aws/shards-swg.json",
    coverage_settings="scripts/coverage-swg.settings.xml",
    # The terrain namespace is not instrumented (scripts/coverage-swg.settings.xml: instrumented, its bake cost 110 s per fixture), so the map has no
    # entry for its files. These are the fixtures whose subject it is.
    dir_rules=(("demo/SwgTatooine/SwgTatooine.Server/World/Terrain/",
                ("TerrainAltitudeChecks", "TerrainBandChecks", "TerrainGoldenChecks", "SlopeSpeedChecks", "StandingSurfaceChecks")),),
)

SUITES = {s.name: s for s in (ENGINE, SWG)}


def suite_of(rel: str) -> Suite | None:
    """The suite an edited repo-relative path belongs to, or None when no .NET suite covers it (the browser client, docs)."""
    if rel.startswith("demo/SwgTatooine/SwgTatooine.Client/"):
        return None
    if rel.startswith("demo/SwgTatooine/"):
        return SWG
    return ENGINE
