#!/usr/bin/env python3
"""
Engine-suite test sharder for the CI merge gate.

WHY: the suite carries shared mutable PROCESS-static state (ArchetypeRegistry,
profiler rings, epoch registry). Run fixtures in parallel inside ONE process and
they pollute each other -> non-deterministic failures (cascade-diamond, OOB). The
authors' defence is 116 `[NonParallelizable]` fixtures, which pins the single
process ~91% serial -> ~9 min wall with 16 workers mostly idle.

FIX (this tool): run K `dotnet test` PROCESSES in parallel, each at workers=1.
Separate processes cannot share statics (a hard guarantee, not a probability
cut); workers=1 == a clean serial dev run, the known-green config. A final SERIAL
"quiet pass" runs the `Category=Sensitive` tests alone (timing/alloc/IO-contention
assertions that flake under any parallel load). Net locally: ~9 min -> ~55 s,
flaky-red -> green. See claude/design/Infrastructure/ci-merge-gate.md.

Modes:
  run  --results-dir DIR        execute the committed shards.json (CI uses this)
  plan --k K --trx FILE         regenerate shards.json from a trx (maintenance)

`run` is correctness-robust: shard 0 is a CATCH-ALL (negative filter = everything
not explicitly assigned to shards 1..K-1), so a newly-added test class can never
silently escape the gate -- it lands in shard 0. shards.json therefore only needs
regenerating for BALANCE, never for coverage (same contract as the
test-affected map).

Env knobs (run mode): SHARD_REPO (repo root, default cwd), SHARD_CONFIG (default
Release).
"""
import argparse, json, os, subprocess, sys, time
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor

HERE        = os.path.dirname(os.path.abspath(__file__))
REPO        = os.environ.get("SHARD_REPO") or os.getcwd()
TESTPROJ    = os.path.join(REPO, "test", "Typhon.Engine.Tests", "Typhon.Engine.Tests.csproj")
RUNSETTINGS = os.path.join(HERE, "workers1.runsettings")
SHARDS_JSON = os.path.join(HERE, "shards.json")
CFG         = os.environ.get("SHARD_CONFIG", "Release")
NS          = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
SENSITIVE_FILTER = "Category=Sensitive"   # the final serial quiet pass

# ── filters ─────────────────────────────────────────────────────────────────
# Quarantine = known-red (excluded everywhere). Sensitive = contention-flaky
# (excluded from the parallel shards, run alone in the serial pass). The trailing
# '.' after each class name disambiguates class-name prefixes (`~Foo.` never
# matches `FooBar.`).

def positive_filter(classes):
    cls = "|".join(f"FullyQualifiedName~{c}." for c in classes)
    return f"(Category!=Quarantine)&(Category!=Sensitive)&({cls})"

def catchall_filter(assigned_elsewhere):
    neg = "&".join(f"(FullyQualifiedName!~{c}.)" for c in assigned_elsewhere)
    base = "(Category!=Quarantine)&(Category!=Sensitive)"
    return f"{base}&{neg}" if neg else base

# ── plan (maintenance) ──────────────────────────────────────────────────────

def trx_universe(trx_path):
    """[(fqn, className)] + className->duration, from a trx. --list-tests only
    yields NUnit display names (method only); the trx carries the real className."""
    pairs, durs = [], {}
    root = ET.parse(trx_path).getroot()
    id2cls, id2name = {}, {}
    for ut in root.iter(NS + "UnitTest"):
        tid = ut.get("id")
        tm = ut.find(NS + "TestMethod")
        if tid and tm is not None:
            id2cls[tid], id2name[tid] = tm.get("className"), tm.get("name")
    from datetime import datetime
    def p(x): return datetime.fromisoformat(x.replace("Z", "+00:00"))
    for r in root.iter(NS + "UnitTestResult"):
        tid = r.get("testId")
        cls = id2cls.get(tid)
        if not cls:
            continue
        pairs.append((cls + "." + (id2name.get(tid) or ""), cls))
        s, e = r.get("startTime"), r.get("endTime")
        if s and e:
            durs[cls] = durs.get(cls, 0.0) + (p(e) - p(s)).total_seconds()
    return pairs, durs

