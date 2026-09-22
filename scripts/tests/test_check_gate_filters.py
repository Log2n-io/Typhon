"""Self-tests for `check-gate-filters.py`.

The load-bearing cases are `test_uncovered_closure_fails` and `test_ungated_suite_fails`: a checker that returned 0
unconditionally would be indistinguishable from a working one without them — the same argument #703 makes about a
lint that has never rejected anything.

`test_fixture_with_no_tests_is_not_a_suite` locks in a distinction the repository actually depends on:
`test/Typhon.Shell.Tests.SchemaFixture` carries `Microsoft.NET.Test.Sdk` and contains no tests. Classifying it as an
ungated suite would push someone to add a job that runs zero tests, which is the false green inverted.

`test_phantom_job_fails` guards the job table itself. It is declared rather than derived (see the checker's
docstring), so without this a table naming a job the workflow never defines would mark its suites as gated and
silence the ungated-suite check for them.

Run: python3 -m unittest discover -s scripts/tests
"""

import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

SCRIPTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHECKER = os.path.join(SCRIPTS, "check-gate-filters.py")

WORKFLOW = """\
name: merge-gate
on: [push]
jobs:
  changes:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - uses: dorny/paths-filter@v4
        id: filter
        with:
          filters: |
            engine:
{patterns}
  suite-job:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
"""

CSPROJ_TEST = """\
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.7.0" />
  </ItemGroup>
{refs}
</Project>
"""

CSPROJ_LIB = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n"


class GateFilters(unittest.TestCase):
    """The checker locates everything from its own path, so each fixture is a whole fake repository."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.tmp.name, "repo")
        for d in ("scripts", os.path.join(".github", "workflows"), os.path.join("test", "Suite"),
                  os.path.join("src", "Lib")):
            os.makedirs(os.path.join(self.root, d))

        with open(CHECKER, encoding="utf-8") as src, \
                open(os.path.join(self.root, "scripts", "check-gate-filters.py"), "w", encoding="utf-8") as dst:
            dst.write(src.read())

        self._lib("src/Lib/Lib.csproj")
        self._suite("test/Suite/Suite.csproj", refs=["../../src/Lib/Lib.csproj"], with_tests=True)
        self._solution(["test/Suite/Suite.csproj", "src/Lib/Lib.csproj"])
        self._jobs({"suite-job": {"filters": ["engine"], "projects": ["test/Suite/Suite.csproj"]}})
        self._allowlist([])
        # Covers the suite but NOT src/Lib — the hole this checker exists to find.
        self._workflow(["              - 'test/Suite/**'"])

    def tearDown(self):
        self.tmp.cleanup()

    # ---- fixture helpers -------------------------------------------------------------------------------------
    def _write(self, rel, text):
        path = os.path.join(self.root, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    def _lib(self, rel):
        self._write(rel, CSPROJ_LIB)

    def _suite(self, rel, refs=(), with_tests=True):
        block = ""
        if refs:
            items = "\n".join(f'    <ProjectReference Include="{r}" />' for r in refs)
            block = f"  <ItemGroup>\n{items}\n  </ItemGroup>"
        self._write(rel, CSPROJ_TEST.format(refs=block))
        if with_tests:
            self._write(os.path.join(os.path.dirname(rel), "Tests.cs"),
                        "public class T { [Test] public void A() { } }\n")

    def _solution(self, projects):
        rows = "\n".join(f'        <Project Path="{p}" />' for p in projects)
        self._write("Typhon.slnx", f"<Solution>\n    <Folder Name=\"/all/\">\n{rows}\n    </Folder>\n</Solution>\n")

    def _jobs(self, jobs):
        self._write("scripts/gate-jobs.json", json.dumps({"jobs": jobs}, indent=2))

    def _allowlist(self, entries):
        self._write("scripts/gate-filter-allowlist.json", json.dumps({"ungated": entries}, indent=2))

    def _workflow(self, pattern_lines):
        self._write(".github/workflows/merge-gate.yml",
                    WORKFLOW.format(patterns="\n".join(pattern_lines)))

    def _run(self):
        """Invoke as a subprocess — the exit code IS the contract the workflow depends on."""
        return subprocess.run(
            [sys.executable, os.path.join(self.root, "scripts", "check-gate-filters.py"), "--no-github"],
            capture_output=True, text=True)

    # ---- cases -----------------------------------------------------------------------------------------------
    def test_uncovered_closure_fails(self):
        """THE load-bearing case: a suite compiles against src/Lib, no filter matches it, so a change there runs nothing."""
        r = self._run()
        self.assertEqual(1, r.returncode, r.stdout + r.stderr)
        self.assertIn("src/Lib/", r.stdout)
        self.assertIn("build closure", r.stdout)

    def test_covered_closure_passes(self):
        """Guards the guard: if the fixture stopped resolving closures, the case above would pass vacuously."""
        self._workflow(["              - 'test/Suite/**'", "              - 'src/Lib/**'"])
        r = self._run()
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)

    def test_ungated_suite_fails(self):
        """A real suite that no job runs, and no allow-list entry."""
        self._workflow(["              - 'test/Suite/**'", "              - 'src/Lib/**'"])
        self._suite("test/Orphan/Orphan.csproj", with_tests=True)
        r = self._run()
        self.assertEqual(1, r.returncode, r.stdout + r.stderr)
        self.assertIn("Orphan", r.stdout)

    def test_allowlisted_suite_passes(self):
        """The escape hatch works — and requires a reason and an issue."""
        self._workflow(["              - 'test/Suite/**'", "              - 'src/Lib/**'"])
        self._suite("test/Orphan/Orphan.csproj", with_tests=True)
        self._allowlist([{"project": "test/Orphan/Orphan.csproj", "reason": "covered elsewhere", "issue": 1}])
        r = self._run()
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)

    def test_fixture_with_no_tests_is_not_a_suite(self):
        """The SchemaFixture case: the test SDK without a single test method is a fixture, not an ungated suite."""
        self._workflow(["              - 'test/Suite/**'", "              - 'src/Lib/**'"])
        self._suite("test/Fixture/Fixture.csproj", with_tests=False)
        r = self._run()
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)

    def test_phantom_job_fails(self):
        """A job table naming a job the workflow does not define would silently mark its suites as gated."""
        self._workflow(["              - 'test/Suite/**'", "              - 'src/Lib/**'"])
        self._jobs({"no-such-job": {"filters": ["engine"], "projects": ["test/Suite/Suite.csproj"]}})
        r = self._run()
        self.assertEqual(1, r.returncode, r.stdout + r.stderr)
        self.assertIn("no-such-job", r.stdout)

    def test_missing_filter_fails(self):
        """A job gated on a filter the block never defines can never run."""
        self._workflow(["              - 'test/Suite/**'", "              - 'src/Lib/**'"])
        self._jobs({"suite-job": {"filters": ["nope"], "projects": ["test/Suite/Suite.csproj"]}})
        r = self._run()
        self.assertEqual(1, r.returncode, r.stdout + r.stderr)
        self.assertIn("nope", r.stdout)


if __name__ == "__main__":
    unittest.main()
