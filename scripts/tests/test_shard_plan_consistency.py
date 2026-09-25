#!/usr/bin/env python3
"""
Self-tests for `bench/aws/shard.py`'s plan-consistency check (#1046), and the committed plan itself.

WHY: a shard's `filter` is what runs and its `classes` list is what the plan says it runs; shard 0's filter is the negative
complement of every other shard's list. `shards.json` is edited by hand, and one edit (#1004) listed six classes in a shard
without excluding them from shard 0: they ran twice, concurrently, and collided on one temp database. `shard_integrity`
cannot see it (every named class did execute), so the plan is checked on its own, before anything runs — and here, so the
invariants job refuses an inconsistent plan in seconds instead of the gate flaking on it.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import importlib.util
import json
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SHARD_PY = os.path.join(REPO, "bench", "aws", "shard.py")

spec = importlib.util.spec_from_file_location("shard", SHARD_PY)
shard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shard)


def plan(*bins):
    """A consistent plan: shard 0 the catch-all of every other bin, each bin its positive filter."""
    elsewhere = [c for b in bins for c in b]
    return ([{"filter": shard.catchall_filter(elsewhere), "classes": ["<catch-all>"]}]
            + [{"filter": shard.positive_filter(b), "classes": list(b)} for b in bins])


class PlanConsistencyTests(unittest.TestCase):
    def test_the_committed_plan_is_consistent(self):
        with open(shard.SHARDS_JSON, encoding="utf-8") as fh:
            self.assertEqual(shard.plan_problems(json.load(fh)), [])

    def test_a_generated_plan_is_consistent(self):
        self.assertEqual(shard.plan_problems(plan(["N.A", "N.B"], ["N.C"])), [])

    def test_a_class_listed_but_not_excluded_by_the_catch_all_runs_twice(self):
        # #1004's edit: added to shard 1's list and filter, shard 0 left as it was.
        p = plan(["N.A"], ["N.C"])
        p[1]["classes"].append("N.B")
        p[1]["filter"] = shard.positive_filter(p[1]["classes"])
        problems = shard.plan_problems(p)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("shard 0 does not exclude N.B", problems[0])
        self.assertIn("runs twice", problems[0])

    def test_a_class_excluded_by_the_catch_all_but_listed_nowhere_runs_nowhere(self):
        p = plan(["N.A"], ["N.C"])
        p[2]["classes"].remove("N.C")
        p[2]["filter"] = shard.positive_filter(["N.D"])
        p[2]["classes"] = ["N.D"]
        problems = shard.plan_problems(p)
        self.assertTrue(any("excludes N.C" in x and "runs nowhere" in x for x in problems), problems)

    def test_a_class_in_two_shards_is_refused(self):
        p = plan(["N.A"], ["N.A", "N.C"])
        self.assertTrue(any("N.A is listed in shard 1 and shard 2" in x for x in shard.plan_problems(p)))

    def test_a_filter_that_disagrees_with_its_list_is_refused(self):
        p = plan(["N.A", "N.B"], ["N.C"])
        p[1]["filter"] = shard.positive_filter(["N.A"])
        self.assertTrue(any("names N.B, which its filter does not run" in x for x in shard.plan_problems(p)))
        p[1]["filter"] = shard.positive_filter(["N.A", "N.B", "N.X"])
        self.assertTrue(any("runs N.X, which its classes list does not name" in x for x in shard.plan_problems(p)))

    def test_a_stale_category_exclusion_is_refused(self):
        p = plan(["N.A"])
        p[1]["filter"] = p[1]["filter"].replace("(Category!=Manual)&", "")
        self.assertTrue(any("category exclusion" in x for x in shard.plan_problems(p)))

    def test_sync_rebuilds_every_filter_from_the_lists(self):
        p = plan(["N.A"], ["N.C"])
        p[1]["classes"].append("N.B")                      # the hand edit, lists only
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "shards.json")
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(p, fh)
            saved, shard.SHARDS_JSON = shard.SHARDS_JSON, path
            try:
                self.assertEqual(shard.cmd_sync(), 0)
                with open(path, encoding="utf-8", newline="") as fh:
                    text = fh.read()
            finally:
                shard.SHARDS_JSON = saved
        synced = json.loads(text)
        self.assertEqual(shard.plan_problems(synced), [])
        self.assertEqual([s["classes"] for s in synced], [s["classes"] for s in p])
        self.assertNotIn("\r", text)                       # the committed format, on every OS
        self.assertTrue(text.endswith("\n"))

    def test_sync_refuses_lists_that_are_themselves_inconsistent(self):
        p = plan(["N.A"], ["N.A"])
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "shards.json")
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(p, fh)
            saved, shard.SHARDS_JSON = shard.SHARDS_JSON, path
            try:
                self.assertEqual(shard.cmd_sync(), 1)
            finally:
                shard.SHARDS_JSON = saved


if __name__ == "__main__":
    unittest.main()
