#!/usr/bin/env python3
"""Generate LLM-facing docs artifacts from the built DocFX site.

Emits three artifacts into ``doc/_site`` (pure build output — never committed):

  1. ``llms.txt``       — a concise, llmstxt.org-compliant map of the conceptual
                          docs (H1, blockquote, ``##`` file-lists, ``## Optional``).
                          Relocated to the HOST ROOT at deploy time.
  2. ``llms-full.txt``  — Guide + Key Concepts + In-Depth Overview concatenated as
                          one normalized-markdown corpus (61 pages, ~0.65 MB).
  3. per-page ``.md``   — ``<page>.html.md`` for all conceptual pages: clean
                          markdown, no frontmatter, no DocFX link syntax.

This is a POST-DocFX source-transform, NOT an HTML scrape. It reuses DocFX's own
build tables to resolve every link deterministically:

  * ``doc/_site/manifest.json`` — source_relative_path -> output ``.html`` path.
  * ``doc/_site/xrefmap.yml``   — ``uid`` -> ``href`` (resolves ``xref:`` links).

Validation is baked in: an unresolved ``xref:`` uid, an unresolved relative
``.md`` link, or any residual DocFX token in the output is a HARD ERROR (non-zero
exit) — a clean build cannot emit a broken map. Mirrors the ``UidNotFound`` /
``InvalidFileLink`` gate already in build-docs.yml.

Design: claude/design/Misc/llms-txt-generation.md  (#492)

Usage:
    python3 scripts/gen-llms-txt.py            # generate + validate (writes doc/_site)
    python3 scripts/gen-llms-txt.py --check    # validate only, write nothing (exit 1 on any defect)
"""
import json
import os
import posixpath
import re
import sys
import time

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SITE_DIR = os.path.join(REPO, "doc", "_site")
SITE = "https://doc.typhondb.io/latest/"

# Content areas, keyed by source_relative_path prefix. `order` drives section
# order in llms.txt; `full` marks the sections concatenated into llms-full.txt.
SECTIONS = [
    ("guide/",             "Guide",             1, True),
    ("key-concepts/",      "Key Concepts",      2, True),
    ("in-depth-overview/", "In-Depth Overview", 3, True),
    ("tools/",             "Tools",             4, False),
    ("demos/",             "Demos",             5, False),
    # feature-set is handled specially (categories primary, leaves optional).
]

# Raw HTML attributes (diagram <a href>/<img src>) on non-fenced lines.
HTMLATTR = re.compile(r'\b(href|src)="([^"]+)"')
FENCE = re.compile(r'^\s*(```|~~~)')
# Inline code span (used only to strip code for the residual audit).
CODE_SPAN = re.compile(r'(`+)(?:.*?)\1')
H1 = re.compile(r'^#\s+(.+?)\s*$', re.M)
ABS_SCHEME = re.compile(r'^[a-z][a-z0-9+.\-]*:')  # http:, mailto:, data:, ...


# ---------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------

def load_manifest():
    """Return (base_path, [ {src, out} ... ]) for every Conceptual page,
    plus the full source_relative_path -> output-html map (for link resolution)."""
    with open(os.path.join(SITE_DIR, "manifest.json"), encoding="utf-8") as f:
        m = json.load(f)
    base = m["source_base_path"]
    src2out = {}
    pages = []
    for f in m["files"]:
        out = f.get("output", {}).get(".html", {}).get("relative_path")
        src = f.get("source_relative_path")
        if not out or not src:
            continue
        src = src.replace("\\", "/")
        src2out[src] = out
        if f.get("type") == "Conceptual":
            pages.append({"src": src, "out": out})
    return base, pages, src2out


def load_xrefmap():
    """Parse xrefmap.yml into {uid: href} without a YAML dependency.

    The file is a flat ``references:`` list of blocks; every block has a ``uid``
    and (for the entries we cross-reference) an ``href``. Line-regular by
    construction — DocFX emits it, we only read uid/href."""
    xref = {}
    uid = None
    with open(os.path.join(SITE_DIR, "xrefmap.yml"), encoding="utf-8") as f:
        for line in f:
            mu = re.match(r'- uid:\s+(.*)$', line)
            if mu:
                uid = _yaml_scalar(mu.group(1))
                continue
            mh = re.match(r'\s+href:\s+(.*)$', line)
            if mh and uid is not None:
                xref[uid] = _yaml_scalar(mh.group(1))
                uid = None
    return xref


