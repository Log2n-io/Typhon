#!/usr/bin/env python3
"""Insert an 'Overview' entry (-> api/index.md) at the top of the generated api/toc.yml.

`docfx metadata` regenerates doc/api/toc.yml from the assemblies and does NOT
include the hand-authored landing (doc/api/index.md). Without a toc entry the
landing falls outside the API toc scope and renders with an empty left sidebar.
This weaves it in as the first node so the API landing shows the assembly tree.

Run AFTER `docfx metadata` and BEFORE `docfx build`. Idempotent.
"""
import os
import sys

TOC = os.path.join(os.path.dirname(os.path.dirname(__file__)), "doc", "api", "toc.yml")
ENTRY = "- name: Overview\n  href: index.md\n"


def main():
    if not os.path.exists(TOC):
        print(f"error: {TOC} not found — run `docfx metadata doc/docfx.json` first", file=sys.stderr)
        return 1
    text = open(TOC, encoding="utf-8").read()
    if "href: index.md" in text:
        print("api/toc.yml already has the Overview entry (idempotent no-op)")
        return 0
    out, done = [], False
    for ln in text.splitlines(keepends=True):
        out.append(ln)
        if not done and ln.strip() == "items:":
            out.append(ENTRY)
            done = True
    if not done:
        print("warn: no 'items:' line in api/toc.yml — nothing injected", file=sys.stderr)
        return 1
    open(TOC, "w", encoding="utf-8").write("".join(out))
    print("injected Overview -> index.md as the first api/toc.yml entry")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
