"""Self-tests for scripts/axis-mutation-probe.py (#704 AC10).

The probe is the only thing standing behind #704's headline claim — "a deliberately planted axis-specific bug is caught
by a cell nobody hand-wrote". A probe that reported success without checking both halves of that would be exactly the
kind of verifier-that-cannot-fail #703 spent a whole issue removing, so its verdict rules are tested here rather than
trusted.

Nothing here builds the engine: `evaluate` is a pure function of the observed outcomes.
"""

import importlib.util
import pathlib
import sys
import tempfile
import textwrap
import unittest

SCRIPTS = pathlib.Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location("axis_mutation_probe", SCRIPTS / "axis-mutation-probe.py")
probe = importlib.util.module_from_spec(_spec)
sys.modules["axis_mutation_probe"] = probe
_spec.loader.exec_module(probe)


def mutation(**kw):
    base = dict(
        name="m", rationale="r", path="src/X.cs", find="a", replace="b",
        filter="f", must_fail=["MatrixTests"], must_pass=["HandWrittenTests"],
    )
    base.update(kw)
    return probe.Mutation(**base)


class EvaluateTests(unittest.TestCase):
    def test_passes_when_the_array_fails_and_the_hand_written_control_passes(self):
        ok, lines = probe.evaluate(mutation(), {
            "N.MatrixTests.Case_A": "Failed",
            "N.MatrixTests.Case_B": "Passed",
            "N.HandWrittenTests.One": "Passed",
        })
        self.assertTrue(ok)
        self.assertTrue(any("caught by 1/2" in l for l in lines))

    def test_fails_when_the_array_did_not_catch_the_defect(self):
        # The mutation is undetectable by the parameterised cells — the array proved nothing.
        ok, lines = probe.evaluate(mutation(), {
            "N.MatrixTests.Case_A": "Passed",
            "N.HandWrittenTests.One": "Passed",
        })
        self.assertFalse(ok)
        self.assertTrue(any("did NOT catch" in l for l in lines))

    def test_fails_when_the_hand_written_control_also_caught_it(self):
        # This is the check that makes the probe evidence rather than a tautology: if the old fixtures catch the
        # mutation too, it says nothing about whether parameterising the axis bought anything.
        ok, lines = probe.evaluate(mutation(), {
            "N.MatrixTests.Case_A": "Failed",
            "N.HandWrittenTests.One": "Failed",
        })
        self.assertFalse(ok)
        self.assertTrue(any("ALSO caught" in l for l in lines))

    def test_fails_when_no_matrix_test_ran(self):
        ok, lines = probe.evaluate(mutation(), {"N.HandWrittenTests.One": "Passed"})
        self.assertFalse(ok)
        self.assertTrue(any("ran at all" in l for l in lines))

    def test_fails_when_no_hand_written_control_ran(self):
        # Without a control, a failing matrix cell only shows the defect is detectable at all.
        ok, lines = probe.evaluate(mutation(), {"N.MatrixTests.Case_A": "Failed"})
        self.assertFalse(ok)
        self.assertTrue(any("no hand-written control" in l for l in lines))

    def test_fails_on_an_empty_result_set(self):
        # A filter that selects nothing must not read as success — the same false green #703's shard check removed.
        ok, lines = probe.evaluate(mutation(), {})
        self.assertFalse(ok)
        self.assertTrue(any("selected nothing" in l for l in lines))


class TrxParsingTests(unittest.TestCase):
    TRX = textwrap.dedent("""\
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions>
            <UnitTest id="11111111-1111-1111-1111-111111111111" name="Case_A">
              <TestMethod className="N.MatrixTests" name="Case_A" />
            </UnitTest>
            <UnitTest id="22222222-2222-2222-2222-222222222222" name="One">
              <TestMethod className="N.HandWrittenTests" name="One" />
            </UnitTest>
          </TestDefinitions>
          <Results>
            <UnitTestResult testId="11111111-1111-1111-1111-111111111111" testName="Case_A" outcome="Failed" />
            <UnitTestResult testId="22222222-2222-2222-2222-222222222222" testName="One" outcome="Passed" />
          </Results>
        </TestRun>
    """)

    def test_maps_fully_qualified_names_to_outcomes(self):
        with tempfile.TemporaryDirectory() as d:
            (pathlib.Path(d) / "probe.trx").write_text(self.TRX, encoding="utf-8")
            outcomes = probe.parse_outcomes(pathlib.Path(d))

        self.assertEqual(outcomes, {"N.MatrixTests.Case_A": "Failed", "N.HandWrittenTests.One": "Passed"})

    def test_no_trx_yields_no_outcomes(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(probe.parse_outcomes(pathlib.Path(d)), {})


class AnchorTests(unittest.TestCase):
    def test_every_shipped_mutation_anchor_occurs_exactly_once(self):
        # A refactor that moves the anchored line makes the mutation stale, and a stale mutation silently probes
        # nothing. Catching it here means the drift is a unit-test failure rather than a nightly that quietly stops
        # proving anything.
        for m in probe.MUTATIONS:
            src = (probe.REPO / m.path).read_text(encoding="utf-8")
            self.assertEqual(src.count(m.find), 1,
                             f"mutation '{m.name}' anchor must occur exactly once in {m.path}")

    def test_replacement_actually_changes_the_source(self):
        for m in probe.MUTATIONS:
            self.assertNotEqual(m.find, m.replace, f"mutation '{m.name}' does not change anything")

    def test_every_mutation_declares_both_halves_of_the_evidence(self):
        for m in probe.MUTATIONS:
            self.assertTrue(m.must_fail, f"mutation '{m.name}' has no must_fail — nothing would prove the array caught it")
            self.assertTrue(m.must_pass, f"mutation '{m.name}' has no must_pass — nothing would prove the hand-written "
                                         "tests missed it, which is the whole point")

    def test_mutation_names_are_unique(self):
        names = [m.name for m in probe.MUTATIONS]
        self.assertEqual(len(names), len(set(names)), "--only selects by name, so duplicates would be ambiguous")


if __name__ == "__main__":
    unittest.main()