def _yaml_scalar(v):
    """Unquote a single-line YAML scalar (single/double quoted or plain)."""
    v = v.strip()
    if len(v) >= 2 and v[0] == "'" and v[-1] == "'":
        return v[1:-1].replace("''", "'")
    if len(v) >= 2 and v[0] == '"' and v[-1] == '"':
        return v[1:-1].replace('\\"', '"').replace("\\\\", "\\")
    return v


def parse_frontmatter(text):
    """Split leading ``---\\n...\\n---\\n`` YAML block. Return (meta, body)."""
    if not text.startswith("---"):
        return {}, text
    end = text.find("\n---", 3)
    if end == -1:
        return {}, text
    block = text[3:end]
    body_start = text.find("\n", end + 1)
    body = text[body_start + 1:] if body_start != -1 else ""
    meta = {}
    for line in block.splitlines():
        mm = re.match(r'(\w+):\s*(.*)$', line)
        if mm:
            meta[mm.group(1)] = _yaml_scalar(mm.group(2))
    return meta, body


# ---------------------------------------------------------------------------
# Normalizer
# ---------------------------------------------------------------------------

def resolve_target(target, src, out, xref, errors):
    """Resolve one link target to an absolute /latest/ URL, or None to leave it.

    Records (kind, detail, page) into `errors` for anything that should resolve
    but doesn't (unresolved xref uid / unresolved relative .md link)."""
    if target.startswith("xref:"):
        uid = target[5:]
        href = xref.get(uid)
        if href is None:
            errors.append(("xref", uid, src))
            return None
        hpath, hsep, hfrag = href.partition("#")
        if hpath in _md_pages:                          # conceptual page -> link its clean-markdown sibling
            hpath += ".md"
        return SITE + hpath + (("#" + hfrag) if hsep else "")
    if target.startswith("#"):
        return None                                   # in-page anchor
    if ABS_SCHEME.match(target) or target.startswith("//") or target.startswith("/"):
        return None                                   # already absolute
    path, sep, frag = target.partition("#")
    frag = ("#" + frag) if sep else ""
    if path.endswith(".md"):
        key = posixpath.normpath(posixpath.join(posixpath.dirname(src), path))
        dest = _src2out.get(key)
        if dest is None:
            errors.append(("mdlink", target, src))
            return None
        if dest in _md_pages:                           # conceptual page -> link its clean-markdown sibling
            dest += ".md"
        return SITE + dest + frag
    # Relative asset (svg/png/…) — resolve against the page's OUTPUT dir.
    asset = posixpath.normpath(posixpath.join(posixpath.dirname(out), path))
    return SITE + asset + frag


def _skip_code(s, i):
    """If s[i] opens an inline code span, return the index just past its close;
    else return i. A span = run of N backticks closed by the next run of N."""
    j = i
    while j < len(s) and s[j] == "`":
        j += 1
    ticks = s[i:j]
    k = s.find(ticks, j)
    return (k + len(ticks)) if k != -1 else i


def _parse_link(s, i):
    """Parse a markdown link/image starting at s[i] (``[`` or ``![``).

    Handles balanced brackets in the text (``[, TValue]``), text spanning
    newlines, inline code inside the text, and balanced parens in the
    destination. Returns (bang, text, target, title, end) or None."""
    n = len(s)
    bang = ""
    if s[i] == "!":
        bang = "!"
        i += 1
    if i >= n or s[i] != "[":
        return None
    depth, j = 0, i
    while j < n:
        ch = s[j]
        if ch == "\\":
            j += 2
            continue
        if ch == "`":
            k = _skip_code(s, j)
            if k > j:                    # skip ] inside inline code in link text
                j = k
                continue
        if ch == "[":
            depth += 1
        elif ch == "]":
            depth -= 1
            if depth == 0:
                break
        j += 1
    if j >= n or s[j] != "]":
        return None
    text = s[i + 1:j]
    k = j + 1
    if k >= n or s[k] != "(":
        return None                      # not an inline link (ref-style/shortcut)
    depth, p = 0, k
    while p < n:
        ch = s[p]
        if ch == "\\":
            p += 2
            continue
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                break
        p += 1
    if p >= n or s[p] != ")":
        return None
    inner = s[k + 1:p]
    mt = re.match(r'^(\S+)(\s+"[^"]*")?\s*$', inner, re.S)
    if mt:
        target, title = mt.group(1), (mt.group(2) or "")
    else:
        target, title = inner.strip(), ""
    return bang, text, target, title, p + 1


