#!/usr/bin/env python3
"""Mechanical link/path guard for `doc/` — the cheap half of the doc-accuracy net.

Why this exists
---------------
Doc accuracy has two failure classes and they cost wildly different amounts to detect:

  * **Mechanical** — a cited `src/...` path was moved, a `foo.md#anchor` link lands nowhere. Deciding this
    needs no judgement at all: the file either exists or it does not.
  * **Semantic** — prose that still parses and still links, but now says something the code no longer does.

Until now BOTH were the LLM reviewer's job (`.github/workflows/doc-accuracy-review.yml`), which meant a
7-minute billed turn to answer questions `os.path.exists` answers in milliseconds. This script takes the
mechanical class, so the reviewer is spent only on the class that actually needs a reader.

Modelled on the `typhon-claude` repo's `check-doc-drift.py`, which took the same split for the knowledge
base. Deliberately a **lint, not a report**: it exits non-zero, it needs no judgement, and it is cheap enough
to run on every push without anyone noticing.

What it checks
--------------
1. **Source-path resolution** — every backticked `src/`, `test/`, `tools/`, `scripts/` path mentioned in a doc
   must exist on disk. A doc citing a file that was renamed reads as authoritative and sends the reader
   nowhere. Trailing `:123` / `:12-34` line references are stripped before the existence test.
2. **Cross-document links** — a relative `[text](path.md)` must resolve, and a `#anchor` must match a heading
   in the target file. Section numbers drift whenever a document is reorganised, and a link that silently
   lands nowhere is invisible until a reader clicks it.

Generated trees (`doc/_site/`, `doc/api/`) are skipped — they are build output and regenerate from source.

Escape hatch
------------
A line containing `<!-- doc-links: ignore -->`, or the line immediately above it carrying that marker, is
skipped. Use it for deliberately-historical references ("removed in #280, formerly at src/Foo.cs") — the job
here is to stop silent rot, not to forbid writing about the past.

Usage
-----
    python3 scripts/check-doc-links.py            # lint doc/, exit 1 on any finding
    python3 scripts/check-doc-links.py --quiet    # only the summary
    python3 scripts/check-doc-links.py --warn     # report but always exit 0 (soak mode)
"""

import argparse
import os
import re
import sys
from pathlib import Path

# Generated or vendored trees: build output, regenerated from the sources we do check.
SKIP_DIRS = {"_site", "api", "templates"}

# Backticked repo-relative source path, optionally with :line or :line-line.
SRC_PATH = re.compile(r"`((?:src|test|tools|scripts|samples|benchmarks)/[A-Za-z0-9_.\-/]+\.[A-Za-z0-9]+)(:\d+(?:-\d+)?)?`")

# Markdown link to a .md target (relative only — http(s) and absolute site paths are out of scope).
MD_LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+\.md)(#[^)\s]*)?\)")

# ATX heading, for anchor collection.
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")

IGNORE_MARKER = "doc-links: ignore"


def heading_slug(text):
    """GitHub/DocFX-compatible anchor slug: lowercase, drop punctuation, spaces to hyphens."""
    text = re.sub(r"`([^`]*)`", r"\1", text)                 # strip inline code
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)     # link text only
    text = re.sub(r"[^\w\s\-]", "", text, flags=re.UNICODE)  # drop punctuation/emoji
    # Each whitespace char becomes its own hyphen — do NOT collapse runs. "Spatial — querying" drops the em
    # dash and leaves two spaces, which GitHub/DocFX render as `spatial--querying`. Collapsing to one hyphen
    # here reports every em-dash heading in the corpus as a missing anchor (it did, on the first run).
    return re.sub(r"\s", "-", text.strip().lower())


