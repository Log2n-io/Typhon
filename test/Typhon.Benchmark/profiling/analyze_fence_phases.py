#!/usr/bin/env python3
"""
Per-PHASE tick-fence analyzer for dotTrace Reporter XML.

`analyze_fence.py` answers "of the time under the fence, which methods spend it" as one flat ranking. This one splits
that ranking by the fence's six phases first, because the phases are dispatched as separate systems with separate work
items and a method that appears in two of them (GetChunkAddress, say) is doing different work in each.

Attribution rule: an Instance is charged to the INNERMOST phase-root frame in its call stack. Phase roots are the
per-phase exec systems' DispatchItem / Prepare / Execute frames, which is where the runtime hands one work item to one
worker — so a phase's DispatchItem call count IS the number of work items that phase ran.

Usage:
    python analyze_fence_phases.py <report.xml> [--top N] [--json out.json]
"""

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

# Ordered: earlier entries win ties only by position in the stack, not by this order.
PHASES = [
    ("Prep",       r"FencePrepExecSystem"),
    ("Migrate",    r"FenceMigrateExecSystem"),
    ("IndexMass",  r"FenceIndexMassUpdateExecSystem"),
    ("EntityMap",  r"FenceEntityMapUpdateExecSystem"),
    ("AabbRefresh", r"FenceAabbRefreshExecSystem"),
    ("Finalize",   r"FenceFinalizeExecSystem"),
    ("AtomicSerial", r"FenceAtomicExecSystem|WriteTickFenceCore"),
]
PHASE_RE = [(name, re.compile(pat)) for name, pat in PHASES]

# Frames worth calling out by name in every phase, because they are the ones the design argues about.
PLAN_RE = re.compile(r"FenceWorkPlan|FenceDagBuilder|LiveFenceCostModel")


def short(fqn: str) -> str:
    base = fqn.split("(")[0]
    if "+" in base:
        base = base[base.rfind("+") + 1:]
    parts = base.rsplit(".", 2)
    return ".".join(parts[-2:]) if len(parts) >= 2 else base


def phase_of(stack):
    """Innermost phase root in the stack (stacks are outermost-first), else None."""
    for frame in reversed(stack):
        for name, rx in PHASE_RE:
            if rx.search(frame):
                return name
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("report")
    ap.add_argument("--top", type=int, default=18)
    ap.add_argument("--json")
    args = ap.parse_args()

    tree = ET.parse(args.report)
    xroot = tree.getroot()

    own = defaultdict(lambda: defaultdict(float))    # phase -> fqn -> own ms
    calls = defaultdict(lambda: defaultdict(int))    # phase -> fqn -> calls
    entry_total = defaultdict(float)                 # phase -> inclusive ms of its exec-system entry
    entry_calls = defaultdict(int)
    plan_own = 0.0
    plan_calls = defaultdict(int)
    plan_ms = defaultdict(float)
    process_own = 0.0

    for f in xroot.findall("Function"):
        fqn = f.get("FQN", "")
        for inst in f.findall("Instance"):
            o = float(inst.get("OwnTime", 0))
            t = float(inst.get("TotalTime", 0))
            c = int(inst.get("Calls", 0) or 0)
            stack = inst.get("CallStack", "").split("/") if inst.get("CallStack") else []
            process_own += o

            if PLAN_RE.search(fqn):
                plan_own += o
                plan_calls[fqn] += c
                plan_ms[fqn] += o

            ph = phase_of(stack + [fqn])
            if ph is None:
                continue

            # The exec-system frame itself: its TotalTime is the phase's whole subtree on that worker.
            if any(rx.search(fqn) for _, rx in PHASE_RE):
                entry_total[ph] += t
                entry_calls[ph] += c

            own[ph][fqn] += o
            calls[ph][fqn] += c

    total_fence_own = sum(sum(d.values()) for d in own.values())

    print("=" * 122)
    print("  TICK FENCE BY PHASE — own time attributed to the innermost phase-root frame (tracing mode: call counts exact)")
    print("=" * 122)
    print(f"  process own time (all instrumented frames) : {process_own:12.1f} ms")
    print(f"  own time under a fence phase               : {total_fence_own:12.1f} ms  ({total_fence_own/process_own*100:.1f} %)")
    print()
    print(f"  {'phase':<14} {'own ms':>11} {'% fence':>9} {'subtree ms':>12}   (subtree = inclusive time of the phase's exec system)")
    print("  " + "-" * 118)
    for name, _ in PHASES:
        if name not in own:
            continue
        po = sum(own[name].values())
        print(f"  {name:<14} {po:11.1f} {po/total_fence_own*100:8.1f}% {entry_total[name]:12.1f}")
    print()

    if plan_ms:
        print("  Planner / DAG (cost of DECIDING the work, not doing it):")
        for fqn, ms in sorted(plan_ms.items(), key=lambda kv: -kv[1])[:8]:
            print(f"    {ms:10.1f} ms  {plan_calls[fqn]:12,} calls   {short(fqn)}")
        print()

    for name, _ in PHASES:
        if name not in own:
            continue
        po = sum(own[name].values())
        print("-" * 122)
        print(f"  PHASE {name}   own {po:.1f} ms   ({po/total_fence_own*100:.1f} % of fence own time)")
        print("-" * 122)
        print(f"  {'own ms':>10} {'% phase':>8} {'calls':>13} {'ns/call':>10}   method")
        for fqn, ms in sorted(own[name].items(), key=lambda kv: -kv[1])[:args.top]:
            c = calls[name][fqn]
            per = (ms * 1e6 / c) if c else 0
            print(f"  {ms:10.1f} {ms/po*100:7.1f}% {c:13,} {per:10.0f}   {short(fqn)}")
        print()

    if args.json:
        out = {
            "process_own_ms": process_own,
            "fence_own_ms": total_fence_own,
            "phases": {
                name: {
                    "own_ms": sum(own[name].values()),
                    "subtree_ms": entry_total[name],
                    "methods": [
                        {"m": short(fqn), "own_ms": ms, "calls": calls[name][fqn]}
                        for fqn, ms in sorted(own[name].items(), key=lambda kv: -kv[1])[:60]
                    ],
                }
                for name, _ in PHASES if name in own
            },
        }
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(out, fh, indent=1)
        print(f"  json -> {args.json}")

    print("  NOTE Tracing instruments every call. ABSOLUTE ms carry ~30 ns/call of profiler overhead and are NOT")
    print("       comparable to the wall-clock harness; CALL COUNTS are exact and RATIOS are what this is for.")


if __name__ == "__main__":
    sys.exit(main())