def _rewrite_segment(s, src, out, xref, errors):
    """Rewrite links/assets across a non-fenced body segment (may span lines),
    scanning left to right so whichever opens first — a code span or a link —
    wins. Code spans and unresolvable links are emitted verbatim."""

    def _attr(m):
        new = resolve_target(m.group(2), src, out, xref, errors)
        return m.group(0) if new is None else '{}="{}"'.format(m.group(1), new)

    res, buf = [], []

    def flush():
        if buf:
            res.append(HTMLATTR.sub(_attr, "".join(buf)))
            buf.clear()

    i, n = 0, len(s)
    while i < n:
        c = s[i]
        if c == "`":
            k = _skip_code(s, i)
            if k > i:
                flush()
                res.append(s[i:k])                    # code span verbatim
                i = k
                continue
            buf.append(c)
            i += 1
            continue
        if c == "[" or (c == "!" and i + 1 < n and s[i + 1] == "["):
            parsed = _parse_link(s, i)
            if parsed:
                bang, text, target, title, end = parsed
                new = resolve_target(target, src, out, xref, errors)
                flush()
                res.append(s[i:end] if new is None
                           else "{}[{}]({}{})".format(bang, text, new, title))
                i = end
                continue
        buf.append(c)
        i += 1
    flush()
    return "".join(res)


def _split_fences(body):
    """Split body into (is_fence, text) segments; fenced code blocks pass through
    untouched. A fence opens on a ``` / ~~~ line and closes on the next such line."""
    lines = body.splitlines(keepends=True)
    segs, buf, i, n = [], [], 0, len(lines)
    while i < n:
        if FENCE.match(lines[i]):
            if buf:
                segs.append((False, "".join(buf)))
                buf = []
            fence = [lines[i]]
            i += 1
            while i < n:
                fence.append(lines[i])
                closed = bool(FENCE.match(lines[i]))
                i += 1
                if closed:
                    break
            segs.append((True, "".join(fence)))
        else:
            buf.append(lines[i])
            i += 1
    if buf:
        segs.append((False, "".join(buf)))
    return segs


def normalize(body, src, out, xref, errors):
    """Rewrite links/assets in a page body to absolute URLs; skip fenced code."""
    parts = []
    for is_fence, text in _split_fences(body):
        parts.append(text if is_fence
                     else _rewrite_segment(text, src, out, xref, errors))
    return "".join(parts)


def strip_code(md):
    """Remove fenced blocks and inline code spans — for residual-token auditing,
    so a link EXAMPLE inside code isn't mistaken for an unresolved real link."""
    out = []
    in_fence = False
    for line in md.splitlines():
        if FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        out.append(CODE_SPAN.sub("", line))
    return "\n".join(out)


def title_of(meta, body, src):
    if meta.get("title"):
        return meta["title"]
    m = H1.search(body)
    if m:
        return m.group(1).strip()
    return posixpath.splitext(posixpath.basename(src))[0]


def desc_of(meta, body):
    if meta.get("description"):
        return meta["description"]
    # Fallback for the ~3 frontmatter-less pages: first prose paragraph.
    for para in re.split(r'\n\s*\n', body):
        p = para.strip()
        if p and not p.startswith(("#", "<", ">", "|", "-", "*", "```")):
            p = re.sub(r'\s+', " ", re.sub(r'[`*_]', "", p))
            return (p[:157] + "…") if len(p) > 158 else p
    return ""


# ---------------------------------------------------------------------------
# Generation
# ---------------------------------------------------------------------------

def read_preamble():
    p = os.path.join(REPO, "doc", "llms-preamble.md")
    if os.path.exists(p):
        text = open(p, encoding="utf-8").read()
        text = re.sub(r'<!--.*?-->', "", text, flags=re.S)   # strip HTML comments
        return text.strip()
    return ("These docs describe the `Typhon` NuGet package (engine + tooling). "
            "Typhon is an ECS database, not SQL: model data as blittable-struct "
            "components, mutate inside a transaction, and query with the ECS view "
            "API. Read Key Concepts first for the mental model, then the Guide.")


def section_of(src):
    for prefix, name, order, full in SECTIONS:
        if src.startswith(prefix):
            return prefix, name, order, full
    return None


