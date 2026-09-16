#!/usr/bin/env python3
"""Fail when the merge gate's path filters do not cover the code its suites compile against.

Motivation
----------
The gate decides whether to run a suite from a `dorny/paths-filter` pattern list. Those patterns were written by
hand and never derived from anything, so they drifted away from what the suites actually build. Measured on
2026-09-16: the `engine` filter covered `src/Typhon.Engine/**` and `test/Typhon.Engine.Tests/**`, while the engine
test project's ProjectReference closure reaches eight further directories — Protocol, Profiler, Schema.Definition,
Analyzers, Generators, Generators.Consumer, Workbench.Fixtures and Samples.Swg. A pull request touching only
`src/Typhon.Protocol/**` therefore ran ZERO tests, and because the verdict job treats a skipped suite as a pass, the
gate reported green. `workbench` had the same hole on its own side.

That is the #774 failure class, which had already cost one green gate over an unrun test. It is not a thing to fix
once: the closure changes whenever someone adds a ProjectReference, and nothing would notice. So this derives the
closure from the project graph and compares it against the filters, every run.

It also catches the other half: a test project that no job runs at all. Nine of the repository's projects under
test/ were in no workflow when this was written.

Why the job table is declared and not parsed
--------------------------------------------
The `dotnet test` invocations are NOT in the workflow. merge-gate.yml launches bench/aws/run-gate.sh (and its
maintained parallel copy bench/aws/ci.sky.yaml), and those scripts name the projects. Deriving "which job runs which
suite" from the workflow alone would happily report full coverage of whatever those scripts do not run — exactly the
false green this script exists to remove. The mapping therefore lives in `scripts/gate-jobs.json`, and this checker
asserts that every job it names is defined in the workflow and every project it names exists — so the table cannot
claim coverage that does not exist.

A test project is one that can FAIL
-----------------------------------
Classification is `Microsoft.NET.Test.Sdk` (or `<IsTestProject>`) AND at least one NUnit test attribute. The SDK
alone is not sufficient: `test/Typhon.Shell.Tests.SchemaFixture` carries it and contains no tests, because it is a
fixture assembly consumed by another suite. Flagging it as "ungated" would push someone to add a job that runs
nothing, which is the failure mode inverted. Benchmarks and runners (Typhon.Benchmark, Typhon.CompetitiveBenchmark,
IOProfileRunner, MonitoringDemo) fall out of the same test, by property rather than by name.

Usage
-----
    python3 scripts/check-gate-filters.py [--quiet] [--no-github]

`--no-github` skips the open-issue probe for allow-list entries (used on runners without a token; the `invariants`
job supplies one). Exit codes: 0 clean, 1 violations, 2 usage or environment error.

This is a lint, not a proof. It reasons about DIRECTORIES: it can tell you a closure directory is unmatched, not
that a pattern is too broad.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKFLOW = ".github/workflows/merge-gate.yml"
SOLUTION = "Typhon.slnx"
ALLOWLIST = "scripts/gate-filter-allowlist.json"
JOBS_FILE = "scripts/gate-jobs.json"

TEST_ATTR = re.compile(r"\[\s*(Test|TestCase|TestCaseSource|TestFixture)\b")


def fail(msg):
    print(f"::error::{msg}" if os.environ.get("GITHUB_ACTIONS") else f"ERROR: {msg}")


def read(path):
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def parse_filters(workflow_text):
    """Return {filter_name: [patterns]} and the 1-based line of the `filters:` block.

    Hand-rolled rather than PyYAML: the other policy scripts are stdlib-only, and the block has a fixed shape —
    `filters: |` followed by `name:` and `- 'pattern'` lines. A malformed block raises, which is a failure the
    caller reports rather than swallows.
    """
    lines = workflow_text.splitlines()
    start = next((i for i, ln in enumerate(lines) if re.match(r"\s*filters:\s*\|", ln)), None)
    if start is None:
        raise ValueError("no `filters: |` block found")
    base = len(lines[start]) - len(lines[start].lstrip())
    out, current = {}, None
    for ln in lines[start + 1:]:
        if not ln.strip():
            continue
        indent = len(ln) - len(ln.lstrip())
        if indent <= base:
            break
        s = ln.strip()
        if s.startswith("#"):
            continue
        if s.startswith("- "):
            if current is None:
                raise ValueError(f"pattern outside any filter: {s}")
            out[current].append(s[2:].strip().strip("'\""))
        elif s.endswith(":"):
            current = s[:-1].strip()
            out[current] = []
    return out, start + 1


def workflow_jobs(workflow_text):
    """Top-level job names, so the table below cannot claim coverage by a job the workflow does not define.

    Without this the table is unfalsifiable in the one direction that matters: naming a job that does not exist
    marks its suites as gated, silences the ungated-suite check for them, and reports green.
    """
    lines = workflow_text.splitlines()
    start = next((i for i, ln in enumerate(lines) if re.match(r"^jobs:\s*$", ln)), None)
    if start is None:
        return set()
    out = set()
    for ln in lines[start + 1:]:
        if ln.strip() and not ln.startswith(" "):
            break
        m = re.match(r"^  ([A-Za-z_][\w-]*):\s*$", ln)
        if m:
            out.add(m.group(1))
    return out


def solution_projects(sln_path):
    """Project paths listed in the .slnx, repo-relative with forward slashes."""
    root = ET.parse(sln_path).getroot()
    return sorted({p.get("Path").replace("\\", "/") for p in root.iter("Project") if p.get("Path")})


def project_references(csproj_abs):
    """Direct ProjectReferences of one project, repo-relative.

    Analyzer references count. `ReferenceOutputAssembly="false"` means the assembly is not linked, but a source
    generator or analyzer changes the COMPILED OUTPUT of its consumer, so a change there can break the suite. The
    same holds for build-order-only references.
    """
    try:
        root = ET.parse(csproj_abs).getroot()
    except (OSError, ET.ParseError):
        return []
    here = os.path.dirname(csproj_abs)
    out = []
    for node in root.iter("ProjectReference"):
        inc = node.get("Include")
        if not inc:
            continue
        target = os.path.normpath(os.path.join(here, inc.replace("\\", os.sep)))
        out.append(os.path.relpath(target, REPO).replace("\\", "/"))
    return out


def closure(project_rel):
    """Transitive ProjectReference closure of a project, including itself."""
    seen, stack = set(), [project_rel]
    while stack:
        cur = stack.pop()
        if cur in seen:
            continue
        seen.add(cur)
        stack.extend(project_references(os.path.join(REPO, cur)))
    return seen


def closure_dirs(project_rel):
    """Directories the closure occupies, as `dir/` prefixes."""
    return {os.path.dirname(p) + "/" for p in closure(project_rel)}


def covered(directory, patterns):
    """Is `dir/` matched by any filter pattern? Directory-level, deliberately."""
    for pat in patterns:
        head = pat.split("**")[0].rstrip("/")
        if head and (directory.rstrip("/") == head or directory.startswith(head + "/")):
            return True
    return False


def is_test_suite(csproj_rel):
    """Test SDK (or IsTestProject) AND at least one test attribute — see the module docstring."""
    abs_path = os.path.join(REPO, csproj_rel)
    if not os.path.exists(abs_path):
        return False
    text = read(abs_path)
    if "Microsoft.NET.Test.Sdk" not in text and "<IsTestProject>" not in text:
        return False
    for dirpath, _dirs, files in os.walk(os.path.dirname(abs_path)):
        if f"{os.sep}obj{os.sep}" in dirpath + os.sep or f"{os.sep}bin{os.sep}" in dirpath + os.sep:
            continue
        for fn in files:
            if fn.endswith(".cs") and TEST_ATTR.search(read(os.path.join(dirpath, fn))):
                return True
    return False


def issue_is_open(number):
    """True/False, or None when the probe could not run (no token, no network)."""
    try:
        r = subprocess.run(["gh", "issue", "view", str(number), "--json", "state", "-q", ".state"],
                           capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        return None
    if r.returncode != 0:
        return None
    return r.stdout.strip().upper() == "OPEN"


def main():
    ap = argparse.ArgumentParser(description="Verify the merge gate's path filters cover their suites' build closures.")
    ap.add_argument("--quiet", action="store_true", help="print only violations")
    ap.add_argument("--no-github", action="store_true", help="skip the open-issue probe for allow-list entries")
    args = ap.parse_args()

    for required in (WORKFLOW, SOLUTION, ALLOWLIST, JOBS_FILE):
        if not os.path.exists(os.path.join(REPO, required)):
            fail(f"{required} not found — run from the repository, or the layout moved")
            return 2

    try:
        JOBS = json.loads(read(os.path.join(REPO, JOBS_FILE)))["jobs"]
    except (ValueError, KeyError) as exc:
        fail(f"{JOBS_FILE}: could not read the job table — {exc}")
        return 2

    workflow_text = read(os.path.join(REPO, WORKFLOW))
    try:
        filters, filters_line = parse_filters(workflow_text)
    except ValueError as exc:
        fail(f"{WORKFLOW}: could not parse the filters block — {exc}")
        return 2

    violations = []
    jobs_defined = workflow_jobs(workflow_text)

    # 1. The job table must still describe reality.
    for job, spec in JOBS.items():
        if jobs_defined and job not in jobs_defined:
            violations.append(f"{WORKFLOW}: the job table names '{job}', which the workflow does not define — "
                              f"its suites would count as gated by a job that cannot run")
        for name in spec["filters"]:
            if name not in filters:
                violations.append(f"{WORKFLOW}:{filters_line}: job '{job}' is gated on filter '{name}', "
                                  f"which the filters block does not define")
        for proj in spec["projects"]:
            if not os.path.exists(os.path.join(REPO, proj)):
                violations.append(f"{ALLOWLIST}: job '{job}' names {proj}, which does not exist")

    # 2. Every gated suite's build closure must be covered by the filters that enable its job.
    #    Jobs sharing a filter set and a suite are one fact, not two: the aws-gate variants are the same suites on
    #    two runners, and reporting each directory twice buried the nine real ones in eighteen lines.
    groups = {}
    for job, spec in JOBS.items():
        for proj in spec["projects"]:
            groups.setdefault((tuple(spec["filters"]), proj), []).append(job)

    for (filter_names, proj), jobs in sorted(groups.items()):
        if not os.path.exists(os.path.join(REPO, proj)):
            continue
        patterns = [p for name in filter_names for p in filters.get(name, [])]
        for directory in sorted(closure_dirs(proj)):
            if not os.path.isdir(os.path.join(REPO, directory)):
                continue  # a planned path is not a red gate
            if not covered(directory, patterns):
                violations.append(
                    f"{WORKFLOW}:{filters_line}: '{directory}' is in the build closure of {proj} "
                    f"({'jobs' if len(jobs) > 1 else 'job'} {', '.join(sorted(jobs))}) but no filter in "
                    f"{list(filter_names)} matches it — a change there would run no tests")

    # 3. Every real test suite runs in a job, or is allow-listed with an open issue.
    gated = {p for spec in JOBS.values() for p in spec["projects"]}
    allow = json.loads(read(os.path.join(REPO, ALLOWLIST)))
    allowed = {e["project"]: e for e in allow.get("ungated", [])}

    discovered = set(solution_projects(os.path.join(REPO, SOLUTION)))
    for dirpath, dirs, files in os.walk(REPO):
        dirs[:] = [d for d in dirs if d not in {".git", "bin", "obj", "node_modules", "claude"}]
        for fn in files:
            if fn.endswith(".csproj"):
                discovered.add(os.path.relpath(os.path.join(dirpath, fn), REPO).replace("\\", "/"))

    for proj in sorted(discovered):
        if proj in gated or not is_test_suite(proj):
            continue
        entry = allowed.get(proj)
        if entry is None:
            violations.append(f"{proj}: a test suite that no gate job runs, and no {ALLOWLIST} entry — "
                              f"gate it or allow-list it with a reason and an open issue")
            continue
        if not entry.get("reason") or not entry.get("issue"):
            violations.append(f"{ALLOWLIST}: the entry for {proj} needs both a reason and an issue")

    # 4. Allow-list hygiene: entries expire with their issue, and must name a real project.
    for proj, entry in sorted(allowed.items()):
        if proj not in discovered:
            violations.append(f"{ALLOWLIST}: entry for {proj}, which does not exist")
            continue
        if proj in gated:
            violations.append(f"{ALLOWLIST}: {proj} is allow-listed but job coverage now exists — drop the entry")
            continue
        if args.no_github:
            continue
        state = issue_is_open(entry["issue"])
        if state is False:
            violations.append(f"{ALLOWLIST}: {proj} is allow-listed against #{entry['issue']}, which is CLOSED — "
                              f"gate the suite or re-open the reason")

    if violations:
        for v in violations:
            fail(v)
        print(f"\ncheck-gate-filters: {len(violations)} violation(s)")
        return 1

    if not args.quiet:
        gated_dirs = sum(len(closure_dirs(p)) for spec in JOBS.values() for p in spec["projects"]
                         if os.path.exists(os.path.join(REPO, p)))
        print(f"check-gate-filters: {len(JOBS)} jobs, {len(gated)} gated suites, {gated_dirs} closure directories, "
              f"{len(allowed)} allow-listed -> PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
