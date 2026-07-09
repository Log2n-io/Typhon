#!/usr/bin/env sh
# Stage the public assemblies + XML docs that DocFX consumes for API metadata.
#
# DocFX reads API from BUILT assemblies (not recompiled source) because Typhon's
# public attributes (TraceEvent, BeginParam, ...) are source-generated and are
# invisible to DocFX's own Roslyn compile. MSBuild already ran the generators to
# produce these DLLs, so we just hand DocFX the output.
#
# Usage: scripts/stage-api-bin.sh [Debug|Release]   (default: Debug)
# Prereq: `dotnet build src/Typhon.Shell -c <config>` has run.
set -eu
CONFIG="${1:-Debug}"
# Stage from the APP (Shell) bin, not a library bin: it carries the four public
# DLLs + their XML docs AND every transitive dependency (MemoryPack, K4os,
# diagnostics), so DocFX resolves all references. A library bin omits the NuGet
# deps and leaves those signatures unresolved (the InvalidAssemblyReference warns).
SRC="src/Typhon.Shell/bin/${CONFIG}/net10.0"
DEST="doc/.api-bin"

if [ ! -d "$SRC" ]; then
  echo "error: $SRC not found — build first: dotnet build src/Typhon.Shell -c ${CONFIG}" >&2
  exit 1
fi

mkdir -p "$DEST"
# All DLLs so DocFX can resolve transitive references; only the four public
# assemblies are actually documented (see the metadata.files list in docfx.json).
cp "$SRC"/*.dll "$DEST/"
for a in Typhon.Engine Typhon.Profiler Typhon.Protocol Typhon.Schema.Definition; do
  cp "$SRC/$a.xml" "$DEST/"
done
echo "staged $(ls "$DEST"/*.dll | wc -l) dll + 4 xml to $DEST (from $CONFIG)"
