#!/usr/bin/env python3
"""
Self-tests for `scripts/audit-rule-coverage.py` (#703 Part 2).

WHY: this audit exists because a verifier that has never rejected anything is not evidence. An AUDIT that has
never rejected anything is the same claim one level up, so every check here gets a planted violation — including
#703's named acceptance clause, *"a planted always-green verifier fails the mutant check"*.

One case pins a real bug this file caught: rule ids carry an optional lowercase sub-rule suffix (`ED-05a`..`ED-05f`
are distinct sub-rules of `ED-05`), and an id grammar without it truncated all six to `ED-05` and reported a 7-way
duplicate that did not exist.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import importlib.util
import json
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.dirname(HERE)

spec = importlib.util.spec_from_file_location("audit_rule_coverage",
                                              os.path.join(SCRIPTS, "audit-rule-coverage.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.rules = os.path.join(self.tmp.name, "rules")
        self.tests = os.path.join(self.tmp.name, "test")
        os.makedirs(self.rules)
        os.makedirs(self.tests)
        self.baseline = os.path.join(self.tmp.name, "baseline.json")
        self.matrix = os.path.join(self.tmp.name, "out", "matrix.md")

    def tearDown(self):
        self.tmp.cleanup()

    def rule_file(self, name, body):
        with open(os.path.join(self.rules, name), "w", encoding="utf-8") as fh:
            fh.write(body)

    def cs_file(self, name, body):
        with open(os.path.join(self.tests, name), "w", encoding="utf-8") as fh:
            fh.write(body)

    def run_audit(self, extra=()):
        return audit.main(["--rules-dir", self.rules, "--tests-dir", self.tests,
                           "--baseline", self.baseline, "--matrix", self.matrix, *extra])

    def write_baseline(self, **counts):
        with open(self.baseline, "w", encoding="utf-8") as fh:
            json.dump(counts, fh)


ONE_RULE = """\
## Module: Example

### LOG-99: A rule that is verified `[fatal]`
  invariant something must hold
  scope: Nothing.cs
  verified: ExampleTests
"""

VERIFIED_AND_MUTATED = """\
using NUnit.Framework;
namespace Typhon.Engine.Tests;
[TestFixture]
internal sealed class ExampleTests
{
    [Test]
    [VerifiesRule("LOG-99")]
    public void Verifies() { }

    [Test]
    [RuleMutant("LOG-99")]
    public void Mutant() { }
}
"""


class TestDuplicateIds(AuditCase):
    def test_the_same_id_in_two_files_is_rejected(self):
        self.rule_file("a.md", "### CX-01: Cancellation `[fatal]`\n  invariant x\n")
        self.rule_file("b.md", "### CX-01: Commit ordering `[fatal]`\n  invariant y\n")
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 1)

    def test_lowercase_sub_rule_suffixes_are_distinct_ids(self):
        """ED-05a..ED-05f are SUB-RULES of ED-05, not six restatements of it. Truncating the suffix invented a
        7-way duplicate — the audit's own first false positive."""
        body = "### ED-05: Parent `[fatal]`\n  invariant p\n"
        body += "".join(f"### ED-05{c}: Sub-rule {c} `[fatal]`\n  invariant s\n" for c in "abcdef")
        self.rule_file("r.md", body)
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 0)

    def test_ids_unique_across_files_pass(self):
        self.rule_file("a.md", "### CX-01: Cancellation `[fatal]`\n  invariant x\n")
        self.rule_file("b.md", "### CPO-01: Commit ordering `[fatal]`\n  invariant y\n")
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 0)


class TestUnknownRuleId(AuditCase):
    def test_attribute_naming_a_nonexistent_rule_is_rejected(self):
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", 'class T { [VerifiesRule("XX-42")] public void V() { } }\n')
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 1)


