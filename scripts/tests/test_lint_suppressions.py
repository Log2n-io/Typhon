#!/usr/bin/env python3
"""
Self-tests for `scripts/lint-test-suppressions.py`.

WHY THIS FILE EXISTS (issue #703): a lint that has never rejected anything is exactly the false green it was
written to prevent. #703's acceptance clause is explicit — *"a PLANTED zero-test shard fails CI. Test the lints,
not just the tests."* So every check gets a fixture that violates it and must produce a non-zero exit, plus a
clean fixture that must not.

Stdlib `unittest` on purpose: the merge gate's `invariants` job runs on a bare ubuntu runner with no Python
dependencies installed, and adding pytest to buy `assert` rewriting would be a new install step for nothing.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.dirname(HERE)


def _load(module_name, filename):
    """Import a hyphenated script as a module (`lint-test-suppressions.py` is not a legal identifier)."""
    spec = importlib.util.spec_from_file_location(module_name, os.path.join(SCRIPTS, filename))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


lint = _load("lint_test_suppressions", "lint-test-suppressions.py")


CLEAN_FIXTURE = """\
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class GoodTests
{
    [Test]
    public void Runs() { }

    [Test]
    [Ignore("#999 - blocked until the subtree hash lands")]
    public void BlockedOnAnOpenIssue() { }

    // Excluded from the gate: known-red under Linux CI, see #406.
    [Test]
    [Category("Quarantine")]
    public void KnownRed() { }

    [Test]
    [Explicit("Long-running race harness")]
    [Category("Nightly")]
    public void CostlyButTiered() { }

    // Needs TYPHON__PROFILER__CONCURRENCY__ENABLED=true; CI cannot set it per-fixture.
    [Test]
    [Explicit("Needs an env var")]
    [Category("Manual")]
    public void ManualWithReason() { }
}
"""


class LintFixtureCase(unittest.TestCase):
    """Writes C# fixtures to a temp tree and runs the lint over them."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.tmp.name, "test")
        os.makedirs(self.root)
        self.shards = os.path.join(self.tmp.name, "shards.json")
        self.write_shards([])

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, text):
        path = os.path.join(self.root, name)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)
        return path

    def write_shards(self, classes):
        with open(self.shards, "w", encoding="utf-8") as fh:
            json.dump([{"filter": "x", "classes": classes}], fh)

    def run_lint(self):
        """(exit_code, stdout). --no-github keeps the self-tests offline and deterministic."""
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = lint.main(["--root", self.root, "--shards", self.shards, "--no-github"])
        return rc, buf.getvalue()

    def assert_flags(self, check):
        rc, out = self.run_lint()
        self.assertEqual(rc, 1, f"expected {check} to fail the lint; output was:\n{out}")
        self.assertIn(check, out)
        return out

    def assert_clean(self):
        rc, out = self.run_lint()
        self.assertEqual(rc, 0, f"expected a clean pass; output was:\n{out}")


class TestCleanTree(LintFixtureCase):
    def test_correctly_marked_suppressions_pass(self):
        self.write("GoodTests.cs", CLEAN_FIXTURE)
        self.assert_clean()

    def test_empty_tree_passes(self):
        self.assert_clean()


