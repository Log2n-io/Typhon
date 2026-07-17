#!/usr/bin/env sh
# Canonical docs build (local & CI): API from built assemblies, then the full site.
#
# API metadata is read from BUILT assemblies (staged by stage-api-bin.sh), not
# recompiled from source — Typhon's public attributes (TraceEvent, BeginParam) are
# source-generated and invisible to DocFX's Roslyn (CS0246). `docfx metadata` writes
# the generated API tree into doc/api/ref/; the hand-authored doc/api/toc.yml (Overview
# + a node that includes api/ref/toc.yml) and doc/api/index.md are STABLE and never
# regenerated — so the API landing's left TOC survives ANY build, including the
# all-in-one `docfx doc/docfx.json --serve`. (No post-processing / inject step.)
#
# Usage: scripts/build-docs.sh [Debug|Release]   (default: Debug)
# Prereq: `dotnet build src/Typhon.Shell -c <config>` has run (for API assemblies).
set -eu
CONFIG="${1:-Debug}"

# Clean generated output first — DocFX's incremental build leaves orphaned HTML
# (and stale search-index / manifest entries) when a source page is deleted.
# Try a full remove (best for purging orphans); if _site is busy — a static server
# holding the docroot open — fall back to emptying its CONTENTS so `set -e` can't
# abort the build with "Device or resource busy". Never fatal.
rm -rf doc/obj
rm -rf doc/_site 2>/dev/null || { rm -rf doc/_site/* doc/_site/.[!.]* 2>/dev/null || true; }
mkdir -p doc/_site
rm -rf doc/api/ref                            # generated API metadata (subfolder); hand-authored api/toc.yml + index.md kept
# Purge any stray flat api/*.yml + .manifest left by a pre-refactor `dest: api` build —
# the yml collide with api/ref/*.yml (DuplicateUids). Never touch the hand-authored toc.yml.
find doc/api -maxdepth 1 -name '*.yml' ! -name 'toc.yml' -delete 2>/dev/null || true
rm -f doc/api/.manifest 2>/dev/null || true

sh scripts/stage-api-bin.sh "$CONFIG"        # public DLLs + XML docs -> doc/.api-bin
dotnet docfx doc/docfx.json                   # metadata (-> doc/api/ref) + build (-> doc/_site)

# LLM-facing artifacts: llms.txt (host-root map), llms-full.txt (conceptual corpus),
# and per-page <page>.html.md — a source-transform over docfx's manifest.json +
# xrefmap.yml. Validation is baked in: an unresolved xref / .md link or residual
# DocFX token is a hard error (set -e aborts the build). Never committed — pure
# build output. Design: claude/design/Misc/llms-txt-generation.md
python3 scripts/gen-llms-txt.py               # -> doc/_site/{llms.txt,llms-full.txt,**/*.html.md}
echo "built doc/_site"
