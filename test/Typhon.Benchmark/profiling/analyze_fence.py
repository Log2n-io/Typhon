#!/usr/bin/env python3
"""
Tick-fence subtree analyzer for dotTrace Reporter XML (#882).

`analyze_profile.py` ranks the whole process. This one answers a narrower question: of the time spent INSIDE the tick
fence, which methods spend it. It works off the per-instance call stacks the Reporter emits, so a method that is also
called from user systems is counted only for the calls that happened under the fence.

Usage:
    python analyze_fence.py <report.xml> [--top N] [--root REGEX] [--depth N]
"""

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

# Frames that mean "we are inside the tick fence". Covers the serial entry point, the per-archetype phases, and the
# parallel path's work items, because the runtime reaches the fence through the scheduler rather than through
# WriteTickFence.
DEFAULT_ROOT = r"TyphonRuntime\.RunParallelFence|DatabaseEngine\.WriteTickFenceCore"


def short(fqn: str) -> str:
    base = fqn.split("(")[0]
    if "+" in base:
        base = base[base.rfind("+") + 1:]
    parts = base.rsplit(".", 2)
    return ".".join(parts[-2:]) if len(parts) >= 2 else base


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("report")
    ap.add_argument("--top", type=int, default=30)
    ap.add_argument("--root", default=DEFAULT_ROOT, help="regex matching the outermost fence frame")
    ap.add_argument("--depth", type=int, default=0, help="also print the N deepest distinct stacks under the root")
    args = ap.parse_args()

    root_re = re.compile(args.root, re.IGNORECASE)
    tree = ET.parse(args.report)
    xroot = tree.getroot()

    own_in_fence = defaultdict(float)     # fqn -> own ms attributed to calls under a fence frame
    calls_in_fence = defaultdict(int)
    own_total = defaultdict(float)        # fqn -> own ms everywhere
    fence_roots = defaultdict(float)      # the outermost fence frame -> total ms
    process_own = 0.0

    for f in xroot.findall("Function"):
        fqn = f.get("FQN", "")
        for inst in f.findall("Instance"):
            own = float(inst.get("OwnTime", 0))
            total = float(inst.get("TotalTime", 0))
            stack = inst.get("CallStack", "").split("/") if inst.get("CallStack") else []
            own_total[fqn] += own
            process_own += own

            hit = next((i for i, fr in enumerate(stack) if root_re.search(fr)), None)
            if hit is None and root_re.search(fqn):
                # The fence entry point itself: its TotalTime is the subtree we are measuring.
                fence_roots[fqn] += total
                own_in_fence[fqn] += own
                calls_in_fence[fqn] += int(inst.get("Calls", 0) or 0)
            elif hit is not None:
                own_in_fence[fqn] += own
                calls_in_fence[fqn] += int(inst.get("Calls", 0) or 0)
                fence_roots[stack[hit]] += 0.0

    fence_own = sum(own_in_fence.values())

    print("=" * 118)
    print("  TICK FENCE — time attributed to calls made UNDER a fence frame (tracing mode, exact call counts)")
    print("=" * 118)
    print(f"  process own time (all Typhon frames) : {process_own:12.1f} ms")
    print(f"  own time under the fence             : {fence_own:12.1f} ms   ({fence_own / process_own * 100:.1f} % of it)")
    print()

    if fence_roots:
        print("  Fence entry points seen (inclusive time of the subtree):")
        for name, t in sorted(fence_roots.items(), key=lambda kv: -kv[1])[:12]:
            if t > 0:
                print(f"    {t:12.1f} ms  {short(name)}")
        print()

    print("-" * 118)
    print(f"  {'own ms':>12}  {'% fence':>8}  {'calls':>12}   method")
    print("-" * 118)
    for fqn, ms in sorted(own_in_fence.items(), key=lambda kv: -kv[1])[:args.top]:
        pct = ms / fence_own * 100 if fence_own else 0
        print(f"  {ms:12.1f}  {pct:7.1f}%  {calls_in_fence[fqn]:12,}   {short(fqn)}")

    print()
    print("  NOTE Tracing instruments every call, so ABSOLUTE times carry large overhead and are not comparable to a")
    print("       wall-clock benchmark. The RATIOS between methods are what this is for.")


if __name__ == "__main__":
    sys.exit(main())
