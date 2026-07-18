#!/usr/bin/env bash
#
# Consumer end-to-end smoke test for the `Typhon` NuGet package (Feature #435, #427 AC7).
#
# This is the anti-"packaging silently broke" gate. From a *clean* local feed it:
#   1. installs the `Typhon` package into a fresh console project,
#   2. defines a [Component]/[Archetype] model,
#   3. builds it — `Unit.ReadAll(...)` compiles ONLY if the ArchetypeAccessorGenerator shipped
#      inside the package (analyzers/dotnet/cs) AND ran in the *consumer's* compilation,
#   4. runs it — opening a real DB, spawning an entity, and reading it back two ways
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

# Named namespace here for a realistic multi-file layout. Global (unnamed) namespace is ALSO supported since #505 —
# the top-level-statement console shape — and is covered by Typhon.Generators.Tests.ArchetypeAccessorGeneratorTests.
cat > Model.cs <<'CS'
using Typhon.Engine;            // Archetype<T>, Comp<T>
using Typhon.Schema.Definition; // [Component], [Archetype], StorageMode

namespace ConsumerSmoke;

[Component("Smoke.Position", 1, StorageMode = StorageMode.Versioned)]
public struct Position
{
    public float X, Y;
    public Position(float x, float y) { X = x; Y = y; }
}

[Component("Smoke.Health", 1, StorageMode = StorageMode.Versioned)]
public struct Health
{
    public int Current, Max;
    public Health(int current, int max) { Current = current; Max = max; }
}

[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}
CS

cat > Program.cs <<'CS'
using System;
using Typhon.Engine;
using Typhon.Schema.Definition;
using ConsumerSmoke;

using var dbe = DatabaseEngine.Open("smoke.typhon", o => o
    .Register<Position>()
    .Register<Health>()
    .RegisterArchetype<Unit>());

EntityId soldier;
using (var tx = dbe.CreateQuickTransaction())
{
    soldier = tx.Spawn<Unit>(
        Unit.Position.Set(new Position(10, 20)),
        Unit.Health.Set(new Health(100, 100)));
    tx.Commit();
}

// (a) Runtime read via the engine API.
using (var tx = dbe.CreateQuickTransaction())
{
    var e = tx.Open(soldier);
    var pos = e.Read(Unit.Position);
    var hp = e.Read(Unit.Health);
    if (hp.Current != 100 || hp.Max != 100 || pos.X != 10f || pos.Y != 20f)
        throw new Exception($"runtime read mismatch: HP {hp.Current}/{hp.Max} at ({pos.X},{pos.Y})");
}

// (b) GENERATED accessor. `Unit.ReadAll` exists ONLY if the ArchetypeAccessorGenerator shipped in the
//     package and ran in this consumer compilation — this line is the crux of the smoke test.
using (var tx = dbe.CreateQuickTransaction())
{
    var u = Unit.ReadAll(tx, soldier);
    if (u.Health.Current != 100 || u.Position.X != 10f)
        throw new Exception($"generated ReadAll mismatch: hp={u.Health.Current} pos.x={u.Position.X}");
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
