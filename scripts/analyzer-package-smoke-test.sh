#!/usr/bin/env bash
#
# Analyzer packaging smoke test (#1584): the diagnostics a NuGet consumer is promised actually reach a consumer's build.
#
# Every in-repo project references the analyzers by PROJECT, so a packaging regression (an analyzer assembly dropped from analyzers/dotnet/cs, an
# engine-only rule leaking into the package) is invisible to the solution build. From a clean local feed this script:
#   1. builds a fresh project that references the `Typhon` package and commits one violation per shipped rule (TYPHON001, 003, 004, 005, 006, 007,
#      009, 010, 011, 012, 013) — the build must FAIL and report every one of them;
#   2. checks the engine-only rules stay out of the package: the same project declares a ref struct that holds a no-copy enumerator by value, which the
#      engine's TYPHON014 would flag — it must not appear;
#   3. builds a client project that references `Typhon.Client` (Typhon.Protocol flows its analyzers) and sends a command by the name of its
#      [ReplicatedMessage] struct — TYPHON015 must be reported.
#
# Usage:  scripts/analyzer-package-smoke-test.sh <feed-dir>
# Exit 0 = PASS.
set -euo pipefail

FEED="${1:?usage: analyzer-package-smoke-test.sh <feed-dir>}"
FEED="$(cd "$FEED" && { pwd -W 2>/dev/null || pwd; })"

NUPKG="$(ls "$FEED"/Typhon.[0-9]*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)"
[ -n "$NUPKG" ] || { echo "analyzers: no Typhon.*.nupkg found in $FEED"; exit 1; }
VERSION="$(basename "$NUPKG" | sed -E 's/^Typhon\.(.*)\.nupkg$/\1/')"
echo "analyzers: testing package 'Typhon' $VERSION from $FEED"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

write_nuget_config() {
  cat > "$1/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="typhon-local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
}

FAIL=0

# ── 1 + 2: engine package ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
SERVER="$WORK/server"
mkdir -p "$SERVER"
write_nuget_config "$SERVER"
cat > "$SERVER/server.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Typhon" Version="$VERSION" />
  </ItemGroup>
</Project>
XML

cat > "$SERVER/Violations.cs" <<'CS'
using System;
using Typhon.Engine;
using Typhon.Schema.Definition;

// TYPHON010: a long rounds the struct up to 16 bytes, it packs to 12.
[Component("Smoke.Padded", 1)]
public struct Padded { public long A; public int B; }

// TYPHON011: a bool is 1 byte managed, 4 marshalled, so the second flag lands at offset 5 managed but 8 marshalled.
[Component("Smoke.Flag", 1)]
public struct Flag { public int X; public bool On; public bool Off; }

// TYPHON013: 120 bytes, past the 112 a ComponentValue carries inline.
[Component("Smoke.Big", 1)]
public struct Big
{
    public long A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14;
}

[Component("Smoke.Pos", 1, StorageMode = StorageMode.SingleVersion)]
public struct Pos { [SpatialIndex] public AABB2F Box; }

[Archetype]
public sealed partial class Thing : Archetype<Thing>
{
    public static readonly Comp<Pos> Pos = Register<Pos>();
}

public static class Violations
{
    // TYPHON004: a transaction that is never disposed.
    public static void LeakTransaction(DatabaseEngine db)
    {
        var tx = db.CreateQuickTransaction();
        tx.Commit();
    }

    // TYPHON001: a no-copy type taken by value.
    public static void ByValue(ClusterEnumerator<Thing> clusters) { }

    // TYPHON003: a no-copy type copied.
    public static void Copy(ref ClusterEnumerator<Thing> clusters)
    {
        var copy = clusters;
        copy.Dispose();
    }

    // TYPHON012: a non-readonly member called on a read-only receiver.
    public static void DefensiveCopy(in EntityRefMut entity) => entity.Set(Thing.Pos, default(Pos));

    // TYPHON009: a spatial component written through a span instead of WriteSpatial.
    public static void SpatialSpan(ref ClusterRef<Thing> cluster)
    {
        var span = cluster.GetSpan(Thing.Pos);
        span[0].Box.MinX = 1;
    }
}