class TestIgnoreChecks(LintFixtureCase):
    def test_ignore_without_issue_is_rejected(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n    [Ignore("broken somehow")]\n'
                           '    public void T() { }\n}\n')
        self.assert_flags("IGNORE_NO_ISSUE")

    def test_ignore_meaning_slow_is_rejected(self):
        # The ChaosStressTests shape: labelled "too long" when it actually hung (#695).
        self.write("A.cs", '[TestFixture]\n[Ignore("Too long, should be manually executed when needed")]\n'
                           'class A\n{\n    [Test]\n    public void T() { }\n}\n')
        out = self.assert_flags("IGNORE_MEANS_COST")
        self.assertIn("Too long", out)

    def test_ignore_meaning_flaky_is_rejected(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n'
                           '    [Ignore("#42 Flaky under parallel load")]\n    public void T() { }\n}\n')
        self.assert_flags("IGNORE_MEANS_COST")

    def test_bare_ignore_is_rejected(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n    [Ignore]\n    public void T() { }\n}\n')
        self.assert_flags("IGNORE_NO_ISSUE")

    def test_closed_issue_is_rejected(self):
        """The #591 shape: the blocker shipped, the guard did not. States are injected — no network."""
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n'
                           '    [Ignore("#591 second shape - blocked on full-range key bounds")]\n'
                           '    public void T() { }\n}\n')
        parsed = lint.scan_tree(self.root)
        violations = lint.check_ignores(parsed, {591: "closed"})
        self.assertTrue(any(v.check == "IGNORE_CLOSED_ISSUE" for v in violations))

    def test_open_issue_is_accepted(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n'
                           '    [Ignore("#693 - cross-component WhereField is guarded, not implemented")]\n'
                           '    public void T() { }\n}\n')
        parsed = lint.scan_tree(self.root)
        violations = lint.check_ignores(parsed, {693: "open"})
        self.assertEqual([v.check for v in violations], [])

    def test_unresolvable_issue_is_not_guessed(self):
        """A number gh could not resolve is omitted from the state map; it must NOT be reported as closed."""
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n'
                           '    [Ignore("#12345 - not resolvable")]\n    public void T() { }\n}\n')
        parsed = lint.scan_tree(self.root)
        violations = lint.check_ignores(parsed, {})
        self.assertEqual([v.check for v in violations], [])


class TestExplicitChecks(LintFixtureCase):
    def test_explicit_without_tier_is_rejected(self):
        # The OlcBTreeStressTests shape: [Explicit] with no tier ran nowhere for four months.
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n    [Explicit("Stress test - run manually")]\n'
                           '    public void T() { }\n}\n')
        self.assert_flags("EXPLICIT_NO_TIER")

    def test_tier_inherited_from_the_fixture_is_accepted(self):
        self.write("A.cs", '[TestFixture]\n[Category("Nightly")]\nclass A\n{\n    [Test]\n'
                           '    [Explicit("Stress")]\n    public void T() { }\n}\n')
        self.assert_clean()

    def test_manual_tier_without_justification_is_rejected(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n    [Explicit("x")]\n'
                           '    [Category("Manual")]\n    public void T() { }\n}\n')
        self.assert_flags("MANUAL_NO_REASON")

    def test_manual_tier_with_justification_is_accepted(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n'
                           '    // Wall-clock latency histograms; a shared CI runner cannot hold the assertion.\n'
                           '    [Explicit("x")]\n    [Category("Manual")]\n    public void T() { }\n}\n')
        self.assert_clean()


class TestQuarantineChecks(LintFixtureCase):
    def test_quarantine_without_issue_is_rejected(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    [Test]\n    [Category("Quarantine")]\n'
                           '    public void T() { }\n}\n')
        self.assert_flags("QUARANTINE_NO_ISSUE")

    def test_quarantine_issue_in_the_leading_comment_is_accepted(self):
        self.write("A.cs", '[TestFixture]\nclass A\n{\n    // QUARANTINE (#406): Linux-only IndexOutOfRange.\n'
                           '    [Test]\n    [Category("Quarantine")]\n    public void T() { }\n}\n')
        self.assert_clean()

    def test_quarantine_issue_on_the_fixture_covers_its_members(self):
        self.write("A.cs", '// Whole fixture quarantined pending #500.\n[TestFixture]\n'
                           '[Category("Quarantine")]\nclass A\n{\n    [Test]\n'
                           '    [Category("Quarantine")]\n    public void T() { }\n}\n')
        self.assert_clean()


class TestShardIntegrity(LintFixtureCase):
    def test_planted_zero_test_shard_fails(self):
        """#703's named acceptance clause: a shard that names a fully-[Ignore]d class must fail."""
        self.write("A.cs", '[TestFixture]\n[Ignore("#42 whole fixture blocked")]\nclass ChaosStressTests\n'
                           '{\n    [Test]\n    public void T() { }\n}\n')
        self.write_shards(["Typhon.Engine.Tests.ChaosStressTests"])
        out = self.assert_flags("SHARD_ZERO_TESTS")
        self.assertIn("ChaosStressTests", out)

    def test_shard_naming_a_live_class_passes(self):
        self.write("A.cs", '[TestFixture]\nclass ChaosStressTests\n{\n    [Test]\n    public void T() { }\n}\n')
        self.write_shards(["Typhon.Engine.Tests.ChaosStressTests"])
        self.assert_clean()

    def test_ignored_class_not_named_by_a_shard_is_not_a_shard_violation(self):
        """An [Ignore]d fixture is a suppression question, not a shard-integrity one — don't conflate them."""
        self.write("A.cs", '[TestFixture]\n[Ignore("#42 blocked")]\nclass Lonely\n'
                           '{\n    [Test]\n    public void T() { }\n}\n')
        self.write_shards(["Typhon.Engine.Tests.SomethingElse"])
        rc, out = self.run_lint()
        self.assertEqual(rc, 0, out)

    def test_catchall_placeholder_is_ignored(self):
        """shard 0's `<catch-all>` is a placeholder, not a class name."""
        self.write("A.cs", CLEAN_FIXTURE)
        self.write_shards(["<catch-all>"])
        self.assert_clean()


