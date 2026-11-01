#!/usr/bin/env python3
"""Runs HugeScanAssessmentTests: every scenario in its own process, on its own clone of the database.

A scenario that crashes the process or never returns cannot take the others with it, and a writing scenario cannot change what the next one reads.

    python3 scripts/huge-scan-assessment.py build --dir D --rows 4000000
    python3 scripts/huge-scan-assessment.py run   --dir D [--match REGEX] [--live] [--cache-mib 64] [--timeout 600] [--results results.jsonl]
    python3 scripts/huge-scan-assessment.py table --dir D [--results results.jsonl]

Needs Release built first. A clone is `cp -c` (APFS) or `cp --reflink=auto`, so the base costs one copy of its size at most.
"""
import argparse
import json
import os
import pathlib
import platform
import re
import shutil
import signal
import subprocess
import sys
import time

REPO = pathlib.Path(__file__).resolve().parent.parent
PROJECT = REPO / "test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj"
FIXTURE = "HugeScanAssessmentTests"


def engine_env(args):
    env = dict(os.environ)
    env.update({
        "TYPHON_HS_DIR": args.dir,
        # What a deployed engine runs with: the test project's telemetry file turns strict checks and the profiler on.
        "TYPHON__CHECKS__ENABLED": "false",
        "TYPHON__PROFILER__ENABLED": "false",
        "TYPHON__PROFILER__CPUSAMPLING__ENABLED": "false",
    })
    return env


def dotnet_test(filter_expr, env, timeout):
    """Returns (exit code or None on timeout, seconds, tail of the output)."""
    cmd = ["dotnet", "test", str(PROJECT), "-c", "Release", "--no-build", "--filter", filter_expr, "--logger", "console;verbosity=normal"]
    start = time.monotonic()
    proc = subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace", start_new_session=True)
    try:
        out, _ = proc.communicate(timeout=timeout)
        return proc.returncode, time.monotonic() - start, out
    except subprocess.TimeoutExpired:
        os.killpg(proc.pid, signal.SIGKILL)
        out, _ = proc.communicate()
        return None, time.monotonic() - start, out


def clone(src, dst):
    if dst.exists():
        shutil.rmtree(dst)
    flag = "-c" if platform.system() == "Darwin" else "--reflink=auto"
    subprocess.run(["cp", flag, "-R", str(src), str(dst)], check=True)


def scenarios(args, env):
    listing = pathlib.Path(args.dir) / "scenarios.txt"
    code, _, out = dotnet_test(f"FullyQualifiedName~{FIXTURE}.ListScenarios", dict(env, TYPHON_HS_LIST=str(listing)), 120)
    if code != 0:
        sys.exit(out)
    return listing.read_text().split()


def cmd_build(args):
    env = dict(engine_env(args), TYPHON_HS_ROWS=str(args.rows))
    code, secs, out = dotnet_test(f"FullyQualifiedName~{FIXTURE}.Build", env, 7200)
    print(out[-1500:])
    print(f"build: exit {code}, {secs:.0f} s")
    sys.exit(0 if code == 0 else 1)


def cmd_run(args):
    env = engine_env(args)
    env["TYPHON_HS_CACHE_MIB"] = str(args.cache_mib)
    if args.live:
        env["TYPHON_HS_LIVE"] = "1"
    results = pathlib.Path(args.dir) / args.results
    env["TYPHON_HS_RESULTS"] = str(results)
    base = pathlib.Path(args.dir) / "base"
    run_dir = pathlib.Path(args.dir) / "run"
    env["TYPHON_HS_DB"] = str(run_dir / "hs.typhon")
    every = scenarios(args, env)
    assert not any(a != b and a in b for a in every for b in every), "a scenario name inside another would select both"
    names = [n for n in every if re.search(args.match, n)]
    for i, name in enumerate(names):
        clone(base, run_dir)
        before = results.stat().st_size if results.exists() else 0
        # By substring: the filter grammar has no quoting for a test-case argument, and no scenario name is contained in another (checked above).
        code, secs, out = dotnet_test(f"FullyQualifiedName~{FIXTURE}.Scan&FullyQualifiedName~{name}", env, args.timeout)
        wrote = results.exists() and results.stat().st_size > before
        if code is None or not wrote:
            outcome = "HANG" if code is None else f"PROCESS-DIED({code})"
            with results.open("a") as f:
                f.write(json.dumps({"scenario": name, "live": args.live, "cacheMiB": args.cache_mib, "outcome": outcome, "wallSeconds": secs,
                                    "tail": out[-1500:]}) + "\n")
        elif code != 0 and "Failed" not in out:
            print(out[-800:])
        status = "HANG" if code is None else ("died" if not wrote else ("pass" if code == 0 else "red"))
        print(f"[{i + 1}/{len(names)}] {name}: {status} ({secs:.0f} s)", flush=True)
        shutil.rmtree(run_dir, ignore_errors=True)


def cmd_table(args):
    rows = [json.loads(line) for line in (pathlib.Path(args.dir) / args.results).read_text().splitlines()]
    print(f"{'scenario':34}{'outcome':14}{'s':>7}{'µs/row':>8}{'reads':>9}{'epoch':>7}{'slot':>6}{'dirty':>7}{'bp':>6}{'heapMiB':>9}{'allocMiB':>10}{'hot%':>6}{'wr p99/max ms':>18}")
    for r in rows:
        if "scanMs" not in r:
            print(f"{r['scenario']:34}{r['outcome'][:13]:14}{r.get('wallSeconds', 0):>7.0f}")
            continue
        wr = r.get("writerScan") or {}
        print(f"{r['scenario']:34}{r['outcome'].split(':')[0][:13]:14}{r['scanMs'] / 1000:>7.1f}{r['usPerRow']:>8.2f}{r['diskReads']:>9}{r['peakEpochPages']:>7}"
              f"{r['peakSlotRefPages']:>6}{r['peakDirtyPages']:>7}{r['backpressureRounds']:>6}{r['peakManagedMiB']:>9.0f}{r['allocMiB']:>10.0f}{r['hotSurvival'] * 100:>6.0f}"
              f"{(str(round(wr.get('p99', 0), 1)) + '/' + str(round(wr.get('max', 0), 1))) if wr else '':>18}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["build", "run", "table"])
    parser.add_argument("--dir", required=True)
    parser.add_argument("--rows", type=int, default=4_000_000)
    parser.add_argument("--match", default=".")
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--cache-mib", type=int, default=64)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--results", default="results.jsonl")
    args = parser.parse_args()
    {"build": cmd_build, "run": cmd_run, "table": cmd_table}[args.command](args)


if __name__ == "__main__":
    main()