class TestDanglingVerified(AuditCase):
    def test_verified_naming_a_nonexistent_test_is_rejected(self):
        self.rule_file("r.md", "### LOG-99: x `[fatal]`\n  verified: DeletedLongAgoTests\n")
        self.cs_file("T.cs", "class SomethingElseTests { }\n")
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 1)

    def test_not_covered_is_an_honest_value_not_a_dangling_one(self):
        """`NOT COVERED — <why>` is how a rule states the truth; it must not be read as a broken citation."""
        self.rule_file("r.md", "### LOG-99: x `[fatal]`\n  verified: NOT COVERED — needs a real emitter test\n")
        self.cs_file("T.cs", "class SomethingElseTests { }\n")
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 0)

    def test_verified_naming_a_real_test_passes(self):
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", VERIFIED_AND_MUTATED)
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 0)


class TestMutantAccounting(AuditCase):
    def test_planted_always_green_verifier_is_reported(self):
        """#703's acceptance clause: a [VerifiesRule] with no [RuleMutant] must be surfaced."""
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", 'class ExampleTests { [VerifiesRule("LOG-99")] public void Verifies() { } }\n')
        self.write_baseline(rules_with_verifier=1, rules_with_mutant=0, fatal_rules_with_verifier=1)
        rc = self.run_audit()
        # Reported, non-blocking on its own — the ratchet is what blocks. Both facts are asserted:
        self.assertEqual(rc, 0)
        with open(self.matrix, encoding="utf-8") as fh:
            self.assertIn("LOG-99", fh.read())

    def test_a_mutant_counts_toward_the_ratchet(self):
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", VERIFIED_AND_MUTATED)
        self.run_audit(["--update-baseline"])
        with open(self.baseline, encoding="utf-8") as fh:
            self.assertEqual(json.load(fh)["rules_with_mutant"], 1)


class TestRatchet(AuditCase):
    def test_losing_a_mutant_is_blocking(self):
        """The one thing the ratchet exists for: a verifier or mutant cannot be quietly deleted."""
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", 'class ExampleTests { [VerifiesRule("LOG-99")] public void Verifies() { } }\n')
        self.write_baseline(rules_with_verifier=1, rules_with_mutant=1, fatal_rules_with_verifier=1)
        self.assertEqual(self.run_audit(), 1)

    def test_losing_a_verifier_is_blocking(self):
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", "class ExampleTests { }\n")
        self.write_baseline(rules_with_verifier=1, rules_with_mutant=0, fatal_rules_with_verifier=1)
        self.assertEqual(self.run_audit(), 1)

    def test_gaining_coverage_is_fine(self):
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", VERIFIED_AND_MUTATED)
        self.write_baseline(rules_with_verifier=0, rules_with_mutant=0, fatal_rules_with_verifier=0)
        self.assertEqual(self.run_audit(), 0)

    def test_a_missing_baseline_is_blocking(self):
        """No baseline means the ratchet cannot detect a deletion — the one thing it is for."""
        self.rule_file("r.md", ONE_RULE)
        self.cs_file("T.cs", VERIFIED_AND_MUTATED)
        self.assertEqual(self.run_audit(), 1)


class TestForkSafety(AuditCase):
    def test_absent_rules_dir_without_the_flag_is_a_usage_error(self):
        rc = audit.main(["--rules-dir", os.path.join(self.tmp.name, "nope"),
                         "--tests-dir", self.tests, "--baseline", self.baseline, "--matrix", self.matrix])
        self.assertEqual(rc, 2)

    def test_absent_rules_dir_with_the_flag_skips_cleanly(self):
        """Fork PRs cannot check out the private knowledge base; the job must skip, not red-out."""
        rc = audit.main(["--rules-dir", os.path.join(self.tmp.name, "nope"),
                         "--tests-dir", self.tests, "--baseline", self.baseline, "--matrix", self.matrix,
                         "--skip-if-no-rules"])
        self.assertEqual(rc, 0)


if __name__ == "__main__":
    unittest.main()