class TestParser(unittest.TestCase):
    def test_attribute_block_binds_to_the_following_declaration(self):
        groups = lint.parse_file("x.cs", '[TestFixture]\n[Category("Nightly")]\nclass A\n{\n}\n')
        self.assertEqual(len(groups), 1)
        self.assertTrue(groups[0].is_class)
        self.assertEqual(groups[0].categories(), {"Nightly"})

    def test_comments_between_attributes_do_not_split_the_block(self):
        groups = lint.parse_file("x.cs", '[Test]\n// why this is explicit\n[Explicit("x")]\npublic void T() { }\n')
        self.assertEqual(len(groups), 1)
        self.assertIn("why this is explicit", " ".join(groups[0].comments))

    def test_blank_line_resets_the_leading_comment_context(self):
        """A comment separated by a blank line belongs to whatever came before, not to the attribute."""
        groups = lint.parse_file("x.cs", '// unrelated banner\n\n[Test]\npublic void T() { }\n')
        self.assertEqual(groups[0].comments, [])

    def test_enclosing_class_is_tracked(self):
        src = ('[TestFixture]\nclass Outer\n{\n    [Test]\n    [Category("Quarantine")]\n'
               '    public void T() { }\n}\n')
        groups = lint.parse_file("x.cs", src)
        member = [g for g in groups if not g.is_class][0]
        self.assertEqual(member.enclosing_class, "Outer")


if __name__ == "__main__":
    unittest.main()


class TestShardTierExclusion(LintFixtureCase):
    """A shard-named class that moved to an excluded TIER runs zero tests just as surely as an [Ignore]d one."""

    def test_shard_naming_a_nightly_fixture_fails(self):
        self.write("A.cs", '[TestFixture]\n[Explicit("stress")]\n[Category("Nightly")]\nclass ChaosStressTests\n'
                           '{\n    [Test]\n    public void T() { }\n}\n')
        self.write_shards(["Typhon.Engine.Tests.ChaosStressTests"])
        self.assert_flags("SHARD_ZERO_TESTS")

    def test_shard_naming_a_quarantined_fixture_fails(self):
        self.write("A.cs", '// Whole fixture is known-red, see #552.\n[TestFixture]\n[Category("Quarantine")]\n'
                           'class CheckerboardTests\n{\n    [Test]\n    public void T() { }\n}\n')
        self.write_shards(["Typhon.Engine.Tests.Runtime.CheckerboardTests"])
        self.assert_flags("SHARD_ZERO_TESTS")

    def test_a_method_level_tier_does_not_empty_the_fixture(self):
        """One Nightly method among normal ones leaves the class runnable — that must NOT be flagged."""
        self.write("A.cs", '[TestFixture]\nclass Mixed\n{\n    [Test]\n    public void Normal() { }\n\n'
                           '    [Test]\n    [Explicit("slow")]\n    [Category("Nightly")]\n'
                           '    public void Slow() { }\n}\n')
        self.write_shards(["Typhon.Engine.Tests.Mixed"])
        self.assert_clean()

    def test_the_excluded_set_matches_shard_py(self):
        """The lint hard-codes the tier list; if shard.py's GATE_EXCLUDED drifts, the two disagree silently."""
        import importlib.util
        import os
        shard_py = os.path.join(os.path.dirname(os.path.dirname(HERE)), "bench", "aws", "shard.py")
        spec = importlib.util.spec_from_file_location("shard_for_lint_check", shard_py)
        shard = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(shard)
        self.assertEqual(set(shard.GATE_EXCLUDED), lint.GATE_EXCLUDED_CATEGORIES)
