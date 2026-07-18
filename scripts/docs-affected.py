#!/usr/bin/env python3
"""docs-affected — Resolve a set of changed files to the doc pages that might now be inaccurate.

USAGE
    python3 scripts/docs-affected.py <file1> [<file2> ...]      # explicit files
    git diff --name-only origin/main...HEAD | python3 scripts/docs-affected.py -   # from a diff

WHAT IT DOES  (issue #490 — Layer 2 of claude/design/doc-accuracy-and-generated-artifacts.md)
    The twin of scripts/test-affected.py, for docs instead of tests. For each changed file:
      - A changed DOC page (doc/**/*.md, excluding generated _site/ and api/) is reviewed directly
        — the PR edited prose; is it still accurate?
      - A changed SOURCE file is prefix-matched against the coarse source-area keys in
        coverage/docs-affected-map.json; the mapped doc pages join the review set.
      - A changed source file under src/ that matches NO area key is reported as UNMAPPED — a
        map-maintenance nudge (the map self-heals instead of silently missing).
    Directory values (trailing '/') expand to their *.md. Mapped pages that no longer exist on disk
    are reported as STALE map entries. The map is HAND-authored — there is no generator for it.

DESIGN
    Scoped, never whole-corpus: the review set is only the pages the changed areas touch. Recall for
    the cross-cutting / unmapped residue is delegated to Layer 3 (the weekly full-corpus audit), not
    to this map. Values in the map are intentionally generous (over-include) to keep recall high here.

OUTPUT
    --format text (default): human-readable sections.
    --format json:  {"pages": [...], "changed_docs": [...], "unmapped": [...], "stale": [...]}
    Exit code is always 0 (advisory tool); an empty "pages" set means nothing to review.
"""
from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MAP_PATH = REPO_ROOT / "coverage/docs-affected-map.json"

# Doc subtrees that are hand-written SOURCE prose (reviewable). Everything else under doc/ — the
# docfx build output and the generated API reference — is excluded: it is generated, not authored.
DOC_SOURCE_PREFIXES = ("doc/guide/", "doc/key-concepts/", "doc/in-depth-overview/", "doc/feature-set/")
DOC_EXCLUDE_PREFIXES = ("doc/_site/", "doc/api/", "doc/obj/", "doc/bin/")


def load_map() -> dict:
    if not MAP_PATH.exists():
        return {}
    with MAP_PATH.open(encoding="utf-8") as fh:
        raw = json.load(fh)
    # Keys beginning with '_' are metadata (README/convention), not map entries.
    return {k: v for k, v in raw.items() if not k.startswith("_")}


def normalize_path(p: str) -> str:
    """Repo-relative POSIX path."""
    try:
        rel = Path(p).resolve().relative_to(REPO_ROOT)
    except ValueError:
        rel = Path(p)
    return str(rel).replace("\\", "/")


def is_doc_source(rel: str) -> bool:
    return (
        rel.endswith(".md")
        and rel.startswith(DOC_SOURCE_PREFIXES)
        and not rel.startswith(DOC_EXCLUDE_PREFIXES)
    )


def expand_value(v: str) -> list[str]:
    """A trailing-'/' value is a directory: expand to its *.md (recursive). Otherwise it is a page."""
    if v.endswith("/"):
        base = REPO_ROOT / v
        if not base.is_dir():
            return []
        return sorted(str(p.relative_to(REPO_ROOT)).replace("\\", "/") for p in base.rglob("*.md"))
    return [v]


def resolve(files: list[str], amap: dict) -> dict:
    pages: set[str] = set()
    changed_docs: set[str] = set()
    unmapped: list[str] = []
    stale: set[str] = set()

    for f in files:
        rel = normalize_path(f)
        if is_doc_source(rel):
            changed_docs.add(rel)
            pages.add(rel)
            continue
        if rel.startswith("src/"):
            matched = False
            for key, values in amap.items():
                if rel.startswith(key):
                    matched = True
                    for v in values:
                        for page in expand_value(v):
                            if (REPO_ROOT / page).exists():
                                pages.add(page)
                            else:
                                stale.add(f"{page}  (mapped by {key})")
            if not matched:
                unmapped.append(rel)
        # Non-src, non-doc files (.github/, scripts/, test/, *.csproj) never map to doc pages.

    return {
        "pages": sorted(pages),
        "changed_docs": sorted(changed_docs),
        "unmapped": sorted(set(unmapped)),
        "stale": sorted(stale),
    }


