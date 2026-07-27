#!/usr/bin/env bash
#
# Onboarding-loop smoke test — the anti-"the scaffold pins the wrong engine" gate (Feature #532).
#
# This closes the gap the other two packaging gates miss:
#   * cli-tool-smoke-test.sh installs the tool and checks --version/--help, but never scaffolds.
#   * consumer-smoke-test.sh compiles the sample against the engine, but HAND-ADDS the package at the feed's
#     version — it bypasses the scaffold's pinned version entirely.
#
# Here we run the exact loop a first-time user runs, from the CO-PUBLISHED artifacts:
#   1. install the PACKED `Typhon.Cli` tool from the local feed,
#   2. `typhon new` — the real scaffold, pinning whatever engine version the CLI decided,
#   3. assert that pin IS the engine version being released (the precise defect this gate exists to catch:
#      an alpha.N CLI that still pins alpha.N-1 fails HERE, before publish, instead of in a user's face),
#   4. `dotnet run` the scaffolded project against the PACKED engine (restored from the local feed) and assert
#      it compiles, runs, and writes a `.typhon-trace` — the headline "zero-edit profiling" promise.
#
# `Typhon`'s transitive deps (MemoryPack, K4os.LZ4, Microsoft.Extensions.*, …) restore from nuget.org; only the
# `Typhon` engine itself comes from the local feed (the released version isn't on nuget.org yet at gate time).
#
# Usage:  scripts/onboarding-smoke-test.sh <feed-dir>
#   <feed-dir>   directory containing Typhon.<version>.nupkg AND Typhon.Cli.<version>.nupkg (the `dotnet pack -o` output)
#
# Exit 0 = PASS. Any non-zero = FAIL.
set -euo pipefail

FEED="${1:?usage: onboarding-smoke-test.sh <feed-dir>}"
# Windows-form absolute path on Git Bash/MSYS (`pwd -W`), native path on Linux/CI (`pwd`). The .NET CLI is a
# native process and cannot resolve an MSYS `/c/...` path in nuget.config.
FEED="$(cd "$FEED" && { pwd -W 2>/dev/null || pwd; })"

# Discover both packed versions from the .nupkg filenames (ignore the .snupkg symbol packages).
CLI_NUPKG="$(ls "$FEED"/Typhon.Cli.*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)"
ENGINE_NUPKG="$(ls "$FEED"/Typhon.*.nupkg 2>/dev/null | grep -v '\.snupkg$' | grep -v '/Typhon\.Cli\.' | head -1 || true)"
[ -n "$CLI_NUPKG" ]    || { echo "onboarding-smoke: no Typhon.Cli.*.nupkg found in $FEED"; exit 1; }
[ -n "$ENGINE_NUPKG" ] || { echo "onboarding-smoke: no Typhon.*.nupkg (engine) found in $FEED"; exit 1; }
CLI_VERSION="$(basename "$CLI_NUPKG" | sed -E 's/^Typhon\.Cli\.(.*)\.nupkg$/\1/')"
ENGINE_VERSION="$(basename "$ENGINE_NUPKG" | sed -E 's/^Typhon\.(.*)\.nupkg$/\1/')"
echo "onboarding-smoke: packed CLI $CLI_VERSION, packed engine $ENGINE_VERSION"

# Co-publish invariant: the tool and the engine ship from one tag, so their versions must be identical.
[ "$CLI_VERSION" = "$ENGINE_VERSION" ] || {
    echo "onboarding-smoke: FAIL — CLI ($CLI_VERSION) and engine ($ENGINE_VERSION) versions differ; they must co-publish from one tag."
    exit 1
}

# NuGet's global cache keys on ID+version and won't re-extract a same-version re-pack. When iterating locally the
# version is fixed (MinVer height), so evict to always test fresh content. (No-op in CI — every build is unique.)
rm -rf "${HOME}/.nuget/packages/typhon" "${HOME}/.nuget/packages/typhon.cli" 2>/dev/null || true

TOOLDIR="$(mktemp -d)"
WORK="$(mktemp -d)"
trap 'rm -rf "$TOOLDIR" "$WORK"' EXIT

echo "onboarding-smoke: installing the packed Typhon.Cli tool into an isolated tool-path..."
dotnet tool install Typhon.Cli --tool-path "$TOOLDIR" --add-source "$FEED" --version "$CLI_VERSION"
TYPHON=""
for cand in "$TOOLDIR/typhon" "$TOOLDIR/typhon.exe"; do
    [ -e "$cand" ] && { TYPHON="$cand"; break; }
done
[ -n "$TYPHON" ] || { echo "onboarding-smoke: FAIL — installed tool has no 'typhon' launcher"; ls -la "$TOOLDIR"; exit 1; }

cd "$WORK"
echo "onboarding-smoke: scaffolding with 'typhon new MyApp'..."
"$TYPHON" new MyApp
cd MyApp

# The crux: the scaffold must pin the engine version being released — not a stale one.
echo "onboarding-smoke: scaffold pinned →"; grep -F 'Include="Typhon"' MyApp.csproj || true
grep -qF "Version=\"$ENGINE_VERSION\"" MyApp.csproj || {
    echo "onboarding-smoke: FAIL — scaffold did NOT pin the released engine version '$ENGINE_VERSION'."
    echo "  (This is the release-blocking defect: the CLI would ship scaffolding a mismatched/unpublished engine.)"
    exit 1
}

# Restore the pinned engine from the local feed (it isn't on nuget.org yet); transitive deps come from nuget.org.
cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

echo "onboarding-smoke: 'dotnet run' — compile the emitted template + run against the packed engine..."
dotnet run

# The headline promise: config-driven profiling wrote a trace with zero code edits.
TRACE="$(ls captures/*.typhon-trace 2>/dev/null | head -1 || true)"
[ -n "$TRACE" ] && [ -s "$TRACE" ] || {
    echo "onboarding-smoke: FAIL — no non-empty *.typhon-trace under captures/ after 'dotnet run'"
    ls -la captures 2>/dev/null || true
    exit 1
}
echo "onboarding-smoke: trace written → $TRACE ($(wc -c < "$TRACE") bytes)"

echo "onboarding-smoke: PASS"
