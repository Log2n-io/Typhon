#!/usr/bin/env python3
"""
lint-native-allocations.py — a native allocation goes through the allocator, or says why not.

WHY. `IMemoryAllocator.AllocatePinned` is not just a malloc: it parents the block in the resource tree, gives it an
id, and counts it into `MemoryUnmanagedTotalBytes` / `PeakBytes` / `LiveBlocks`. A `NativeMemory.Alloc` called
directly does none of that, so the memory is invisible to the gauges an operator reads and to the leak diagnostics a
teardown is debugged from. An audit on 2026-09-27 found **21 such sites** across subscriptions, collections and the
spawn arena — none obviously leaking, none counted either. "How much native memory does the engine hold?" had a
21-site blind spot, and nothing noticed because nothing was looking.

The rule this enforces is not "never call NativeMemory". Some sites have a real reason: growable per-call scratch that
would churn the resource tree, or an arena whose whole design is that blocks are never moved. The rule is that the
reason is WRITTEN DOWN at the call site, so the next reader inherits the judgement instead of re-deriving it.

    // native-alloc: <reason>          on, or immediately above, the allocating line

Checks:

    UNANNOTATED_NATIVE_ALLOC   a NativeMemory.Alloc / AllocZeroed / AlignedAlloc / Realloc with no `native-alloc:`
                               reason anywhere in the 3 lines above it or on the line itself

Exempt by construction: `PinnedMemoryBlock` itself (it IS the allocator's native path) and anything outside
`src/`. Test code is not scanned — a fixture's own scratch is its own business.

Usage:
    python3 scripts/lint-native-allocations.py            # repo root
    python3 scripts/lint-native-allocations.py --list     # print every site and its reason, then exit 0
"""

import argparse
import pathlib
import re
import sys

ALLOC = re.compile(r"NativeMemory\.(?:Alloc|AllocZeroed|AlignedAlloc|Realloc)\s*\(")
REASON = re.compile(r"native-alloc:\s*(\S.*)")

# The allocator's own native path. Everything else in the engine is supposed to go through it.
EXEMPT_FILES = {"Foundation/Memory/internals/PinnedMemoryBlock.cs"}

LOOKBEHIND = 3


def scan(root):
    findings, sites = [], []
    for path in sorted((root / "src").rglob("*.cs")):
        rel = path.relative_to(root / "src").as_posix()
        rel = rel.split("/", 1)[1] if "/" in rel else rel
        if rel in EXEMPT_FILES:
            continue

        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        for i, line in enumerate(lines):
            if not ALLOC.search(line):
                continue

            window = lines[max(0, i - LOOKBEHIND):i + 1]
            reason = next((m.group(1).strip() for m in (REASON.search(w) for w in window) if m), None)
            display = f"{path.relative_to(root).as_posix()}:{i + 1}"
            sites.append((display, reason))
            if reason is None:
                findings.append((display, line.strip()))

    return findings, sites


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true", help="print every site and its reason, then exit 0")
    ap.add_argument("--root", default=".", help="repository root")
    args = ap.parse_args()

    root = pathlib.Path(args.root).resolve()
    findings, sites = scan(root)

    if args.list:
        for where, reason in sites:
            print(f"  {where}\n      {reason or '(NO REASON)'}")
        print(f"\nnative-allocation lint: {len(sites)} site(s), {len(findings)} without a reason")
        return 0

    if not findings:
        print(f"native-allocation lint: clean ({len(sites)} annotated site(s))")
        return 0

    print(f"=== UNANNOTATED_NATIVE_ALLOC - {len(findings)} violation(s) ===")
    print("    A direct NativeMemory allocation is invisible to the resource tree, the telemetry ids and the")
    print("    unmanaged-memory gauges. Route it through IMemoryAllocator.AllocatePinned, or state the reason it")
    print("    cannot be, as `// native-alloc: <reason>` on the line or just above it.\n")
    for where, text in findings:
        print(f"  {where}: {text}")

    print(f"\nnative-allocation lint: {len(findings)} violation(s) across 1 check(s)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
