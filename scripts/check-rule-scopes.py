#!/usr/bin/env python3
"""Verify that every symbol named in a rule's `scope:` line actually exists in the engine source.

Motivation
----------
The 2026-07-28 doc-integrity pass found nine rules scoped to symbols that do not exist anywhere in `src/`:
`WalCrc.cs`, `BulkLoadSessionImpl`, `BulkAllocationLog`, `RootFileHeader.NextFreeTSN`, `BK_CleanShutdown`,
`SnapshotStore.cs`, `ApplyCommitted`, `DurabilityGuarantee` and the v1 `WalRecordHeader`. Several had been dead for
months while the rules citing them read as current, which is worse than a missing rule — a reader trusts the name,
greps for it, finds nothing, and concludes the rule is obsolete rather than that the scope is.

None of that needed judgement to catch. This does it mechanically.

Every unresolved symbol fails
-----------------------------
This checker used to carry a `--baseline-rules` regression-only mode that demoted an unresolved symbol to a warning
when the change introduced it, on the grounds that the symbol was "arriving" with a companion PR in the other
repository. That distinction existed only because rules lived in `Typhon-Claude` while the code lived here, so a
rule and the symbol it scopes could not land in one commit (#747).

They can now. A scope naming a symbol that does not exist is a defect in the diff that introduces it, and the diff
that fixes it is the same diff. There is no "early" any more, so there is no mode.

Usage
-----
    python3 scripts/check-rule-scopes.py [--quiet]

Exit codes: 0 clean, 1 unresolved symbols, 2 could not locate the source tree.

This is intentionally a *lint*, not a proof. It resolves identifiers by presence anywhere in the C# sources, so it
cannot tell you a scope is too NARROW — only that a named symbol is gone. Scope completeness stays a human judgement.
"""

import argparse
import os
import re
import sys

# Words that appear in scope lines but are prose, not symbols.
STOPWORDS = {
    "the", "and", "or", "of", "in", "on", "at", "to", "for", "with", "via", "from", "per", "plus", "both", "all",
    "every", "any", "not", "only", "also", "see", "note", "corrected", "verified", "deliberately", "which", "that",
    "this", "its", "it", "is", "are", "was", "were", "has", "have", "had", "but", "so", "if", "when", "then", "than",
    "each", "one", "two", "three", "path", "paths", "phase", "phases", "step", "steps", "line", "lines", "side",
    "write", "writes", "read", "reads", "page", "pages", "rule", "rules", "gate", "check", "checks", "gated",
    "gating", "owner", "owners", "gone", "dead", "true", "false", "none", "gap", "old", "new", "gets", "runs",
}


def load_source_index(src_root):
    """Return (identifier blob, set of source file names).

    Two different membership tests are needed and conflating them is a bug: a scope line naming `WalWriter.cs` is
    referring to a FILE, whose name never appears inside its own contents, while `ApplyCommitted` is an IDENTIFIER
    that must appear in some file's text.
    """
    blobs, names = [], set()
    for dirpath, _dirs, files in os.walk(src_root):
        for fn in files:
            if fn.endswith((".cs", ".csproj", ".props")):
                names.add(fn)
                try:
                    with open(os.path.join(dirpath, fn), encoding="utf-8", errors="replace") as fh:
                        blobs.append(fh.read())
                except OSError:
                    pass
    return "\n".join(blobs), names


def candidate_symbols(scope_text):
    """Pull plausible identifiers out of a scope line.

    Accepts `Foo.cs`, `Foo.Bar`, `Namespace/Foo.cs` and bare PascalCase identifiers. Deliberately conservative:
    a false negative here is fine, a false positive wastes someone's afternoon.
    """
    out = set()
    # File references first, and remember their stems: `RecordFormat.cs` is a FILE, and there is no requirement that a
    # type of that name exists inside it. Emitting the stem as a separate identifier produced false positives.
    stems = set()
    for m in re.finditer(r"[A-Za-z_][\w./]*\.cs\b", scope_text):
        base = os.path.basename(m.group(0))
        out.add(base)
        stems.add(base[:-3].split(".")[0])
    # Strip directory segments so `Foundation/Hashing/internals/Foo.cs` does not yield `Foundation`.
    prose = re.sub(r"[A-Za-z_][\w./]*\.cs\b", " ", scope_text)
    for m in re.finditer(r"\b([A-Z][A-Za-z0-9]{3,})(?:\.([A-Z][A-Za-z0-9]{2,}))?\b", prose):
        head = m.group(1)
        if head.lower() in STOPWORDS or head in stems or head.isupper():
            continue
        out.add(head)
        if m.group(2):
            out.add(m.group(2))
    return out


def iter_scope_symbols(rules_dir):
    """Yield (file, lineno, rule, symbol) for every candidate symbol on every `scope:` line.

    Emits a (file, 0, rule, None) sentinel per scope line so callers can count lines without re-parsing.
    """
    for fn in sorted(os.listdir(rules_dir)):
        if not fn.endswith(".md") or fn == "README.md":
            continue  # README documents the FORMAT; its examples are placeholders
        path = os.path.join(rules_dir, fn)
        rule = None
        with open(path, encoding="utf-8", errors="replace") as fh:
            for lineno, line in enumerate(fh, 1):
                # The `[a-z]?` suffix is load-bearing: rules split into variants (TP-01a, ED-05a..f) would
                # otherwise be reported under their un-suffixed sibling, sending the reader to edit a rule that
                # has nothing wrong with it. #567's TP-01a surfaced as "TP-01" before this.
                m = re.match(r"^### ([A-Z]+-\d+[a-z]?)", line)
                if m:
                    rule = m.group(1)
                if not line.lstrip().startswith("scope:"):
                    continue
                # A retired or unbuilt rule may legitimately name things that no longer exist.
                yield (fn, 0, rule or "?", None)
                for sym in sorted(candidate_symbols(line)):
                    yield (fn, lineno, rule or "?", sym)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # repo root
    src = os.path.join(root, "src")
    rules_dir = os.path.join(root, "rules")
    for needed in (src, rules_dir):
        if not os.path.isdir(needed):
            print("cannot find %s — run this from a Typhon checkout" % needed, file=sys.stderr)
            return 2

    blob, filenames = load_source_index(src)

    def resolves(sym):
        # A partial-class file like `SpatialRTree.Query.cs` also satisfies a scope naming `SpatialRTree.cs`.
        if sym.endswith(".cs"):
            if sym in filenames:
                return True
            stem = sym[:-3]
            return any(n == sym or n.startswith(stem + ".") for n in filenames)
        return sym in blob

    unknown, checked = [], 0
    for fn, lineno, rule, sym in iter_scope_symbols(rules_dir):
        if lineno == 0:      # sentinel: one per scope line, for the count
            checked += 1
            continue
        if not resolves(sym):
            unknown.append((fn, lineno, rule, sym))

    if not args.quiet:
        print("checked %d scope lines across %s" % (checked, rules_dir))

    if unknown:
        print("\n%d scope symbol(s) not found in src/:\n" % len(unknown))
        for fn, lineno, rule, sym in unknown:
            print("  %s:%d  %-14s  %s" % (fn, lineno, rule, sym))
        print("\nA scope line naming a symbol that does not exist makes the rule read as obsolete when it is not.")
        print("Either repoint it at the live type, or mark the rule [UNBUILT] and say so explicitly.")
        return 1

    if not args.quiet:
        print("all scope symbols resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main())
