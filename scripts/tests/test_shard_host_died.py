#!/usr/bin/env python3
"""
Self-tests for `bench/aws/shard.py`'s verdict on a test host that dies part-way (#1391).

WHY: the verdict was computed from the trx alone and `rc` was printed and ignored. A host killed by a native crash leaves a partial trx with no failure in
it, so ~171 tests never ran and the gate reported PASS; the merge gate runs the same script, so a native crash in a test could merge.

These tests fake `run_one` with what a run leaves on disk, a log and a trx, because a crash is the one outcome no test project can be asked for.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import importlib.util
import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SHARD_PY = os.path.join(REPO, "bench", "aws", "shard.py")

spec = importlib.util.spec_from_file_location("shard_host_died", SHARD_PY)
shard = importlib.util.module_from_spec(spec)
sys.modules["shard_host_died"] = shard
spec.loader.exec_module(shard)

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
CRASH = "The active test run was aborted. Reason: Test host process crashed : Fatal error. System.AccessViolationException"
TRX = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="{ns}">
  <TestDefinitions><UnitTest id="1" name="T"><TestMethod className="N.C" name="T"/></UnitTest></TestDefinitions>
  <Results><UnitTestResult testId="1" testName="T" outcome="Passed"/></Results>
  <ResultSummary><Counters total="1" executed="1" passed="1" failed="0"/></ResultSummary>
</TestRun>
"""


class HostDiedTests(unittest.TestCase):
    def test_a_log_naming_the_crash_is_a_dead_host(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "shard0.log"), "w", encoding="utf-8") as fh:
                fh.write(CRASH)
            self.assertTrue(shard.host_died(d, "0"))

    def test_a_failing_test_is_not_a_dead_host(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "shard0.log"), "w", encoding="utf-8") as fh:
                fh.write("Failed!  - Failed:     1, Passed:   999")
            self.assertFalse(shard.host_died(d, "0"))

    def test_a_pass_that_never_ran_is_not_a_dead_host(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertFalse(shard.host_died(d, "0"))


class VerdictTests(unittest.TestCase):
    """`cmd_run` over one shard whose runs are scripted: each entry says whether that run's host dies."""

    def run_gate(self, deaths, sensitive=False, silent_exit=False):
        runs = iter(deaths)

        def fake_run_one(label, flt, results_dir, project=None, env_extra=None):
            died = next(runs, False)
            with open(os.path.join(results_dir, f"shard{label}.log"), "w", encoding="utf-8") as fh:
                fh.write(CRASH if died else "Passed!")
            trx = os.path.join(results_dir, f"shard{label}.trx")
            with open(trx, "w", encoding="utf-8") as fh:
                fh.write(TRX.format(ns=NS))
            return label, 1 if died or silent_exit else 0, 0.0, trx

        saved = (shard.SHARDS_JSON, shard.run_one, shard.plan_problems, shard.project_names_category, shard.shard_integrity)
        with tempfile.TemporaryDirectory() as d:
            plan = os.path.join(d, "shards.json")
            with open(plan, "w", encoding="utf-8") as fh:
                json.dump([{"filter": "x", "classes": ["N.C"]}], fh)
            shard.SHARDS_JSON = plan
            shard.run_one = fake_run_one
            shard.plan_problems = lambda _: []
            shard.project_names_category = lambda category: sensitive and category == "Sensitive"
            shard.shard_integrity = lambda _, __: []
            try:
                verdict = shard.cmd_run(os.path.join(d, "results"))
                return verdict, os.path.exists(os.path.join(d, "results", "shard0.crash.log"))
            finally:
                shard.SHARDS_JSON, shard.run_one, shard.plan_problems, shard.project_names_category, shard.shard_integrity = saved

    def test_a_clean_run_passes(self):
        self.assertEqual(self.run_gate([False]), (0, False))

    def test_a_host_that_dies_twice_fails_the_gate_though_no_test_failed(self):
        self.assertEqual(self.run_gate([True, True]), (1, True))

    def test_a_host_that_dies_once_is_rerun_and_keeps_its_log(self):
        self.assertEqual(self.run_gate([True, False]), (0, True))

    def test_a_pass_with_no_retry_of_its_own_is_rerun_and_fails_the_gate_dying_twice(self):
        self.assertEqual(self.run_gate([False, True, True], sensitive=True)[0], 1)
        self.assertEqual(self.run_gate([False, True, False], sensitive=True)[0], 0)

    def test_a_non_zero_exit_with_no_failure_recorded_fails_the_gate(self):
        self.assertEqual(self.run_gate([False], silent_exit=True)[0], 1)


class SkippedPassTests(unittest.TestCase):
    def test_a_skipped_pass_leaves_no_old_log_behind(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "shardS.log"), "w", encoding="utf-8") as fh:
                fh.write(CRASH)
            shard.skipped_trx(d, "S")
            self.assertFalse(shard.host_died(d, "S"))


class RetryCommandTests(unittest.TestCase):
    def test_a_host_that_died_is_not_retried_into_a_pass(self):
        with tempfile.TemporaryDirectory() as d:
            log = os.path.join(d, "workbench-dotnet.log")
            with open(log, "w", encoding="utf-8") as fh:
                fh.write(CRASH)
            self.assertEqual(shard.cmd_retry(os.path.join(d, "none.trx"), "p", d, "W", log), 1)


if __name__ == "__main__":
    unittest.main()
