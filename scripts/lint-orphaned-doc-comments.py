#!/usr/bin/env python3
"""
lint-orphaned-doc-comments.py — a doc comment must document the member below it.

WHY: inserting a member immediately ABOVE an existing member's doc comment leaves that comment attached to the new
member, and the member it was written for silently loses its documentation. Nothing catches it — the C# compiler does
not validate one `<summary>` per member, `dotnet build` is clean, every test passes, and the diff reads as a pure
addition because the orphaned lines are untouched context. The reader is the only detector, and the reader is looking
at the new member.

Measured on this repo when the check landed: 39 across src/, tools/, test/ and demo/, including three introduced in a
single session by one author who had already been told twice that session to stop doing it. One of them
(`DatabaseEngine.TickFence.cs`) had stacked THREE summaries, so two members were undocumented and the third carried a
description of neither. That is the whole argument for a mechanical check: it is a mistake that survives review
because the evidence of it is what the reviewer is scrolling past.

The shape is unambiguous. A member takes exactly one `<summary>`, so a `///` line opening a `<summary>` immediately
after a `///` line that CLOSED a doc-comment element means the first comment has no member:

    /// <summary>Drain the pending deltas.</summary>       <- documents nothing any more
    /// <summary>Drain them before a new subscriber.</summary>
    internal void FlushBeforeSubscribe() => ...

Anything between them (an attribute, a blank line, code) means they belong to different members, so adjacency is the
whole test — it is precisely the insertion mistake and nothing else.

Checks:

    ORPHANED_DOC_COMMENT  a second (or later) `<summary>` inside one contiguous `///` block
    BASELINE_STALE        a file with FEWER than its baseline records (advisory: run --update-baseline)

Ratchet: per-file counts are compared against `coverage/orphaned-doc-comments-baseline.json` and may not INCREASE.
Per file rather than one total so that fixing one file cannot pay for breaking another, and so a failure names where
to look. The baseline is data, not a target; it exists so that the 39 already here do not have to be fixed in the same
change as the check, while the 40th cannot land.

Usage:
    python3 scripts/lint-orphaned-doc-comments.py                    # lint the repo against the baseline
    python3 scripts/lint-orphaned-doc-comments.py --quiet            # findings only, no per-file summary
    python3 scripts/lint-orphaned-doc-comments.py --update-baseline  # accept the current counts
    python3 scripts/lint-orphaned-doc-comments.py --root DIR         # point at a tree (self-tests)

Exit code: 0 when no file exceeds its baseline, 1 when one does, 2 on a usage error.
"""
import argparse
import json
import os
import re
import sys

DEFAULT_ROOTS = ("src", "tools", "test", "demo")
SKIP_DIRS = {"obj", "bin", "node_modules", ".git", "TestResults"}

# A `///` line, and a `<summary>` opening on one. Everything is decided per contiguous BLOCK of `///` lines: a member
# takes exactly one `<summary>`, so a block holding two is two comments that a later insertion pushed together, and
# every `<summary>` after the first documents nothing.
#
# Counting summaries per block rather than looking for "a `<summary>` right after a closing tag" is what keeps the
# check honest. XML doc elements have no required order, so a single legal comment may read `<inheritdoc/>`,
# `<remarks>…</remarks>`, `<summary>…</summary>` — one member, three elements, and the adjacency form flagged it.
# `InProcessLink.RequestKick` is exactly that and is not a defect.
DOC_LINE = re.compile(r"^\s*///")
OPEN_SUMMARY = re.compile(r"^\s*///\s*<summary>")


class Finding:
    def __init__(self, check, where, message):
        self.check = check
        self.where = where
        self.message = message


def scan_file(path):
    """Line numbers (1-based) of every orphaned `<summary>` in one file — the second and later one in any `///` block."""
    hits = []
    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
            summaries_in_block = 0
            for number, line in enumerate(fh, start=1):
                if not DOC_LINE.match(line):
                    summaries_in_block = 0
                    continue
                if OPEN_SUMMARY.match(line):
                    summaries_in_block += 1
                    if summaries_in_block > 1:
                        hits.append(number)
    except OSError as exc:
        raise SystemExit(f"could not read {path}: {exc}")
    return hits


def scan(root, roots):
    """{relative path: [line numbers]} for every .cs file under `roots` inside `root`."""
    found = {}
    for top in roots:
        base = os.path.join(root, top)
        if not os.path.isdir(base):
            continue
        for directory, subdirectories, files in os.walk(base):
            subdirectories[:] = [d for d in subdirectories if d not in SKIP_DIRS]
            for name in files:
                if not name.endswith(".cs"):
                    continue
                full = os.path.join(directory, name)
                hits = scan_file(full)
                if hits:
                    found[os.path.relpath(full, root).replace(os.sep, "/")] = hits
    return found


def ratchet(found, baseline_path, update):
    if update:
        os.makedirs(os.path.dirname(baseline_path), exist_ok=True)
        with open(baseline_path, "w", encoding="utf-8") as fh:
            json.dump({p: len(h) for p, h in sorted(found.items())}, fh, indent=2, sort_keys=True)
            fh.write("\n")
        print(f"baseline written: {baseline_path} ({sum(len(h) for h in found.values())} in {len(found)} file(s))")
        return []

    if not os.path.exists(baseline_path):
        return [Finding("BASELINE_MISSING", baseline_path,
                        "no baseline; run with --update-baseline to record the current counts")]

    with open(baseline_path, "r", encoding="utf-8") as fh:
        baseline = json.load(fh)

    findings = []
    for path in sorted(set(found) | set(baseline)):
        actual = len(found.get(path, []))
        allowed = int(baseline.get(path, 0))
        if actual > allowed:
            lines = ", ".join(str(n) for n in found[path][allowed:]) if allowed else \
                    ", ".join(str(n) for n in found[path])
            findings.append(Finding(
                "ORPHANED_DOC_COMMENT", f"{path}:{lines}",
                f"{actual} orphaned doc comment(s), baseline allows {allowed} — one `///` block holds more than one "
                f"<summary>, so all but the last document nothing. Move each back onto its own member "
                f"(or delete it if that member is gone)"))
        elif actual < allowed:
            findings.append(Finding(
                "BASELINE_STALE", path,
                f"{actual} now, baseline allows {allowed} — run --update-baseline so the slack cannot be re-spent"))
    return findings


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    parser.add_argument("--roots", nargs="*", default=list(DEFAULT_ROOTS))
    parser.add_argument("--baseline", default=None)
    parser.add_argument("--update-baseline", action="store_true")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    baseline_path = args.baseline or os.path.join(args.root, "coverage", "orphaned-doc-comments-baseline.json")
    found = scan(args.root, args.roots)
    findings = ratchet(found, baseline_path, args.update_baseline)

    if args.update_baseline:
        return 0

    blocking = [f for f in findings if f.check != "BASELINE_STALE"]
    for check in sorted({f.check for f in findings}):
        group = [f for f in findings if f.check == check]
        print(f"\n=== {check} — {len(group)} finding(s) ===")
        for finding in group:
            print(f"  {finding.where}: {finding.message}")

    total = sum(len(h) for h in found.values())
    if not args.quiet:
        print(f"\norphaned doc comments: {total} across {len(found)} file(s); "
              f"{len(blocking)} over baseline")
    return 1 if blocking else 0


if __name__ == "__main__":
    sys.exit(main())