def collect_anchors(md_path):
    """Every heading slug in a file, plus explicit <a name="..."> / id="..." anchors."""
    anchors = set()
    try:
        lines = md_path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return anchors

    fenced = False
    for line in lines:
        if line.lstrip().startswith("```"):
            fenced = not fenced
            continue
        if fenced:
            continue
        m = HEADING.match(line)
        if m:
            anchors.add(heading_slug(m.group(2)))
        for explicit in re.findall(r'(?:name|id)="([^"]+)"', line):
            anchors.add(explicit.lower())
    return anchors


def doc_files(doc_root):
    for path in sorted(doc_root.rglob("*.md")):
        if any(part in SKIP_DIRS for part in path.relative_to(doc_root).parts):
            continue
        yield path


def ignored(lines, i):
    """True when this line, or the one above it, opts out."""
    if IGNORE_MARKER in lines[i]:
        return True
    return i > 0 and IGNORE_MARKER in lines[i - 1]


def main():
    ap = argparse.ArgumentParser(description="Mechanical link/path lint for doc/.")
    ap.add_argument("--repo-root", default=None, help="Repository root (default: parent of this script's dir).")
    ap.add_argument("--quiet", action="store_true", help="Summary only.")
    ap.add_argument("--warn", action="store_true", help="Report findings but always exit 0.")
    args = ap.parse_args()

    root = Path(args.repo_root).resolve() if args.repo_root else Path(__file__).resolve().parent.parent
    doc_root = root / "doc"

    # A missing src/ or doc/ means a broken checkout — fail loudly rather than scanning nothing and
    # reporting a cheerful green (the same trap check-doc-drift.py guards against).
    if not doc_root.is_dir():
        print(f"check-doc-links: FATAL — no doc/ under {root}", file=sys.stderr)
        return 2
    if not (root / "src").is_dir():
        print(f"check-doc-links: FATAL — no src/ under {root}; wrong --repo-root?", file=sys.stderr)
        return 2

    anchor_cache = {}
    bad_paths, bad_links, bad_anchors = [], [], []
    files_scanned = paths_checked = links_checked = 0

    for md in doc_files(doc_root):
        files_scanned += 1
        rel = md.relative_to(root).as_posix()
        lines = md.read_text(encoding="utf-8", errors="replace").splitlines()

        for i, line in enumerate(lines):
            if ignored(lines, i):
                continue

            # ── 1. source paths ──────────────────────────────────────────────────────────────────────
            for m in SRC_PATH.finditer(line):
                paths_checked += 1
                target = root / m.group(1)
                if not target.exists():
                    bad_paths.append((rel, i + 1, m.group(1)))

            # ── 2. cross-document links ──────────────────────────────────────────────────────────────
            for m in MD_LINK.finditer(line):
                href, frag = m.group(1), (m.group(2) or "")
                if href.startswith(("http://", "https://", "/")):
                    continue
                links_checked += 1
                target = (md.parent / href).resolve()
                if not target.is_file():
                    bad_links.append((rel, i + 1, href))
                    continue
                if frag and len(frag) > 1:
                    if target not in anchor_cache:
                        anchor_cache[target] = collect_anchors(target)
                    if frag[1:].lower() not in anchor_cache[target]:
                        bad_anchors.append((rel, i + 1, f"{href}{frag}"))

    findings = len(bad_paths) + len(bad_links) + len(bad_anchors)

    if not args.quiet:
        for label, rows in (("unresolved source path", bad_paths),
                            ("broken doc link", bad_links),
                            ("missing anchor", bad_anchors)):
            for rel, ln, what in rows:
                print(f"{rel}:{ln}: {label} -> {what}")

    print(f"\ncheck-doc-links: {files_scanned} files, {paths_checked} source paths, {links_checked} links "
          f"-> {len(bad_paths)} unresolved paths, {len(bad_links)} broken links, {len(bad_anchors)} missing anchors")

    if findings and not args.warn:
        print("check-doc-links: FAIL (add '<!-- doc-links: ignore -->' on or above a line that is deliberately historical)")
        return 1

    print("check-doc-links: PASS" if not findings else "check-doc-links: findings reported (--warn: not failing)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