def sort_key(src):
    """Directory order, with README/index leading each directory."""
    d = posixpath.dirname(src)
    base = posixpath.basename(src)
    lead = 0 if base in ("README.md", "index.md") else 1
    return (d, lead, base)


def run(write):
    t0 = time.time()
    global _src2out, _md_pages
    base, pages, _src2out = load_manifest()
    _md_pages = {pg["out"] for pg in pages}   # conceptual outputs we emit a `.md` sibling for
    xref = load_xrefmap()
    errors = []           # unresolved xref / mdlink — hard failures
    residual = []         # residual DocFX tokens in emitted output — hard failures

    # Harvest + normalize every page once.
    rows = []             # {src, out, title, desc, section, order, full, md}
    for pg in pages:
        src, out = pg["src"], pg["out"]
        raw = open(os.path.join(base, src), encoding="utf-8").read()
        meta, body = parse_frontmatter(raw)
        md = normalize(body, src, out, xref, errors).strip() + "\n"
        # Residual-token audit on the emitted markdown.
        _audit_residual(md, out, residual)
        rows.append({
            "src": src, "out": out,
            "title": title_of(meta, body, src),
            "desc": desc_of(meta, body),
            "md": md,
        })

    # Per-page .md.
    written = 0
    if write:
        for r in rows:
            dst = os.path.join(SITE_DIR, r["out"] + ".md")
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, "w", encoding="utf-8", newline="\n") as f:
                f.write(r["md"])
            written += 1

    by_src = {r["src"]: r for r in rows}

    # llms-full.txt — Guide -> Key Concepts -> Overview, normalized bodies.
    full_srcs = [r["src"] for r in rows
                 if section_of(r["src"]) and section_of(r["src"])[3]]
    full_srcs.sort(key=lambda s: (section_of(s)[2], sort_key(s)))
    full_parts = [
        "# Typhon — Full Documentation Corpus\n\n"
        "> Guide + Key Concepts + In-Depth Overview, concatenated. "
        "Generated from https://doc.typhondb.io/ — do not edit.\n"
    ]
    for s in full_srcs:
        r = by_src[s]
        full_parts.append(
            "\n\n<!-- ===== {}{}.md ===== -->\n\n{}".format(SITE, r["out"], r["md"]))
    full_txt = "".join(full_parts)

    # llms.txt — sectioned map.
    llms = build_index(rows, by_src)

    # Validate BEFORE writing the map/corpus so a defect fails the build.
    _audit_residual(full_txt, "llms-full.txt", residual)
    _validate_index_links(llms, residual)
    ok = report(errors, residual)

    if write and ok:
        with open(os.path.join(SITE_DIR, "llms-full.txt"), "w",
                  encoding="utf-8", newline="\n") as f:
            f.write(full_txt)
        with open(os.path.join(SITE_DIR, "llms.txt"), "w",
                  encoding="utf-8", newline="\n") as f:
            f.write(llms)

    dt = time.time() - t0
    mode = "generated" if write else "checked"
    print("{}: {} per-page .md, llms.txt ({} B), llms-full.txt ({} B) in {:.2f}s"
          .format(mode, written if write else len(rows),
                  len(llms.encode()), len(full_txt.encode()), dt))
    return 0 if ok else 1


