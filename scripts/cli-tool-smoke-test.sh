#!/usr/bin/env bash
#
# CLI tool smoke test for the `Typhon.Cli` dotnet tool (Feature #435, #428).
#
# This is the anti-"the tool package silently broke" gate. From a local feed it:
#   1. installs the `Typhon.Cli` package into an ISOLATED tool-path (no global-state pollution,
#      and no collision with a `typhon` that may already be installed globally),
#   2. asserts the installed command is `typhon` — and that NO `tsh` command was produced
#      (the alias was dropped, D-5: the SDK is one-command-per-tool),
#   3. runs `typhon --version` and asserts it reports the packed version (proves the tool launches
#      and that --version is wired to the MinVer-derived assembly version, not a hardcoded literal),
#   4. runs `typhon --help` and asserts the app identifies itself as `typhon`.
#
# A dotnet tool package is self-contained (its whole dependency closure is packed under tools/<tfm>/any/),
# so only the local feed is needed — no nuget.org restore for the tool itself.
#
# Usage:  scripts/cli-tool-smoke-test.sh <feed-dir>
#   <feed-dir>   directory containing Typhon.Cli.<version>.nupkg  (e.g. the `dotnet pack -o` output)
#
# Exit 0 = PASS. Any non-zero = FAIL.
set -euo pipefail

FEED="${1:?usage: cli-tool-smoke-test.sh <feed-dir>}"
# Windows-form absolute path on Git Bash/MSYS (`pwd -W`), native path on Linux/CI (`pwd`).
# The .NET CLI is a native Windows process and cannot resolve an MSYS `/c/...` path.
FEED="$(cd "$FEED" && { pwd -W 2>/dev/null || pwd; })"

# Discover the packed version from the .nupkg filename (ignore the .snupkg symbol package).
NUPKG="$(ls "$FEED"/Typhon.Cli.*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)"
[ -n "$NUPKG" ] || { echo "cli-smoke: no Typhon.Cli.*.nupkg found in $FEED"; exit 1; }
VERSION="$(basename "$NUPKG" | sed -E 's/^Typhon\.Cli\.(.*)\.nupkg$/\1/')"
echo "cli-smoke: testing tool 'Typhon.Cli' $VERSION from $FEED"

TOOLDIR="$(mktemp -d)"
trap 'rm -rf "$TOOLDIR"' EXIT

# NuGet's global cache keys on ID+version and does NOT re-extract a same-version re-pack. When iterating
# locally the version is fixed (MinVer height), so evict the tool package to always test fresh content.
# (In CI every build has a unique version, so this is a no-op there.)
rm -rf "${HOME}/.nuget/packages/typhon.cli" 2>/dev/null || true

echo "cli-smoke: installing tool into isolated tool-path..."
dotnet tool install Typhon.Cli \
    --tool-path "$TOOLDIR" \
    --add-source "$FEED" \
    --version "$VERSION"

# The launcher is `typhon` (Linux) / `typhon.exe` (Windows). Resolve whichever exists.
TYPHON=""
for cand in "$TOOLDIR/typhon" "$TOOLDIR/typhon.exe"; do
    [ -e "$cand" ] && { TYPHON="$cand"; break; }
done
[ -n "$TYPHON" ] || { echo "cli-smoke: FAIL — installed tool has no 'typhon' launcher"; ls -la "$TOOLDIR"; exit 1; }
echo "cli-smoke: launcher present: $TYPHON"

# The dropped alias must NOT exist.
if [ -e "$TOOLDIR/tsh" ] || [ -e "$TOOLDIR/tsh.exe" ]; then
    echo "cli-smoke: FAIL — a 'tsh' command was produced; the alias must be dropped (D-5)"
    exit 1
fi
echo "cli-smoke: no 'tsh' command (alias correctly dropped)"

echo "cli-smoke: 'typhon --version' ..."
VER_OUT="$("$TYPHON" --version)"
echo "  > $VER_OUT"
echo "$VER_OUT" | grep -qF "$VERSION" || {
    echo "cli-smoke: FAIL — --version '$VER_OUT' does not report packed version '$VERSION'"; exit 1; }

echo "cli-smoke: 'typhon --help' ..."
HELP_OUT="$("$TYPHON" --help)"
echo "$HELP_OUT" | grep -qi "typhon" || {
    echo "cli-smoke: FAIL — --help does not identify the app as 'typhon'"; exit 1; }

echo "cli-smoke: PASS"