def validate_map(amap: dict) -> tuple[list[str], list[str]]:
    """Whole-map integrity check. Returns (stale, unmapped_subsystems).

    stale    = a mapped page/dir that no longer exists (or a mapped dir with no *.md) — map rot.
    unmapped = top-level src/Typhon.Engine/<X>/ subsystems with no map key — a coverage gap.
    """
    stale: set[str] = set()
    for key, values in amap.items():
        for v in values:
            if v.endswith("/"):
                base = REPO_ROOT / v
                if not base.is_dir():
                    stale.add(f"{v}  (dir missing; mapped by {key})")
                elif not any(base.rglob("*.md")):
                    stale.add(f"{v}  (dir has no *.md; mapped by {key})")
            elif not (REPO_ROOT / v).exists():
                stale.add(f"{v}  (mapped by {key})")

    unmapped: list[str] = []
    src_root = REPO_ROOT / "src/Typhon.Engine"
    if src_root.is_dir():
        for d in sorted(src_root.iterdir()):
            if d.is_dir() and d.name not in ("bin", "obj"):
                prefix = f"src/Typhon.Engine/{d.name}/"
                if prefix not in amap:
                    unmapped.append(prefix)
    return sorted(stale), unmapped


def run_validate(amap: dict) -> int:
    """--validate: BLOCKING on stale (deterministic map rot), ADVISORY on unmapped (Layer 3 is the floor)."""
    stale, unmapped = validate_map(amap)
    print(f"# docs-affected map validation — {len(amap)} mapped area(s)")
    if unmapped:
        print(f"# WARNING: {len(unmapped)} source subsystem(s) with no map entry "
              f"(Layer 3's weekly audit still covers their doc drift; add a key for fast Layer-2 coverage):")
        for u in unmapped:
            print(f"  {u}")
    if stale:
        print(f"# FAIL: {len(stale)} stale map entr(y/ies) — a mapped page/dir no longer exists:")
        for s in stale:
            print(f"  {s}")
        print("# Fix coverage/docs-affected-map.json (a doc page was moved, renamed, or deleted).")
        return 1
    print("# OK: every mapped page/dir exists (0 stale).")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=__doc__,
        epilog=(
            "EXAMPLES\n"
            "    python3 scripts/docs-affected.py src/Typhon.Engine/Ecs/EcsQuery.cs\n"
            "    git diff --name-only origin/main...HEAD | python3 scripts/docs-affected.py -\n"
        ),
    )
    ap.add_argument("files", nargs="*", help="Changed files. Use - to read newline-separated paths from stdin.")
    ap.add_argument("--format", choices=["text", "json"], default="text")
    ap.add_argument("--validate", action="store_true",
                    help="Validate the whole map (ignores file args): exit non-zero on stale entries, warn on unmapped subsystems.")
    args = ap.parse_args()

    if args.validate:
        return run_validate(load_map())

    if not args.files:
        ap.error("no files given (pass file paths, or --validate to check the map)")

    if "-" in args.files:
        args.files = [f for f in args.files if f != "-"]
        args.files += [line.strip() for line in sys.stdin.read().splitlines() if line.strip()]

    result = resolve(args.files, load_map())

    if args.format == "json":
        print(json.dumps(result, indent=2))
        return 0

    pages, changed_docs, unmapped, stale = (result["pages"], result["changed_docs"], result["unmapped"], result["stale"])
    print(f"# docs-affected: {len(pages)} page(s) to review")
    for p in pages:
        marker = " (edited in this PR)" if p in changed_docs else ""
        print(f"{p}{marker}")
    if unmapped:
        print("\n# UNMAPPED source areas (no doc pages mapped — consider adding to coverage/docs-affected-map.json):")
        for u in unmapped:
            print(f"  {u}")
    if stale:
        print("\n# STALE map entries (mapped page no longer exists — fix coverage/docs-affected-map.json):")
        for s in stale:
            print(f"  {s}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