// TYPHON005: owns a must-dispose transaction, has no Dispose().
public sealed class Holder
{
    private Transaction _tx;
    public void Use(DatabaseEngine db) => _tx = db.CreateQuickTransaction();
}

// TYPHON006: Dispose() does not dispose the critical field.
public sealed class ForgetfulHolder : IDisposable
{
    private Transaction _tx;
    public void Use(DatabaseEngine db) => _tx = db.CreateQuickTransaction();
    public void Dispose() { }
}

// TYPHON007: an early return skips the critical field.
public sealed class EarlyReturnHolder : IDisposable
{
    private Transaction _tx;
    private bool _skip;
    public void Use(DatabaseEngine db) => _tx = db.CreateQuickTransaction();
    public void Dispose()
    {
        if (_skip)
        {
            return;
        }
        _tx.Dispose();
    }
}

// Engine-only TYPHON014 would demand [NoCopy]/[MustDispose] here: a consumer cannot apply those (they are internal), so it must not ship.
public ref struct EnumeratorWrapper
{
    public ClusterEnumerator<Thing> Inner;
}
CS

OUT="$WORK/server.log"
set +e
(cd "$SERVER" && dotnet build -nologo -v q > "$OUT" 2>&1)
RC=$?
set -e
if [ $RC -eq 0 ]; then
  echo "analyzers: FAIL — the violation project built cleanly: no analyzer error reached it"
  FAIL=1
fi
for id in TYPHON001 TYPHON003 TYPHON004 TYPHON005 TYPHON006 TYPHON007 TYPHON009 TYPHON010 TYPHON011 TYPHON012 TYPHON013; do
  if grep -q "$id" "$OUT"; then
    echo "analyzers: ok   $id reported"
  else
    echo "analyzers: FAIL $id not reported"
    FAIL=1
  fi
done
for id in TYPHON008 TYPHON014; do
  if grep -q "$id" "$OUT"; then
    echo "analyzers: FAIL engine-only $id reached a consumer"
    FAIL=1
  else
    echo "analyzers: ok   engine-only $id absent"
  fi
done
if grep -q "CS8032" "$OUT"; then
  echo "analyzers: FAIL an analyzer assembly could not be loaded (CS8032)"
  FAIL=1
fi

# ── 3: client package (TYPHON015 via Typhon.Protocol) ──────────────────────────────────────────────────────────────────────────────────────────────
CLIENT_PKG="$(ls "$FEED"/Typhon.Client.[0-9]*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)"
if [ -z "$CLIENT_PKG" ]; then
  echo "analyzers: FAIL no Typhon.Client.*.nupkg in $FEED"
  FAIL=1
else
  CLIENT="$WORK/client"
  mkdir -p "$CLIENT"
  write_nuget_config "$CLIENT"
  cat > "$CLIENT/client.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Typhon.Client" Version="$VERSION" />
  </ItemGroup>
</Project>
XML
  cat > "$CLIENT/Bot.cs" <<'CS'
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Protocol;

[ReplicatedMessage]
public partial struct MoveCmd
{
    public sbyte Forward;
}

public static class Bot
{
    // TYPHON015: sent by name although the typed struct is in reach.
    public static Task<ushort> Move(TyphonClient client, RecordValues values) => client.SendCommandAsync("MoveCmd", values);
}
CS
  COUT="$WORK/client.log"
  set +e
  (cd "$CLIENT" && dotnet build -nologo -v q > "$COUT" 2>&1)
  set -e
  if grep -q "TYPHON015" "$COUT"; then
    echo "analyzers: ok   TYPHON015 reported in a Typhon.Client project"
  else
    echo "analyzers: FAIL TYPHON015 not reported in a Typhon.Client project"
    FAIL=1
    cat "$COUT" | tail -20
  fi
fi

if [ $FAIL -ne 0 ]; then
  echo "--- server build output ---"
  grep -E "error|warning" "$OUT" | sed -E 's#^.*/server/##' | sort -u | head -60
  echo "analyzers: FAIL"
  exit 1
fi
echo "analyzers: PASS"
