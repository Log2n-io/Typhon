#!/usr/bin/env python3
"""Add DocFX YAML front-matter (uid/title/description) to published doc/ markdown.

Idempotent: files that already begin with a `---` front-matter block are left
untouched (so pages authored with front-matter, or a re-run, are safe).

- uid  : derived from the path under its tree (unique), e.g.
         in-depth-overview/01-foundation.md -> overview-foundation
         feature-set/Ecs/query-system.md    -> feature-ecs-query-system
         guide/README.md                    -> guide-index
- title: the page's first H1 (markdown-stripped)
- desc : the page's summary `>` blockquote, else its first real paragraph.

Usage:
    python scripts/add-frontmatter.py            # dry-run: report counts
    python scripts/add-frontmatter.py --apply
"""
import os
import re
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TREES = {
    "doc/in-depth-overview": "overview",
    "doc/guide": "guide",
    "doc/feature-set": "feature",
}
LABEL = re.compile(r"^\*\*[A-Za-z ]+:\*\*")  # **Code:** / **Status:** lines


def strip_md(s: str) -> str:
    s = re.sub(r"`([^`]*)`", r"\1", s)
    s = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", s)     # [text](url) -> text
    s = re.sub(r"[*_]{1,3}([^*_]+)[*_]{1,3}", r"\1", s)  # emphasis
    s = re.sub(r"<[^>]+>", "", s)                        # html tags
    for a, b in (("\\<", "<"), ("\\>", ">"), ("\\[", "["), ("\\]", "]"), ("\\|", "|")):
        s = s.replace(a, b)
    return s.strip()


def uid_for(relpath: str, tag: str) -> str:
    parts = relpath[:-3].split("/")  # drop .md
    parts = [re.sub(r"^\d+[-_]?", "", p) for p in parts]  # strip NN- prefixes
    parts = [p if p.lower() != "readme" else "index" for p in parts]
    slug = "-".join(p.lower().replace("_", "-") for p in parts if p)
    return f"{tag}-{slug}"


def title_desc(text: str):
    lines = text.splitlines()
    title, i = None, 0
    for i, ln in enumerate(lines):
        m = re.match(r"^#\s+(.+)", ln)
        if m:
            title = strip_md(m.group(1))
            break
    desc = None
    for ln in lines[i + 1:]:
        s = ln.strip()
        if not s:
            continue
        if s.startswith(">"):
            desc = strip_md(s.lstrip("> ").strip())
            break
        if s.startswith(("#", "|", "```", "!", "<", "-", "*")) or LABEL.match(s):
            continue
        desc = strip_md(s)
        break
    if desc and len(desc) > 160:
        desc = desc[:157].rsplit(" ", 1)[0] + "…"
    return title, desc


def yaml_q(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def process(path, relpath, tag, apply):
    with open(path, encoding="utf-8") as f:
        text = f.read()
    if text.lstrip("﻿").startswith("---"):
        return False  # already has front-matter
    title, desc = title_desc(text)
    if not title:
        title = re.sub(r"^\d+[-_]?", "", os.path.basename(path)[:-3]).replace("-", " ").title()
    if not desc:
        desc = title
    fm = (f"---\nuid: {uid_for(relpath, tag)}\n"
          f"title: {yaml_q(title)}\ndescription: {yaml_q(desc)}\n---\n\n")
    if apply:
        with open(path, "w", encoding="utf-8") as f:
            f.write(fm + text)
    return True


def main():
    apply = "--apply" in sys.argv[1:]
    n = 0
    for tree, tag in TREES.items():
        base = os.path.join(REPO, tree)
        for root, _, names in os.walk(base):
            for name in names:
                if name.endswith(".md"):
                    p = os.path.join(root, name)
                    rel = os.path.relpath(p, base).replace("\\", "/")
                    if process(p, rel, tag, apply):
                        n += 1
    verb = "added front-matter to" if apply else "would add front-matter to"
    print(f"{verb} {n} file(s)" + ("" if apply else "  (dry-run — pass --apply)"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
