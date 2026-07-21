#!/usr/bin/env bash
#
# Consumer end-to-end smoke test for the `Typhon` NuGet package (Feature #435, #427 AC7).
#
# This is the anti-"packaging silently broke" gate. From a *clean* local feed it:
#   1. installs the `Typhon` package into a fresh console project,
#   2. compiles the canonical SWG Light schema source (the same .cs the `typhon new` scaffold
#      emits — samples/Typhon.Samples.Swg/Light/) STANDALONE against the package (#531),
#   3. builds it — `Harvester.ReadAll(...)` compiles ONLY if the ArchetypeAccessorGenerator shipped
#      inside the package (analyzers/dotnet/cs) AND ran in the *consumer's* compilation,
#   4. runs it — opening a real DB, spawning a Harvester, and reading it back two ways
#      (runtime `Read` + generated `ReadAll`).
#
# The transitive dependencies (MemoryPack, K4os.LZ4, Microsoft.Extensions.*, diagnostics) are
# restored from nuget.org; only the `Typhon` package itself comes from the local feed.
#
# Usage:  scripts/consumer-smoke-test.sh <feed-dir>
#   <feed-dir>   directory containing Typhon.<version>.nupkg  (e.g. the `dotnet pack -o` output)
#
# Exit 0 = PASS. Any non-zero = FAIL (build error, generator missing, runtime mismatch).
set -euo pipefail

# Absolute script dir, captured BEFORE any `cd` — the SWG Light source is resolved relative to it below.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

FEED="${1:?usage: consumer-smoke-test.sh <feed-dir>}"
# Windows-form absolute path when on Git Bash/MSYS (`pwd -W`), native path on Linux/CI (`pwd`).
# The .NET CLI is a native Windows process and cannot resolve an MSYS `/c/...` path in nuget.config.
FEED="$(cd "$FEED" && { pwd -W 2>/dev/null || pwd; })"

# Discover the packed version from the .nupkg filename (ignore the .snupkg symbol package).
NUPKG="$(ls "$FEED"/Typhon.*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)"
[ -n "$NUPKG" ] || { echo "smoke: no Typhon.*.nupkg found in $FEED"; exit 1; }
VERSION="$(basename "$NUPKG" | sed -E 's/^Typhon\.(.*)\.nupkg$/\1/')"
echo "smoke: testing package 'Typhon' $VERSION from $FEED"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="typhon-local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

cat > smoke.csproj <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <!-- Exact pinned prerelease version resolves without an explicit prerelease flag. -->
    <PackageReference Include="Typhon" Version="$VERSION" />
  </ItemGroup>
</Project>
XML

# Compile the ACTUAL SWG Light schema source against the package (not a hand-maintained copy) — this is what
# proves the sample the scaffold emits is package-compatible standalone, and can never drift from the real source.
LIGHT_SRC="$SCRIPT_DIR/../samples/Typhon.Samples.Swg/Light"
[ -d "$LIGHT_SRC" ] || { echo "smoke: SWG Light source not found at $LIGHT_SRC"; exit 1; }
cp "$LIGHT_SRC"/*.cs "$WORK/"
echo "smoke: using SWG Light schema from $LIGHT_SRC"

cat > Program.cs <<'CS'
using System;
using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Samples.Swg;

// Footprint carries a [SpatialIndex], so Harvester is cluster-eligible — a grid is required before the archetypes wire.
using var dbe = DatabaseEngine.Open("smoke.typhon", o => o
    .Register<Position>().Register<Footprint>().Register<Cargo>().Register<Drift>().Register<Extractor>()
    .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));
    // archetypes self-register at assembly load (#514) — no RegisterArchetype call

EntityId drone;
using (var tx = dbe.CreateQuickTransaction())
{
    drone = tx.Spawn<Harvester>(
        Harvester.Position.Set(new Position { P = new Point2F { X = 10, Y = 20 } }),
        Harvester.Footprint.Set(new Footprint { Box = new AABB2F { MinX = 10, MaxX = 10, MinY = 20, MaxY = 20 } }),
        Harvester.Cargo.Set(new Cargo { Amount = 250, Capacity = 1000 }),
        Harvester.Drift.Set(new Drift { Dx = 0, Dy = 0 }),
        Harvester.Extractor.Set(new Extractor { ResourceKind = 1, Rate = 5 }));
    tx.Commit();
}

// (a) Runtime read via the engine API.
using (var tx = dbe.CreateQuickTransaction())
{
    var e = tx.Open(drone);
    var pos = e.Read(Harvester.Position);
    var cargo = e.Read(Harvester.Cargo);
    if (cargo.Amount != 250 || cargo.Capacity != 1000 || pos.P.X != 10f || pos.P.Y != 20f)
        throw new Exception($"runtime read mismatch: cargo {cargo.Amount}/{cargo.Capacity} at ({pos.P.X},{pos.P.Y})");
}

// (b) GENERATED accessor. `Harvester.ReadAll` exists ONLY if the ArchetypeAccessorGenerator shipped in the
//     package and ran in this consumer compilation — this line is the crux of the smoke test.
using (var tx = dbe.CreateQuickTransaction())
{
    var h = Harvester.ReadAll(tx, drone);
    if (h.Cargo.Amount != 250 || h.Position.P.X != 10f)
        throw new Exception($"generated ReadAll mismatch: cargo={h.Cargo.Amount} pos.x={h.Position.P.X}");
}

Console.WriteLine("SMOKE OK: package installed, generator fired, DB spawn+read verified.");
CS

# NuGet's global cache keys on ID+version and does NOT re-extract a same-version re-pack. When iterating
# locally the version is fixed (MinVer height), so evict the Typhon package to always test fresh content.
# (In CI every build has a unique version, so this is a no-op there.)
rm -rf "${HOME}/.nuget/packages/typhon" 2>/dev/null || true

echo "smoke: building consumer (the generator must fire here)..."
dotnet build smoke.csproj -c Release -v quiet
echo "smoke: running consumer..."
OUT="$(dotnet run --project smoke.csproj -c Release --no-build)"
echo "  > $OUT"
echo "$OUT" | grep -q "SMOKE OK" || { echo "smoke: FAIL — expected marker not found"; exit 1; }
echo "smoke: PASS"
