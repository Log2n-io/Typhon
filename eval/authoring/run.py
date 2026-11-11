#!/usr/bin/env python3
"""Typhon authoring eval driver (#1649). See README.md for the protocol.

  run.py pack     --feed DIR --version V
  run.py prepare  --feed DIR --version V --docs DIR --work DIR [--tasks T01,T02]
  run.py validate --feed DIR --version V --docs DIR --work DIR [--tasks ...]   # reference solutions, must pass
  run.py check    --feed DIR --version V --work DIR [--tasks ...] [--out FILE] [--label TEXT]
"""
import argparse
import datetime
import json
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
TASKS = [f"T{i:02d}" for i in range(1, 11)]
PACK_PROJECTS = ["src/Typhon.Protocol/Typhon.Protocol.csproj", "src/Typhon.Engine/Typhon.Engine.csproj"]
TEST_TIMEOUT_S = 300
# NuGet packages folder. Kept SHORT: package-relative paths reach ~120 chars and Windows MSBuild fails to copy past 260.
PACKAGES = Path(tempfile.gettempdir()) / "tev-pkgs"


def sh(cmd, cwd=None, env=None, timeout=None):
    r = subprocess.run(cmd, cwd=cwd, env=env, timeout=timeout, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode, r.stdout + r.stderr


def task_list(arg):
    return arg.split(",") if arg else TASKS


def write_workspace_files(ws: Path, feed: Path, version: str, packages: Path):
    (ws / "Solution.csproj").write_text((HERE / "template/Solution.csproj").read_text())
    (ws / "nuget.config").write_text((HERE / "template/nuget.config").read_text()
                                     .replace("$FEED$", str(feed)).replace("$PACKAGES$", str(packages)))
    (ws / "Directory.Build.props").write_text((HERE / "template/Directory.Build.props").read_text().replace("$VERSION$", version))


def cmd_pack(a):
    feed = Path(a.feed)
    feed.mkdir(parents=True, exist_ok=True)
    for p in PACK_PROJECTS:
        rc, out = sh(["dotnet", "pack", str(REPO / p), "-c", "Release", f"-p:MinVerVersionOverride={a.version}", "-o", str(feed), "-nologo", "-v", "q"])
        if rc != 0:
            sys.exit(f"pack failed for {p}:\n{out}")
    print(f"packed {a.version} into {feed}")


def cmd_prepare(a, reference=False):
    feed, work, docs = Path(a.feed).resolve(), Path(a.work).resolve(), Path(a.docs).resolve()
    packages = PACKAGES
    for t in task_list(a.tasks):
        ws = work / t
        if ws.exists():
            shutil.rmtree(ws)
        ws.mkdir(parents=True)
        write_workspace_files(ws, feed, a.version, packages)
        shutil.copytree(docs, ws / "docs")
        (ws / "TASK.md").write_text((HERE / "preamble.md").read_text() + "\n" + (HERE / "tasks" / t / "TASK.md").read_text())
        starter = HERE / "tasks" / t / "starter"
        if starter.exists():
            shutil.copytree(starter, ws, dirs_exist_ok=True)
        if reference:
            shutil.copytree(HERE / "reference" / t, ws, dirs_exist_ok=True)
    print(f"prepared {', '.join(task_list(a.tasks))} in {work}")


def build_seed(work: Path, feed: Path, version: str):
    seed = work / "_seed"
    if seed.exists():
        shutil.rmtree(seed)
    seed.mkdir(parents=True)
    write_workspace_files(seed, feed, version, PACKAGES)
    (seed / "Solution.csproj").unlink()
    shutil.copy(HERE / "seed/Seed.csproj", seed)
    shutil.copy(HERE / "seed/Program.cs", seed)
    shutil.copy(HERE / "tasks/T07/starter/Model.cs", seed / "ModelV1.cs")
    rc, out = sh(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", "-o", str(seed / "out")], cwd=seed)
    if rc != 0:
        sys.exit(f"seed build failed:\n{out}")
    return seed / "out" / "Seed.dll"


def parse_trx(path: Path):
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    root = ET.parse(path).getroot()
    results = []
    for r in root.iter("{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
        msg = r.find("t:Output/t:ErrorInfo/t:Message", ns)
        results.append((r.get("testName"), r.get("outcome"), (msg.text or "").strip() if msg is not None else ""))
    return results


def cmd_check(a):
    feed, work = Path(a.feed).resolve(), Path(a.work).resolve()
    seed_dll = None
    report = []
    for t in task_list(a.tasks):
        ws = work / t
        entry = {"task": t, "build": None, "checks": [], "note": ""}
        rc, out = sh(["dotnet", "build", "Solution.csproj", "-nologo", "-v", "q"], cwd=ws)
        entry["build"] = rc == 0
        if rc != 0:
            entry["note"] = "\n".join(l for l in out.splitlines() if "error" in l)[:2000]
            report.append(entry)
            print(f"{t}: BUILD FAILED")
            continue
        chk = work / "_checkers" / t
        if chk.exists():
            shutil.rmtree(chk)
        shutil.copytree(HERE / "checker", chk)
        write_workspace_files(chk, feed, a.version, PACKAGES)
        (chk / "Solution.csproj").unlink()
        env = dict(os.environ)
        env["EVAL_SOLUTION_DIR"] = str(ws)
        env["TYPHON__CHECKS__ENABLED"] = "true"
        env["TYPHON__CHECKS__DECLAREDACCESS"] = "true"
        if t == "T07":
            seed_dll = seed_dll or build_seed(work, feed, a.version)
            env["EVAL_T07_SEED"] = str(seed_dll)
        trx_dir = chk / "trx"
        try:
            rc, out = sh(["dotnet", "test", str(chk / "Checker.csproj"), f"-p:TaskId={t}", f"-p:SolutionProject={ws / 'Solution.csproj'}",
                          "-nologo", "--logger", "trx;LogFileName=r.trx", "--results-directory", str(trx_dir)],
                         cwd=chk, env=env, timeout=TEST_TIMEOUT_S)
        except subprocess.TimeoutExpired:
            entry["note"] = f"checker timed out after {TEST_TIMEOUT_S}s"
            report.append(entry)
            print(f"{t}: TIMEOUT")
            continue
        trx = trx_dir / "r.trx"
        if not trx.exists():
            entry["note"] = "checker did not run (contract mismatch?):\n" + "\n".join(l for l in out.splitlines() if "error" in l)[:2000]
            report.append(entry)
            print(f"{t}: CHECKER COMPILE FAILED")
            continue
        entry["checks"] = parse_trx(trx)
        report.append(entry)
        passed = sum(1 for c in entry["checks"] if c[1] == "Passed")
        print(f"{t}: {passed}/{len(entry['checks'])}")
    if a.out:
        write_report(Path(a.out), report, a)
    (work / "results.json").write_text(json.dumps(report, indent=2))
    return report


def write_report(path: Path, report, a):
    lines = [f"# Authoring eval — {a.label or path.stem}", "",
             f"Date: {datetime.date.today().isoformat()} · package `{a.version}` · work dir `{Path(a.work).name}`", ""]
    total = sum(len(e["checks"]) for e in report)
    passed = sum(1 for e in report for c in e["checks"] if c[1] == "Passed")
    tasks_ok = sum(1 for e in report if e["build"] and e["checks"] and all(c[1] == "Passed" for c in e["checks"]))
    lines += [f"**Tasks fully correct: {tasks_ok}/{len(report)} · checks passed: {passed}/{total}**", "",
              "| Task | Build | Check | Result | Detail |", "|---|---|---|---|---|"]
    for e in report:
        if not e["build"] or not e["checks"]:
            lines.append(f"| {e['task']} | {'ok' if e['build'] else 'FAIL'} | — | FAIL | {e['note'].splitlines()[0] if e['note'] else ''} |")
            continue
        for name, outcome, msg in e["checks"]:
            detail = msg.splitlines()[0].replace("|", "\\|")[:180] if msg else ""
            lines.append(f"| {e['task']} | ok | {name} | {'pass' if outcome == 'Passed' else 'FAIL'} | {detail} |")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n")
    print(f"report: {path}")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("command", choices=["pack", "prepare", "validate", "check"])
    p.add_argument("--feed", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--docs")
    p.add_argument("--work")
    p.add_argument("--tasks")
    p.add_argument("--out")
    p.add_argument("--label")
    a = p.parse_args()
    if a.command == "pack":
        cmd_pack(a)
    elif a.command == "prepare":
        cmd_prepare(a)
    elif a.command == "validate":
        cmd_prepare(a, reference=True)
        report = cmd_check(a)
        bad = [e["task"] for e in report if not (e["build"] and e["checks"] and all(c[1] == "Passed" for c in e["checks"]))]
        if bad:
            sys.exit(f"reference solutions failing: {bad}")
        print("all reference solutions pass")
    else:
        cmd_check(a)


if __name__ == "__main__":
    main()