def cmd_plan(k, trx_path):
    pairs, weights = trx_universe(trx_path)
    classes = sorted({cls for _, cls in pairs})
    # greedy longest-processing-time-first bin-pack by class duration
    bins, load = [[] for _ in range(k)], [0.0] * k
    for c in sorted(classes, key=lambda c: -weights.get(c, 0.01)):
        i = load.index(min(load))
        bins[i].append(c)
        load[i] += weights.get(c, 0.01)
    # bin 0 = catch-all (negative complement of bins 1..k-1)
    elsewhere = [c for b in bins[1:] for c in b]
    filters = [catchall_filter(elsewhere)] + [positive_filter(b) for b in bins[1:]]

    # SIMULATE the exact substring rule VSTest applies -> every test maps to one shard
    def hits(fqn):
        h = [j for j in range(1, k) if any((c + ".") in fqn for c in bins[j])]
        if not any((c + ".") in fqn for c in elsewhere):   # catch-all (bin 0)
            h.append(0)
        return h
    bad = [(fqn, hits(fqn)) for fqn, _ in pairs if len(hits(fqn)) != 1]
    print(f"tests={len(pairs)} classes={len(classes)} k={k}")
    for i, b in enumerate(bins):
        tag = " (catch-all)" if i == 0 else ""
        print(f"  shard {i}: {len(b):3d} classes, est {load[i]:6.1f}s{tag}")
    if bad:
        print(f"\nPARTITION BROKEN: {len(bad)} tests matched !=1 shard (showing 5):")
        for fqn, h in bad[:5]:
            print(f"  {fqn} -> {h}")
        raise SystemExit(1)
    print(f"\nPARTITION OK: all {len(pairs)} tests matched by exactly one shard.")
    print(f"est wall (max shard) = {max(load):.1f}s  vs serial sum {sum(load):.1f}s")
    json.dump([{"filter": f, "classes": (bins[i] if i else ["<catch-all>"])}
               for i, f in enumerate(filters)],
              open(SHARDS_JSON, "w"), indent=1)
    print(f"wrote {SHARDS_JSON}")

# ── run (CI) ────────────────────────────────────────────────────────────────

def run_one(label, flt, results_dir):
    trx = f"shard{label}.trx"
    cmd = ["dotnet", "test", TESTPROJ, "-c", CFG, "--no-build",
           "--filter", flt, "--settings", RUNSETTINGS,
           "--logger", f"trx;LogFileName={trx}", "--results-directory", results_dir]
    t0 = time.time()
    r = subprocess.run(cmd, capture_output=True, text=True)
    with open(os.path.join(results_dir, f"shard{label}.log"), "w",
              encoding="utf-8", errors="replace") as fh:
        fh.write(r.stdout + "\n" + r.stderr)
    return label, r.returncode, time.time() - t0, os.path.join(results_dir, trx)

def parse_trx(path):
    if not os.path.exists(path):
        return (0, 0, 0)
    c = ET.parse(path).getroot().find(NS + "ResultSummary/" + NS + "Counters")
    return (0, 0, 0) if c is None else (int(c.get("total", 0)),
                                        int(c.get("passed", 0)), int(c.get("failed", 0)))

def cmd_run(results_dir):
    shards = json.load(open(SHARDS_JSON))
    os.makedirs(results_dir, exist_ok=True)
    print(f"[shard] {len(shards)} parallel shards (workers=1) + serial Sensitive pass; "
          f"cfg={CFG} repo={REPO}", flush=True)
    t0 = time.time()
    results = []
    with ThreadPoolExecutor(max_workers=len(shards)) as ex:
        futs = [ex.submit(run_one, str(i), s["filter"], results_dir)
                for i, s in enumerate(shards)]
        for f in futs:
            results.append(f.result())
    par = time.time() - t0
    results.sort(key=lambda x: int(x[0]))
    tot = pas = fail = 0
    print("\nshard   rc   time   total  passed  failed", flush=True)
    for label, rc, dt, trx in results:
        t, p, f = parse_trx(trx); tot += t; pas += p; fail += f
        print(f"  {label:>2}  {rc:3d}  {dt:5.0f}s  {t:5d}  {p:5d}  {f:5d}", flush=True)

    print("\nserial quiet pass (Category=Sensitive, workers=1, no siblings)...", flush=True)
    s0 = time.time()
    _, src, sdt, strx = run_one("S", SENSITIVE_FILTER, results_dir)
    ser = time.time() - s0
    st, sp, sf = parse_trx(strx); tot += st; pas += sp; fail += sf
    print(f"   S  {src:3d}  {sdt:5.0f}s  {st:5d}  {sp:5d}  {sf:5d}", flush=True)

    overall = 0 if all(rc == 0 for _, rc, _, _ in results) and src == 0 else 1
    print(f"\n[shard] WALL={par + ser:.0f}s (parallel {par:.0f}s + serial {ser:.0f}s)  "
          f"total={tot} passed={pas} failed={fail}", flush=True)
    print(f"[shard] OVERALL={'PASS' if overall == 0 else 'FAIL'}", flush=True)
    return overall

# ── main ────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    pr = sub.add_parser("run");  pr.add_argument("--results-dir", required=True)
    pp = sub.add_parser("plan"); pp.add_argument("--k", type=int, default=8)
    pp.add_argument("--trx", required=True)
    a = ap.parse_args()
    if a.cmd == "plan":
        cmd_plan(a.k, a.trx)
    else:
        sys.exit(cmd_run(a.results_dir))
