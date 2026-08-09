"""Self-tests for scripts/linearizability-probe.py (#705 AC13).

The probe is the only thing standing behind T5's claim that the model can detect anything at all. A probe that could
report success without checking BOTH halves — green on the unmutated tree, red on the racy one — would be a verifier
that cannot fail, which is the defect class this whole epic exists to remove.

Nothing here builds the engine or runs a test: the pieces under test are pure functions of file text and trx contents.
"""

import importlib.util
import pathlib
import sys
import tempfile
import textwrap
import unittest

SCRIPTS = pathlib.Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location("linearizability_probe", SCRIPTS / "linearizability-probe.py")
probe = importlib.util.module_from_spec(_spec)
sys.modules["linearizability_probe"] = probe
_spec.loader.exec_module(probe)


TRX_TEMPLATE = textwrap.dedent(
    """\
    <?xml version="1.0" encoding="UTF-8"?>
    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <Results>
    {results}
      </Results>
      <TestDefinitions>
    {definitions}
      </TestDefinitions>
    </TestRun>
    """
)


def write_trx(directory: pathlib.Path, cases):
    """cases: [(class_name, test_name, outcome)] → one trx file in `directory`."""
    definitions, results = [], []
    for i, (cls, name, outcome) in enumerate(cases):
        tid = f"0000000{i}-0000-0000-0000-000000000000"
        definitions.append(
            f'    <UnitTest id="{tid}" name="{name}"><TestMethod className="{cls}" name="{name}" /></UnitTest>'
        )
        results.append(f'    <UnitTestResult testId="{tid}" testName="{name}" outcome="{outcome}" />')
    (directory / "run.trx").write_text(
        TRX_TEMPLATE.format(results="\n".join(results), definitions="\n".join(definitions)), encoding="utf-8"
    )


class ParseOutcomesTests(unittest.TestCase):
    def test_reads_class_qualified_names_and_outcomes(self):
        with tempfile.TemporaryDirectory() as d:
            p = pathlib.Path(d)
            write_trx(p, [("Typhon.Engine.Tests.LinearizabilityTests", "ParallelOperations_AreLinearizable", "Failed")])
            got = probe.parse_outcomes(p)
        self.assertEqual(
            got, {"Typhon.Engine.Tests.LinearizabilityTests.ParallelOperations_AreLinearizable": "Failed"}
        )

    def test_empty_directory_yields_nothing_rather_than_throwing(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(probe.parse_outcomes(pathlib.Path(d)), {})


class ApplyMutationTests(unittest.TestCase):
    """A stale anchor must abort loudly. A mutation that silently fails to apply probes nothing while reporting a
    clean run — the exact false-green shape #703 removed from the suppression taxonomy."""

    def _mutation(self, tmp: pathlib.Path, text: str, find: str, replace: str):
        rel = "fake.cs"
        (tmp / rel).write_text(text, encoding="utf-8")
        original_repo = probe.REPO
        probe.REPO = tmp
        m = probe.RaceMutation(name="t", rationale="r", path=rel, find=find, replace=replace)
        return m, original_repo

    def test_applies_and_returns_the_original_text(self):
        with tempfile.TemporaryDirectory() as d:
            tmp = pathlib.Path(d)
            m, restore = self._mutation(tmp, "alpha BETA gamma", "BETA", "DELTA")
            try:
                original = probe.apply_mutation(m)
                self.assertEqual(original, "alpha BETA gamma")
                self.assertEqual((tmp / "fake.cs").read_text(encoding="utf-8"), "alpha DELTA gamma")
            finally:
                probe.REPO = restore

    def test_missing_anchor_aborts(self):
        with tempfile.TemporaryDirectory() as d:
            tmp = pathlib.Path(d)
            m, restore = self._mutation(tmp, "alpha gamma", "BETA", "DELTA")
            try:
                with self.assertRaises(SystemExit) as ctx:
                    probe.apply_mutation(m)
                self.assertIn("STALE", str(ctx.exception))
            finally:
                probe.REPO = restore

    def test_ambiguous_anchor_aborts(self):
        """Two matches means the mutation would change more than it claims — also stale, also loud."""
        with tempfile.TemporaryDirectory() as d:
            tmp = pathlib.Path(d)
            m, restore = self._mutation(tmp, "BETA and BETA", "BETA", "DELTA")
            try:
                with self.assertRaises(SystemExit) as ctx:
                    probe.apply_mutation(m)
                self.assertIn("occurs 2 times", str(ctx.exception))
            finally:
                probe.REPO = restore


class ExitStatusTests(unittest.TestCase):
    """The three-way verdict is the design: a red baseline is neither a pass nor a probe failure, and collapsing it
    into either would misreport the state of the world while #400 is open."""

    def test_three_distinct_statuses(self):
        self.assertEqual(probe.EXIT_OK, 0)
        self.assertEqual(probe.EXIT_FAIL, 1)
        self.assertEqual(probe.EXIT_BASELINE_RED, 2)
        self.assertEqual(len({probe.EXIT_OK, probe.EXIT_FAIL, probe.EXIT_BASELINE_RED}), 3)


class MutationInventoryTests(unittest.TestCase):
    def test_every_mutation_is_described_and_targets_a_real_file(self):
        self.assertGreater(len(probe.MUTATIONS), 0, "a probe with no mutations checks nothing")
        for m in probe.MUTATIONS:
            self.assertTrue(m.name, "a mutation needs a name to be reported")
            self.assertGreater(len(m.rationale), 80, f"{m.name}: the rationale must say what the mutation proves")
            self.assertTrue((probe.REPO / m.path).exists(), f"{m.name}: targets {m.path}, which does not exist")
            self.assertGreaterEqual(m.seeds, 1, f"{m.name}: must get at least one seeded attempt")

    def test_anchors_are_present_exactly_once_in_their_target(self):
        """The anchors are checked here as well as at runtime, so a refactor breaks the fast unit test rather than a
        20-minute nightly job."""
        for m in probe.MUTATIONS:
            text = (probe.REPO / m.path).read_text(encoding="utf-8")
            self.assertEqual(text.count(m.find), 1, f"{m.name}: anchor must occur exactly once in {m.path}")

    def test_model_filter_names_the_model_test(self):
        self.assertIn(probe.MODEL_TEST, probe.MODEL_FILTER)


if __name__ == "__main__":
    unittest.main()
