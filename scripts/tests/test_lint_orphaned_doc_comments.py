"""Self-tests for scripts/lint-orphaned-doc-comments.py.

The checker's whole value is that it fires on the insertion mistake and on nothing else, so the cases below are
paired: each "this is an orphan" has a "this looks like one and is not" beside it. A checker that flagged an
attribute-separated pair, or a `<summary>` opening a fresh comment after a blank line, would be worse than nothing —
it would be turned off.
"""
import importlib.util
import json
import os
import shutil
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(os.path.dirname(HERE), "lint-orphaned-doc-comments.py")

_spec = importlib.util.spec_from_file_location("lint_orphaned_doc_comments", SCRIPT)
lint = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(lint)


class OrphanDetection(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="orphan-lint-")
        os.makedirs(os.path.join(self.root, "src"))

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def write(self, name, text):
        path = os.path.join(self.root, "src", name)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)
        return path

    def scan(self, name, text):
        self.write(name, text)
        return lint.scan(self.root, ["src"])

    def test_two_summaries_in_a_row_is_an_orphan(self):
        found = self.scan("A.cs", """
/// <summary>Documents nothing now.</summary>
/// <summary>The member below.</summary>
internal void M() { }
""")
        self.assertEqual({"src/A.cs": [3]}, found)

    def test_a_summary_after_a_remarks_close_is_an_orphan(self):
        found = self.scan("B.cs", """
/// <summary>First.</summary>
/// <remarks>Why.</remarks>
/// <summary>Second.</summary>
internal void M() { }
""")
        self.assertEqual({"src/B.cs": [4]}, found)

    def test_three_stacked_summaries_report_both_orphans(self):
        # The real shape found in DatabaseEngine.TickFence.cs: two members left undocumented, not one.
        found = self.scan("C.cs", """
/// <summary>One.</summary>
/// <summary>Two.</summary>
/// <summary>Three.</summary>
internal void M() { }
""")
        self.assertEqual({"src/C.cs": [3, 4]}, found)

    def test_a_summary_on_its_own_member_is_not_an_orphan(self):
        found = self.scan("D.cs", """
/// <summary>First member.</summary>
internal void First() { }

/// <summary>Second member.</summary>
internal void Second() { }
""")
        self.assertEqual({}, found)

    def test_an_attribute_between_them_is_not_an_orphan(self):
        # Two members, the second carrying an attribute. The attribute ends the `///` block, so each summary is alone.
        found = self.scan("E.cs", """
/// <summary>First.</summary>
[Obsolete]
/// <summary>Second.</summary>
internal void M() { }
""")
        self.assertEqual({}, found)

    def test_out_of_order_elements_in_one_comment_are_not_an_orphan(self):
        # `InProcessLink.RequestKick` exactly: one member, one summary, and `<remarks>` written before it. XML doc has
        # no required element order, so an adjacency check flagged this and it is not a defect. It is the reason the
        # check counts summaries per block instead.
        found = self.scan("H.cs", """
/// <inheritdoc />
/// <remarks>Refused, as the runtime refuses it.</remarks>
/// <summary>The one summary this member has.</summary>
internal bool M() => false;
""")
        self.assertEqual({}, found)

    def test_a_summary_after_returns_in_the_same_comment_is_not_an_orphan(self):
        found = self.scan("I.cs", """
/// <param name="x">The input.</param>
/// <returns>The output.</returns>
/// <summary>Still one member.</summary>
internal int M(int x) => x;
""")
        self.assertEqual({}, found)

    def test_a_blank_line_between_them_is_not_an_orphan(self):
        found = self.scan("F.cs", """
/// <summary>First.</summary>

/// <summary>Second.</summary>
internal void M() { }
""")
        self.assertEqual({}, found)

    def test_a_multiline_summary_is_not_an_orphan(self):
        found = self.scan("G.cs", """
/// <summary>
/// A summary that spans lines.
/// </summary>
internal void M() { }
""")
        self.assertEqual({}, found)

    def test_a_bom_and_crlf_do_not_hide_an_orphan(self):
        # Probed because a miss here would be invisible: the file reads normally and the check simply says nothing.
        path = os.path.join(self.root, "src", "Bom.cs")
        with open(path, "wb") as fh:
            fh.write(b"\xef\xbb\xbf/// <summary>Orphan.</summary>\r\n/// <summary>Real.</summary>\r\nclass E { }\r\n")
        self.assertEqual({"src/Bom.cs": [2]}, lint.scan(self.root, ["src"]))

    def test_a_doc_comment_on_the_same_line_as_code_is_not_an_orphan(self):
        found = self.scan("J.cs", "/// <summary>Only one.</summary> class F { }\n")
        self.assertEqual({}, found)

    def test_a_preprocessor_directive_between_them_is_a_known_miss(self):
        # Documented as a limit, not a target: `#region` ends the `///` block, so the pair is not seen. Asserted so the
        # behaviour is a decision on record — if someone later makes the check span directives, this test tells them
        # they changed something deliberate rather than fixing an oversight.
        found = self.scan("K.cs", """
/// <summary>Orphaned, and not caught.</summary>
#region Stuff
/// <summary>The member's own.</summary>
class B { }
#endregion
""")
        self.assertEqual({}, found)

    def test_obj_and_bin_are_skipped(self):
        os.makedirs(os.path.join(self.root, "src", "obj"))
        with open(os.path.join(self.root, "src", "obj", "Generated.cs"), "w", encoding="utf-8") as fh:
            fh.write("/// <summary>A.</summary>\n/// <summary>B.</summary>\nclass X { }\n")
        self.assertEqual({}, lint.scan(self.root, ["src"]))


