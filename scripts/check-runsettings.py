#!/usr/bin/env python3
"""Every `.runsettings` in the repo must be well-formed XML (#705).

WHY this is worth a check of its own. `bench/aws/nightly.runsettings` carried an XML comment containing
`--blame-hang` — and an XML comment may not contain a double hyphen. `dotnet test` rejected the settings file
outright, ran ZERO tests, and both the seeded and the suppressed nightly tiers reported nothing while looking
fully configured. The prose explaining the hang guard is what disabled the tier.

That is exactly the false green #703 exists to remove, one layer below where #703 was looking: the suppression
lints check which tests are SELECTED, and a settings file that fails to parse means no test is selected at all,
for a reason no test-level check can see. #703's "the tier must select tests" assertion caught the symptom on the
runner; this catches the cause in under a second, on any machine, before the push.

Runs over the whole repo rather than a hard-coded list, because the failure mode is a file nobody remembered to
check — enumerating the ones we remembered would reproduce the original mistake.
"""

from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET

SKIP_DIRS = {"bin", "obj", "node_modules", ".git", "TestResults"}


def double_hyphens_in_comments(text: str):
    """Line numbers (1-based) where a `--` sits inside an XML comment — illegal, and the reason this check exists.

    Diagnosed here rather than inferred from the parser message: expat reports a bare "not well-formed (invalid
    token)" with a column, which says nothing about the cause. Scanning for the real condition turns an unhelpful
    error into an actionable one, and a hint that is merely guessed at is worse than none.
    """
    hits = []
    pos, line = 0, 1
    while True:
        start = text.find("<!--", pos)
        if start < 0:
            return hits
        line += text.count("\n", pos, start)
        end = text.find("-->", start + 4)
        body_end = end if end >= 0 else len(text)
        body = text[start + 4:body_end]
        # Walk the body so the reported line is the offending one, not the comment's opening line.
        off, body_line = 0, line
        while True:
            i = body.find("--", off)
            if i < 0:
                break
            hits.append(body_line + body.count("\n", 0, i))
            off = i + 2
        line += text.count("\n", start, body_end)
        pos = body_end + 3 if end >= 0 else len(text)


def find_settings(root: str):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if fn.endswith(".runsettings"):
                yield os.path.join(dirpath, fn)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
                    help="repo root to scan (default: the repo this script lives in)")
    args = ap.parse_args(argv)

    found, bad = 0, []
    for path in sorted(find_settings(args.root)):
        found += 1
        rel = os.path.relpath(path, args.root)
        try:
            ET.parse(path)
        except ET.ParseError as e:
            with open(path, encoding="utf-8", errors="replace") as fh:
                hyphen_lines = double_hyphens_in_comments(fh.read())
            bad.append((rel, str(e), hyphen_lines))

    if found == 0:
        # Not a pass: the repo has runsettings, so finding none means the scan is looking in the wrong place and
        # a green result here would mean nothing at all.
        print("::error::no .runsettings files found — the scan is misconfigured, not the repo clean")
        return 1

    for rel, err, hyphen_lines in bad:
        print(f"::error file={rel}::{rel} is not well-formed XML: {err}")
        print(f"  {rel}: {err}")
        if hyphen_lines:
            where = ", ".join(str(n) for n in hyphen_lines)
            print(f"  HINT: line(s) {where} put a DOUBLE HYPHEN inside an XML comment, which is illegal and is "
                  "almost certainly the cause — the parser only says 'invalid token'. Spell a CLI flag without "
                  "its leading dashes inside comments (`blame-hang`, not the two-dash form).")

    if bad:
        print(f"\nrunsettings check: {len(bad)} of {found} file(s) malformed. dotnet test REJECTS a bad settings "
              "file and then runs zero tests, so this disables a whole tier while it still looks configured.")
        return 1

    print(f"runsettings check: {found} file(s), all well-formed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
