#!/usr/bin/env python3
"""
Self-tests for how `bench/aws/shard.py` hands a shard its filter (#1453).

WHY: a plan grows with the suite and a command line does not, so the filter travels in a generated runsettings file. A file that
lost a setting of `workers1.runsettings` would run the shard wrongly with nothing to show for it: `AssemblySelectLimit` is what keeps
the NUnit adapter from dropping the filter and running every test (see that file's comment), so the generated file is checked for
it, and for a filter that survives XML intact at any length.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import importlib.util
import os
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SHARD_PY = os.path.join(REPO, "bench", "aws", "shard.py")

spec = importlib.util.spec_from_file_location("shard", SHARD_PY)
shard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shard)

# Every character the plan's filters are made of that XML or a shell would treat specially.
NASTY = "TestCategory!=Quarantine&(FullyQualifiedName~Foo.|FullyQualifiedName!~Bar.)&Name!=\"<x>\""


class ShardSettingsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = os.path.join(self.tmp.name, "results")   # not created: cmd_retry's dir may not exist either

    def written_filter(self, flt):
        root = ET.parse(shard.settings_for(flt, self.dir, "0")).getroot()
        return root, root.find("RunConfiguration/TestCaseFilter").text

    def test_the_settings_of_the_committed_file_survive(self):
        root, _ = self.written_filter("a=b")
        self.assertEqual(root.findtext("NUnit/NumberOfTestWorkers"), "1")
        self.assertEqual(root.findtext("NUnit/DefaultTimeout"), "10000")
        self.assertEqual(root.findtext("NUnit/AssemblySelectLimit"), "1000000")

    def test_a_filter_round_trips_exactly(self):
        self.assertEqual(self.written_filter(NASTY)[1], NASTY)

    def test_a_filter_no_command_line_could_carry_round_trips(self):
        flt = "&".join(f"FullyQualifiedName!~Some.Namespace.Class{i}." for i in range(3000))
        self.assertGreater(len(flt), 100_000)
        self.assertEqual(self.written_filter(flt)[1], flt)

    def test_the_file_sits_beside_the_shards_trx(self):
        path = shard.settings_for("a=b", self.dir, "7R1")
        self.assertEqual(os.path.dirname(path), os.path.abspath(self.dir))
        self.assertEqual(os.path.basename(path), "shard7R1.runsettings")

    def test_run_one_passes_the_file_and_no_filter(self):
        done = mock.Mock(returncode=0, stdout="", stderr="")
        with mock.patch.object(shard.subprocess, "run", return_value=done) as run:
            shard.run_one("3", NASTY, self.dir)
        cmd = run.call_args.args[0]
        self.assertNotIn("--filter", cmd)
        self.assertNotIn(NASTY, cmd)
        settings = cmd[cmd.index("--settings") + 1]
        self.assertEqual(ET.parse(settings).getroot().findtext("RunConfiguration/TestCaseFilter"), NASTY)


if __name__ == "__main__":
    unittest.main()
