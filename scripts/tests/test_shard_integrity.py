#!/usr/bin/env python3
"""
Self-tests for `bench/aws/shard.py`'s plan-integrity check (#703 AC4).

WHY: `cmd_plan` already refuses to emit a plan whose classes do not partition the suite. Nothing checked the other
end — that the plan, once RUN, executed what it named. It did not: `shards.json` named `ChaosStressTests` and
`Runtime.CheckerboardTests`, both class-level `[Ignore]`d, so two shards budgeted a slot, ran zero tests, and the
gate reported green.

The check can only be driven off RESULTS (a filter cannot reveal it — `[Ignore]` is unconditional in NUnit), so
these tests synthesise trx documents rather than running dotnet.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import importlib.util
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SHARD_PY = os.path.join(REPO, "bench", "aws", "shard.py")

spec = importlib.util.spec_from_file_location("shard", SHARD_PY)
shard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shard)

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

TRX_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="{ns}">
  <TestDefinitions>
{defs}
  </TestDefinitions>
  <Results>
{results}
  </Results>
</TestRun>
"""


def write_trx(path, tests):
    """tests: [(className, testName, outcome)] -> a trx `all_results` can parse."""
    defs, results = [], []
    for i, (cls, name, outcome) in enumerate(tests):
        tid = f"id{i}"
        defs.append(f'    <UnitTest id="{tid}" name="{name}">'
                    f'<TestMethod className="{cls}" name="{name}" /></UnitTest>')
        results.append(f'    <UnitTestResult testId="{tid}" outcome="{outcome}" />')
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(TRX_TEMPLATE.format(ns=NS, defs="\n".join(defs), results="\n".join(results)))
    return path


class TestShardIntegrity(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def trx(self, name, tests):
        return write_trx(os.path.join(self.tmp.name, name), tests)

    def test_named_class_that_ran_nothing_is_reported(self):
        """The live defect: a shard names a fixture, the fixture is [Ignore]d, the shard reports green on nothing."""
        shards = [{"classes": ["Typhon.Engine.Tests.ChaosStressTests", "Typhon.Engine.Tests.TransactionTests"]}]
        trx = self.trx("shard1.trx", [("Typhon.Engine.Tests.TransactionTests", "Commit", "Passed")])
        self.assertEqual(shard.shard_integrity(shards, [trx]), ["Typhon.Engine.Tests.ChaosStressTests"])

    def test_all_named_classes_executed_is_healthy(self):
        shards = [{"classes": ["Typhon.Engine.Tests.TransactionTests"]}]
        trx = self.trx("shard1.trx", [("Typhon.Engine.Tests.TransactionTests", "Commit", "Passed")])
        self.assertEqual(shard.shard_integrity(shards, [trx]), [])

    def test_a_failing_test_still_counts_as_executed(self):
        """Integrity asks whether the plan RAN the class, not whether it passed — that is the retry pass's job."""
        shards = [{"classes": ["Typhon.Engine.Tests.TransactionTests"]}]
        trx = self.trx("shard1.trx", [("Typhon.Engine.Tests.TransactionTests", "Commit", "Failed")])
        self.assertEqual(shard.shard_integrity(shards, [trx]), [])

    def test_catchall_placeholder_is_not_a_class(self):
        shards = [{"classes": ["<catch-all>"]}]
        self.assertEqual(shard.shard_integrity(shards, [self.trx("s.trx", [])]), [])

    def test_plan_entry_may_be_a_namespace_suffix_of_the_trx_name(self):
        """shards.json carries `Runtime.CheckerboardTests`; the trx carries the fully-qualified name."""
        shards = [{"classes": ["Runtime.CheckerboardTests"]}]
        trx = self.trx("s.trx", [("Typhon.Engine.Tests.Runtime.CheckerboardTests", "Tick", "Passed")])
        self.assertEqual(shard.shard_integrity(shards, [trx]), [])

    def test_suffix_match_does_not_accept_a_different_class(self):
        """`~Foo.` must never be satisfied by `FooBar.` — the same prefix hazard the plan's trailing '.' guards."""
        shards = [{"classes": ["CheckerboardTests"]}]
        trx = self.trx("s.trx", [("Typhon.Engine.Tests.ExtendedCheckerboardTests", "Tick", "Passed")])
        self.assertEqual(shard.shard_integrity(shards, [trx]), ["CheckerboardTests"])

    def test_execution_is_pooled_across_every_shard_trx(self):
        """A class may be named by one shard and executed under another's trx — that is not an integrity failure."""
        shards = [{"classes": ["A.Alpha"]}, {"classes": ["A.Beta"]}]
        t1 = self.trx("s1.trx", [("A.Alpha", "T", "Passed")])
        t2 = self.trx("s2.trx", [("A.Beta", "T", "Passed")])
        self.assertEqual(shard.shard_integrity(shards, [t1, t2]), [])

    def test_missing_trx_does_not_crash(self):
        """A shard whose process died writes no trx; integrity must still report, not raise."""
        shards = [{"classes": ["A.Alpha"]}]
        self.assertEqual(shard.shard_integrity(shards, [os.path.join(self.tmp.name, "absent.trx")]), ["A.Alpha"])

    def test_duplicates_are_reported_once(self):
        shards = [{"classes": ["A.Alpha"]}, {"classes": ["A.Alpha"]}]
        self.assertEqual(shard.shard_integrity(shards, [self.trx("s.trx", [])]), ["A.Alpha"])


class TestGateFilters(unittest.TestCase):
    """The gate must exclude the tiered categories BY CATEGORY, not by relying on NUnit's [Explicit] judgement."""

    def test_every_gate_filter_excludes_the_tier_categories(self):
        for flt in (shard.positive_filter(["A.Alpha"]),
                    shard.catchall_filter(["A.Beta"]),
                    shard.SENSITIVE_FILTER):
            for cat in shard.GATE_EXCLUDED:
                self.assertIn(f"(Category!={cat})", flt, f"{cat} not excluded from: {flt}")

    def test_the_parallel_shards_also_exclude_sensitive(self):
        self.assertIn("(Category!=Sensitive)", shard.positive_filter(["A.Alpha"]))
        self.assertIn("(Category!=Sensitive)", shard.catchall_filter([]))

    def test_the_quiet_pass_selects_sensitive(self):
        self.assertIn("(Category=Sensitive)", shard.SENSITIVE_FILTER)

    def test_class_names_keep_the_disambiguating_dot(self):
        """`~Foo.` never matches `FooBar.` — dropping the dot silently widens every shard."""
        self.assertIn("FullyQualifiedName~A.Alpha.", shard.positive_filter(["A.Alpha"]))
        self.assertIn("FullyQualifiedName!~A.Beta.", shard.catchall_filter(["A.Beta"]))


if __name__ == "__main__":
    unittest.main()