def build_index(rows, by_src):
    def line(r):
        d = (": " + r["desc"]) if r["desc"] else ""
        return "- [{}]({}{}.md){}".format(r["title"], SITE, r["out"], d)

    out = []
    out.append("# Typhon\n")
    out.append(
        "\n> Real-time, low-latency ACID database engine — an ECS architecture "
        "with MVCC snapshot isolation, targeting microsecond-level operations. "
        "This file maps Typhon's conceptual documentation for coding agents "
        "building on the `Typhon` NuGet package. Every link below points to the "
        "clean-markdown (`.md`) version of the page; drop the `.md` suffix for "
        "the rendered HTML page. Full corpus: "
        "https://doc.typhondb.io/latest/llms-full.txt\n")
    out.append("\n" + read_preamble() + "\n")

    # Primary sections (guide/concepts/overview/tools/demos).
    for prefix, name, order, full in sorted(SECTIONS, key=lambda s: s[2]):
        sec = [r for r in rows if r["src"].startswith(prefix)]
        if not sec:
            continue
        sec.sort(key=lambda r: sort_key(r["src"]))
        out.append("\n## {}\n".format(name))
        out.extend(line(r) for r in sec)

    # Feature Catalog — the 17 top-level category indexes only.
    cats = [r for r in rows
            if re.match(r'^feature-set/[^/]+/README\.md$', r["src"])]
    cats.sort(key=lambda r: r["title"].lower())
    if cats:
        out.append("\n## Feature Catalog\n")
        out.append("> One page per feature, grouped by subsystem. Each category "
                   "index below links every feature in that subsystem.")
        out.extend(line(r) for r in cats)

    # Optional — skippable for a smaller context: full corpus, API, benchmarks,
    # and the full feature-catalog drill-down (leaves + subcategory indexes).
    out.append("\n## Optional\n")
    out.append("- [llms-full.txt — the conceptual core as one file]({}llms-full.txt): "
               "Guide + Key Concepts + In-Depth Overview concatenated.".format(SITE))
    if "api/index.md" in by_src:
        out.append("- [API reference]({}api/index.html): full public surface "
                   "(also shipped as XML docs inside the NuGet package).".format(SITE))
    bench = [r for r in rows if r["src"].startswith("../benchmark/")]
    for r in bench:
        out.append("- [Benchmarks]({}{}.md): CI-committed reference-hardware results."
                   .format(SITE, r["out"]))
    # Feature-catalog ROOT index only. The full drill-down (subcategory indexes +
    # every leaf) is reachable from the `## Feature Catalog` category indexes above
    # — each links every feature in its subsystem — so listing it flat here would
    # only bloat the map (llmstxt.org: a curated overview, not a sitemap dump).
    root = [r for r in rows if r["src"] == "feature-set/README.md"]
    out.extend(line(r) for r in root)

    return "\n".join(out) + "\n"


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def _audit_residual(md, where, residual):
    if md.startswith("---") and re.match(r'---\s*\n', md):
        residual.append(("frontmatter", where))
    scan = strip_code(md)               # ignore link-shaped examples inside code
    for tok in ("](xref:", "](~/", '="xref:'):
        if tok in scan:
            residual.append((tok, where))
    # An internal .md link that survived normalization (should be absolute now).
    for m in re.finditer(r'\]\((?!https?://|/|#)([^)\s]+\.md)(#[^)]*)?\)', scan):
        residual.append(("unresolved-md-link:" + m.group(1), where))


# Artifacts this run generates into doc/_site — valid targets even before written.
GENERATED = {"llms.txt", "llms-full.txt"}


def _validate_index_links(llms, residual):
    """Every link in llms.txt must point at a file that exists in doc/_site."""
    for m in re.finditer(r'\]\((https://doc\.typhondb\.io/(?:latest/)?)([^)\s]+)\)', llms):
        rel = m.group(2).split("#")[0]
        if rel in GENERATED:
            continue
        # llms.txt is host-root; llms-full.txt + pages live under /latest/.
        local = os.path.join(SITE_DIR, rel.replace("/", os.sep))
        if os.path.exists(local):
            continue
        # `--check` runs without writing the `.md` siblings; accept a `.html.md`
        # target when its `.html` base exists (we emit a `.md` for every page).
        if rel.endswith(".html.md") and os.path.exists(local[:-len(".md")]):
            continue
        residual.append(("index-link-404:" + rel, "llms.txt"))


def report(errors, residual):
    if not errors and not residual:
        return True
    print("FAIL — llms.txt generation found defects:", file=sys.stderr)
    seen = set()
    for kind, detail, page in errors:
        key = (kind, detail)
        if key in seen:
            continue
        seen.add(key)
        label = "unresolved xref uid" if kind == "xref" else "unresolved .md link"
        print("  [{}] {}  (in {})".format(label, detail, page), file=sys.stderr)
    for item in residual:
        print("  [residual] {}  (in {})".format(item[0], item[1]), file=sys.stderr)
    print("Reproduce locally with: sh scripts/build-docs.sh", file=sys.stderr)
    return False


def main():
    write = "--check" not in sys.argv[1:]
    if not os.path.exists(os.path.join(SITE_DIR, "manifest.json")):
        print("error: doc/_site/manifest.json not found — run scripts/build-docs.sh first",
              file=sys.stderr)
        return 2
    return run(write)


if __name__ == "__main__":
    raise SystemExit(main())
