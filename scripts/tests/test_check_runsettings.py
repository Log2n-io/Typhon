"""Self-tests for scripts/check-runsettings.py (#705).

The check exists because a malformed settings file makes `dotnet test` run ZERO tests while the job still looks
configured — so a check that could not reject a malformed file would be a false green guarding against a false
green. The first case below plants the exact defect that shipped: a double hyphen inside an XML comment.
"""

import importlib.util
import io
import os
import pathlib
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

SCRIPTS = pathlib.Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location("check_runsettings", SCRIPTS / "check-runsettings.py")
check = importlib.util.module_from_spec(_spec)
sys.modules["check_runsettings"] = check
_spec.loader.exec_module(check)

WELL_FORMED = """<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <!-- a perfectly ordinary comment -->
  <NUnit>
    <NumberOfTestWorkers>1</NumberOfTestWorkers>
  </NUnit>
</RunSettings>
"""

# The exact shape that shipped: a CLI flag spelled with its two leading dashes, inside a comment.
DOUBLE_HYPHEN_IN_COMMENT = """<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <!-- The real guard is vstest's `--blame-hang`, which kills the test host. -->
  <NUnit>
    <NumberOfTestWorkers>1</NumberOfTestWorkers>
  </NUnit>
</RunSettings>
"""


class CheckRunsettingsTests(unittest.TestCase):
    def run_check(self, root):
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = check.main(["--root", str(root)])
        return rc, buf.getvalue()

    def write(self, root, name, text):
        p = pathlib.Path(root) / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")
        return p

    def test_well_formed_passes(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.write(tmp, "a.runsettings", WELL_FORMED)
            rc, out = self.run_check(tmp)
            self.assertEqual(rc, 0, out)
            self.assertIn("all well-formed", out)

    def test_double_hyphen_in_a_comment_is_rejected(self):
        """The defect that shipped in bench/aws/nightly.runsettings and silently emptied two nightly tiers."""
        with tempfile.TemporaryDirectory() as tmp:
            self.write(tmp, "nightly.runsettings", DOUBLE_HYPHEN_IN_COMMENT)
            rc, out = self.run_check(tmp)
            self.assertEqual(rc, 1, f"a malformed settings file must fail the check; output was:\n{out}")
            self.assertIn("nightly.runsettings", out)
            self.assertIn("HINT", out, "the report must say what to do — the cause is not obvious from the parser error")

    def test_unclosed_tag_is_rejected(self):
        """Not just the comment case — any parse failure has the same consequence for dotnet test."""
        with tempfile.TemporaryDirectory() as tmp:
            self.write(tmp, "b.runsettings", "<RunSettings><NUnit></RunSettings>")
            rc, _ = self.run_check(tmp)
            self.assertEqual(rc, 1)

    def test_bin_and_obj_are_skipped(self):
        """Build output contains copies; a stale malformed copy under bin/ is not a source defect."""
        with tempfile.TemporaryDirectory() as tmp:
            self.write(tmp, "good.runsettings", WELL_FORMED)
            self.write(tmp, os.path.join("bin", "Debug", "stale.runsettings"), DOUBLE_HYPHEN_IN_COMMENT)
            self.write(tmp, os.path.join("obj", "stale.runsettings"), DOUBLE_HYPHEN_IN_COMMENT)
            rc, out = self.run_check(tmp)
            self.assertEqual(rc, 0, out)
            self.assertIn("1 file(s)", out, "only the source file should have been scanned")

    def test_finding_nothing_is_a_failure_not_a_pass(self):
        """A scan that matches no files is misconfigured; reporting 'clean' there is the false green itself."""
        with tempfile.TemporaryDirectory() as tmp:
            rc, out = self.run_check(tmp)
            self.assertEqual(rc, 1, out)
            self.assertIn("misconfigured", out)

    def test_the_repo_itself_is_clean(self):
        """Pins the real files, so a future edit that breaks one fails here rather than on the nightly runner."""
        repo = SCRIPTS.parent
        rc, out = self.run_check(repo)
        self.assertEqual(rc, 0, out)


if __name__ == "__main__":
    unittest.main()
