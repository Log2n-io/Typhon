#!/usr/bin/env python3
"""Generate a nested DocFX `toc.yml` for a folder tree.

Usage:
    python scripts/gen-docfx-toc.py doc/feature-set

- A folder's `README.md` becomes that node's landing (`href`); it is not a
  separate item.
- Other `*.md` files become leaf items; sub-folders recurse.
- Display names come from each file's first H1, falling back to a
  Title-Cased version of the (kebab/snake) filename.

Regenerate after adding/removing feature pages. Kept deliberately simple; the
generated file is committed and can be hand-tweaked.
"""
import os
import re
import sys


def title(name: str) -> str:
    name = re.sub(r"\.md$", "", name)
    name = name.replace("-", " ").replace("_", " ").strip()
    return name[:1].upper() + name[1:] if name else name


def clean(name: str) -> str:
    """Strip common markdown escapes so H1s render as plain nav labels."""
    for a, b in (("\\<", "<"), ("\\>", ">"), ("\\[", "["), ("\\]", "]"),
                 ("\\|", "|"), ("\\`", "`"), ("\\_", "_"), ("\\*", "*")):
        name = name.replace(a, b)
    return name.strip()


def h1(path: str):
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                s = line.strip()
                if s.startswith("# "):
                    return s[2:].strip()
    except OSError:
        pass
    return None


def build(folder: str, rel: str = ""):
    entries = sorted(os.listdir(folder))
    files = [e for e in entries
             if e.endswith(".md") and e.lower() != "readme.md" and e != "toc.yml"]
    dirs = [e for e in entries if os.path.isdir(os.path.join(folder, e))]
    items = []
    for f in files:
        items.append({"name": clean(h1(os.path.join(folder, f)) or title(f)), "href": rel + f})
    for d in dirs:
        sub = os.path.join(folder, d)
        readme = os.path.join(sub, "README.md")
        node = {"name": clean((h1(readme) if os.path.exists(readme) else None) or title(d))}
        if os.path.exists(readme):
            node["href"] = rel + d + "/README.md"
        subitems = build(sub, rel + d + "/")
        if subitems:
            node["items"] = subitems
        elif "href" not in node:
            continue
        items.append(node)
    return items


_SPECIAL = set(":#[]{}&*!|>'\"%@`,")


def yamlstr(s: str) -> str:
    if s == "" or s[0] in "-?# " or any(c in _SPECIAL for c in s):
        return "'" + s.replace("'", "''") + "'"
    return s


def emit(items, indent=0):
    pad = "  " * indent
    out = []
    for it in items:
        out.append(f"{pad}- name: {yamlstr(it['name'])}")
        if "href" in it:
            out.append(f"{pad}  href: {it['href']}")
        if "items" in it:
            out.append(f"{pad}  items:")
            out.extend(emit(it["items"], indent + 2))
    return out


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    root = sys.argv[1].rstrip("/\\")
    if not os.path.isdir(root):
        print(f"not a folder: {root}")
        return 1
    items = build(root)
    # A root README becomes the section's landing ("Overview") so the section is
    # navigable — otherwise the folder has no index.html and the nav node 404s.
    if os.path.exists(os.path.join(root, "README.md")):
        items.insert(0, {"name": "Overview", "href": "README.md"})
    lines = emit(items)
    out = os.path.join(root, "toc.yml")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
