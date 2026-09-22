"""Planted-violation self-tests for check-blueprint-public-api.py.

A policy script that has only ever been run against a clean tree proves nothing: it is green in exactly the same way
whether it works or silently matches nothing. Each test here plants one violation in a temporary repository shaped
like the real one and requires the checker to find it, and one requires it to stay quiet when there is nothing wrong.
"""

import importlib.util
import os
import shutil
import tempfile
import unittest

SCRIPTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(repo_root):
    """Imports the checker with its REPO rebound to a temporary tree."""
    spec = importlib.util.spec_from_file_location("blueprint_check", os.path.join(SCRIPTS, "check-blueprint-public-api.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.REPO = repo_root
    return module


class BlueprintCheckTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="blueprint-check-")
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

        self.blueprint = os.path.join(self.root, "demo", "SwgTatooine", "Replication")
        self.sim = os.path.join(self.root, "demo", "SwgTatooine", "Sim")
        self.properties = os.path.join(self.root, "src", "Typhon.Engine", "Properties")
        for directory in (self.blueprint, self.sim, self.properties):
            os.makedirs(directory, exist_ok=True)

        self.write(os.path.join(self.blueprint, "Replication.cs"), "class R { void Declare() { } }\n")
        self.write(os.path.join(self.properties, "AssemblyInfo.cs"), '[assembly: InternalsVisibleTo("SwgTatooine")]\n')

        self.module = load(self.root)
        # One allow-listed file in debt, so the friend line is justified and the ratchet has a baseline.
        self.write(os.path.join(self.sim, "TatooineSim.cs"), "class S { ArchetypeClusterState _cs; }\n")
        self.module.INTERNALS_ALLOWLIST = {"demo/SwgTatooine/Sim/TatooineSim.cs": "the knobs"}

    @staticmethod
    def write(path, text):
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def run_check(self):
        violations = []
        self.module.check_lists_are_sane(violations)
        self.module.check_blueprint(violations)
        self.module.check_internals_ratchet(violations, quiet=True)
        self.module.check_friend_line(violations)
        return violations

    def test_a_clean_tree_passes(self):
        self.assertEqual(self.run_check(), [])

    def test_a_varint_in_the_blueprint_is_caught(self):
        self.write(os.path.join(self.blueprint, "Bad.cs"), "class B { void F() { w.WriteVaru(3); } }\n")
        self.assertTrue(any("varint" in v for v in self.run_check()))

    def test_a_socket_in_the_blueprint_is_caught(self):
        self.write(os.path.join(self.blueprint, "Bad.cs"), "class B { Socket _s; }\n")
        self.assertTrue(any("socket" in v for v in self.run_check()))

    def test_the_vocabulary_check_ignores_comments(self):
        self.write(os.path.join(self.blueprint, "Doc.cs"), "// no WireWriter lives here, by design\nclass D { }\n")
        self.assertEqual(self.run_check(), [])

    def test_a_new_file_reaching_into_internals_is_caught(self):
        self.write(os.path.join(self.sim, "Sneaky.cs"), "class X { EpochGuard _g; }\n")
        violations = self.run_check()
        self.assertTrue(any("Sneaky.cs" in v and "allow-list" in v for v in violations))

    def test_a_cleaned_file_must_leave_the_allowlist(self):
        self.write(os.path.join(self.sim, "TatooineSim.cs"), "class S { }\n")
        violations = self.run_check()
        self.assertTrue(any("no longer touches engine internals" in v for v in violations))

    def test_the_friend_line_must_go_when_the_debt_does(self):
        os.remove(os.path.join(self.sim, "TatooineSim.cs"))
        self.module.INTERNALS_ALLOWLIST = {}
        self.assertTrue(any("close AC-20" in v for v in self.run_check()))

    def test_a_missing_friend_line_with_debt_outstanding_is_caught(self):
        self.write(os.path.join(self.properties, "AssemblyInfo.cs"), "// nothing granted\n")
        self.assertTrue(any("cannot build" in v for v in self.run_check()))

    def test_a_public_type_on_the_internals_list_is_caught(self):
        self.module.INTERNAL_TYPES = list(self.module.INTERNAL_TYPES) + ["ClusterSpatialQuery"]
        self.assertTrue(any("is public" in v for v in self.run_check()))


if __name__ == "__main__":
    unittest.main()