class Ratchet(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="orphan-ratchet-")
        self.baseline = os.path.join(self.root, "coverage", "baseline.json")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_missing_baseline_is_reported(self):
        findings = lint.ratchet({}, self.baseline, update=False)
        self.assertEqual(["BASELINE_MISSING"], [f.check for f in findings])

    def test_update_writes_counts_not_line_numbers(self):
        lint.ratchet({"a.cs": [3, 9]}, self.baseline, update=True)
        with open(self.baseline, encoding="utf-8") as fh:
            self.assertEqual({"a.cs": 2}, json.load(fh))

    def test_at_baseline_is_clean(self):
        lint.ratchet({"a.cs": [3, 9]}, self.baseline, update=True)
        self.assertEqual([], lint.ratchet({"a.cs": [3, 9]}, self.baseline, update=False))

    def test_one_more_in_a_known_file_blocks(self):
        lint.ratchet({"a.cs": [3]}, self.baseline, update=True)
        findings = lint.ratchet({"a.cs": [3, 9]}, self.baseline, update=False)
        self.assertEqual(["ORPHANED_DOC_COMMENT"], [f.check for f in findings])
        self.assertIn("9", findings[0].where, "the finding names the line that is over budget, not the grandfathered one")

    def test_a_new_file_with_any_blocks(self):
        lint.ratchet({}, self.baseline, update=True)
        findings = lint.ratchet({"new.cs": [12]}, self.baseline, update=False)
        self.assertEqual(["ORPHANED_DOC_COMMENT"], [f.check for f in findings])

    def test_fixing_one_is_advisory_not_blocking(self):
        # Fixing a grandfathered orphan must never fail the build; it asks for a baseline bump so the slack is not
        # left available for someone else to spend.
        lint.ratchet({"a.cs": [3, 9]}, self.baseline, update=True)
        findings = lint.ratchet({"a.cs": [3]}, self.baseline, update=False)
        self.assertEqual(["BASELINE_STALE"], [f.check for f in findings])

    def test_one_file_improving_does_not_pay_for_another_regressing(self):
        lint.ratchet({"a.cs": [1, 2], "b.cs": [1]}, self.baseline, update=True)
        findings = lint.ratchet({"a.cs": [1], "b.cs": [1, 2]}, self.baseline, update=False)
        checks = sorted(f.check for f in findings)
        self.assertEqual(["BASELINE_STALE", "ORPHANED_DOC_COMMENT"], checks)


if __name__ == "__main__":
    unittest.main()
