#!/usr/bin/env python3
"""Rewrite repo-source relative links in published doc/ markdown to GitHub URLs.

The published trees (doc/in-depth-overview, doc/guide, doc/feature-set) link to
implementation and test sources with relative paths like `../../src/Foo.cs`.
Those escape DocFX's content root, so they warn at build time and 404 on the
site. On GitHub they resolve as repo-relative links. This rewrites them to
absolute github.com blob/tree URLs so they resolve *everywhere* (site + GitHub).

Only links whose resolved path is under one of ROOTS are rewritten; intra-doc
links, anchors, and existing URLs are left untouched. `claude/` refs are left
alone (private KB — handled when the Internals tab lands).

Usage:
    python scripts/normalize-doc-links.py            # dry-run: report counts
    python scripts/normalize-doc-links.py --apply     # rewrite in place
"""
import os
import re
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
BLOB = "https://github.com/Log2n-io/Typhon/blob/main/"
TREE = "https://github.com/Log2n-io/Typhon/tree/main/"
ROOTS = ("src/", "test/", "tools/", "benchmark/", "doc/guide/example/")
TREES = ("doc/in-depth-overview", "doc/guide", "doc/feature-set")
LINK = re.compile(r'\]\((?P<t>[^)\s]+)(?P<title>\s+"[^"]*")?\)')


def resolve(md_path, target):
    path, sep, frag = target.partition("#")
    if not path:
        return None, None, None
    absol = os.path.normpath(os.path.join(os.path.dirname(md_path), path))
    rel = os.path.relpath(absol, REPO).replace("\\", "/")
    return rel, (sep + frag if sep else ""), absol


def rewrite_target(md_path, target):
    if re.match(r"^[a-z]+:", target) or target.startswith("#"):
        return None
    rel, frag, absol = resolve(md_path, target)
    if not rel or rel.startswith(".."):
        return None
    if not any(rel == r.rstrip("/") or rel.startswith(r) for r in ROOTS):
        return None
    base = TREE if os.path.isdir(absol) else BLOB
    return base + rel + frag


def process(md_path, apply):
    with open(md_path, encoding="utf-8") as f:
        text = f.read()
    n = [0]

    def _sub(m):
        new = rewrite_target(md_path, m.group("t"))
        if new is None:
            return m.group(0)
        n[0] += 1
        return "](" + new + (m.group("title") or "") + ")"

    out = LINK.sub(_sub, text)
    if apply and n[0]:
        with open(md_path, "w", encoding="utf-8") as f:
            f.write(out)
    return n[0]


def main():
    apply = "--apply" in sys.argv[1:]
    total, files = 0, 0
    for tree in TREES:
        for root, _, names in os.walk(os.path.join(REPO, tree)):
            for name in names:
                if name.endswith(".md"):
                    c = process(os.path.join(root, name), apply)
                    if c:
                        total += c
                        files += 1
    verb = "rewrote" if apply else "would rewrite"
    print(f"{verb} {total} link(s) across {files} file(s)"
          + ("" if apply else "  (dry-run — pass --apply to write)"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
