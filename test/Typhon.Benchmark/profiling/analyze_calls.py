#!/usr/bin/env python3
"""
Per-arm CALL COUNTS and self time from a dotTrace Tracing report.

`analyze_profile.py` ranks functions by self time across the whole process, which is the right view for a
sampling snapshot. A TRACING snapshot carries something a sampling one cannot: an exact `Calls` attribute on
every function and on every call-stack instance of it. That is the number this script is for — under tracing
the nanoseconds are inflated by instrumentation (and by the inlining it suppresses), but the counts are facts.

Instances are attributed to an ARM by looking for a marker frame in their call stack, so two workloads running
in one process — here the R-Tree query and the linear scan it competes with — are separated without needing
two snapshots taken minutes apart on a shared box.

Usage:
    python3 analyze_calls.py <report.xml> --arm tree=ScanTree --arm scan=ScanLinear [--reps N] [--top N]
"""
import argparse
import collections
import re
import xml.etree.ElementTree as ET


def main():
    ap = argparse.ArgumentParser(description="Per-arm call counts from a dotTrace Tracing report")
    ap.add_argument("report")
    ap.add_argument("--arm", action="append", default=[],
                    help="name=MarkerFrame — attribute instances whose call stack contains MarkerFrame")
    ap.add_argument("--reps", type=int, default=0, help="iterations per arm, to report calls PER QUERY")
    ap.add_argument("--top", type=int, default=25)
    ap.add_argument("--filter", default=None, help="regex on the function name")
    args = ap.parse_args()

    arms = []
    for spec in args.arm:
        name, _, marker = spec.partition("=")
        arms.append((name, marker or name))
    if not arms:
        arms = [("all", "")]

    pat = re.compile(args.filter) if args.filter else None

    # calls[arm][fqn] and own[arm][fqn]
    calls = {a: collections.Counter() for a, _ in arms}
    own = {a: collections.Counter() for a, _ in arms}

    current = None
    for event, el in ET.iterparse(args.report, events=("start", "end")):
        if event == "start" and el.tag == "Function":
            current = el.get("FQN", "?")
        elif event == "end" and el.tag == "Instance":
            stack = el.get("CallStack", "")
            for arm, marker in arms:
                if marker and marker not in stack:
                    continue
                calls[arm][current] += int(el.get("Calls", 0))
                own[arm][current] += float(el.get("OwnTime", 0.0))
            el.clear()
        elif event == "end" and el.tag == "Function":
            el.clear()

    for arm, marker in arms:
        rows = calls[arm]
        if not rows:
            print(f"\n── arm {arm!r} (marker {marker!r}): no instances matched")
            continue

        total_own = sum(own[arm].values())
        print(f"\n── arm {arm!r} (marker {marker!r}) — {sum(rows.values()):,} instrumented calls, {total_own:.1f} ms self")
        per = f"{'calls/query':>12}" if args.reps else ""
        print(f"  {'calls':>12} {per} {'self ms':>9} {'self %':>7}  function")
        shown = 0
        for fqn, n in rows.most_common():
            if pat and not pat.search(fqn):
                continue
            name = short(fqn)
            pq = f"{n / args.reps:12.1f}" if args.reps else ""
            o = own[arm][fqn]
            print(f"  {n:12,} {pq} {o:9.1f} {100.0 * o / total_own if total_own else 0:6.1f}%  {name}")
            shown += 1
            if shown >= args.top:
                break


def short(fqn):
    """Trim the argument list and the namespace, keeping Type.Method — the form the other analyzer prints."""
    head = fqn.split("(")[0]
    parts = head.split(".")
    return ".".join(parts[-2:]) if len(parts) >= 2 else head


if __name__ == "__main__":
    main()
