"""Self-tests for `check-rule-scopes.py`.

The load-bearing case is `test_unresolved_symbol_fails`: a checker that returned 0 unconditionally would be
indistinguishable from a working one without it — the same argument the rule-coverage audit makes about a
`[VerifiesRule]` test that ships no `[RuleMutant]`.

These tests previously covered a `--baseline-rules` regression-only mode, which demoted an unresolved symbol to a
warning when the change introduced it. That mode existed only because rules lived in a different repository from
the code they scope, so the two halves could not land together (#747). They can now, so the mode is gone and every
unresolved symbol fails again.

Run: python3 -m unittest discover -s scripts/tests
"""

import importlib.util
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

SCRIPTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHECKER = os.path.join(SCRIPTS, "check-rule-scopes.py")

# A symbol no C# source could plausibly contain. Deliberately not a realistic-looking name: a test that fails the
# day someone legitimately adds `Foo` is worse than no test.
ABSENT = "NoSuchSymbolQqZzWibble"


def _load_checker():
    """Import the hyphenated script as a module, so candidate_symbols can be unit-tested directly."""
    spec = importlib.util.spec_from_file_location("check_rule_scopes", CHECKER)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class ScopeResolution(unittest.TestCase):
    """The checker locates both `src/` and `rules/` from its own path, so the fixture is a whole fake repo."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.tmp.name, "repo")
        os.makedirs(os.path.join(self.root, "scripts"))
        os.makedirs(os.path.join(self.root, "src"))
        os.makedirs(os.path.join(self.root, "rules"))

        with open(os.path.join(self.root, "src", "Thing.cs"), "w", encoding="utf-8") as fh:
            fh.write("namespace N { internal sealed class ThingThatExists { } }\n")
        with open(CHECKER, encoding="utf-8") as src, \
                open(os.path.join(self.root, "scripts", "check-rule-scopes.py"), "w", encoding="utf-8") as dst:
            dst.write(src.read())

        self._rule(f"### ZZ-01: fake\n  scope: {ABSENT}\n")

    def tearDown(self):
        self.tmp.cleanup()

    def _rule(self, text):
        with open(os.path.join(self.root, "rules", "zz.md"), "w", encoding="utf-8") as fh:
            fh.write(textwrap.dedent(text))

    def _run(self):
        """Invoke the checker as a subprocess — the exit code IS the contract the workflow depends on."""
        return subprocess.run([sys.executable, os.path.join(self.root, "scripts", "check-rule-scopes.py")],
                              capture_output=True, text=True)

    def test_unresolved_symbol_fails(self):
        """THE load-bearing case. A scope naming something absent from src/ is a defect in the diff that adds it."""
        r = self._run()
        self.assertEqual(1, r.returncode, r.stdout + r.stderr)
        self.assertIn("not found in src/", r.stdout)
        self.assertIn(ABSENT, r.stdout)

    def test_resolving_symbol_passes(self):
        """Guards the guard: if the fake engine tree stopped resolving anything, the case above would pass vacuously."""
        self._rule("### ZZ-01: fake\n  scope: ThingThatExists\n")
        r = self._run()
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertIn("all scope symbols resolve", r.stdout)

    def test_missing_source_tree_is_an_error_not_a_silent_pass(self):
        """A checkout with no `src/` must exit 2, never scan nothing and report a cheerful green."""
        os.rename(os.path.join(self.root, "src"), os.path.join(self.root, "src-moved"))
        self.assertEqual(2, self._run().returncode)

    def test_missing_rules_dir_is_an_error_not_a_silent_pass(self):
        """Same reasoning from the other side: no rules to check is a broken checkout, not a pass."""
        os.rename(os.path.join(self.root, "rules"), os.path.join(self.root, "rules-moved"))
        self.assertEqual(2, self._run().returncode)


class SymbolExtraction(unittest.TestCase):
    """Pinned because a parse change silently alters which citations are checked at all."""

    def setUp(self):
        self.mod = _load_checker()

    def test_file_and_identifier_forms(self):
        got = self.mod.candidate_symbols("  scope: Foo/Bar/Baz.cs (the writer), Alpha.Beta and prose words")
        self.assertIn("Baz.cs", got)
        self.assertIn("Alpha", got)
        self.assertIn("Beta", got)
        self.assertNotIn("Foo", got)   # directory segment, not a symbol
        self.assertNotIn("the", got)   # stopword

    def test_identifiers_shorter_than_four_chars_are_ignored(self):
        """Deliberate, and worth pinning: the head pattern needs 4+ chars, so `Qux.Quux` yields only `Quux`. Short
        PascalCase words are far more often prose than types, and a false positive here wastes an afternoon."""
        self.assertEqual({"Quux"}, self.mod.candidate_symbols("  scope: Qux.Quux"))

    def test_file_stem_is_not_emitted_as_an_identifier(self):
        """`RecordFormat.cs` is a FILE; requiring a type of that name inside it produced false positives."""
        self.assertNotIn("RecordFormat", self.mod.candidate_symbols("  scope: RecordFormat.cs"))


if __name__ == "__main__":
    unittest.main()
