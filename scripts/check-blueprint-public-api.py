#!/usr/bin/env python3
"""Verify the SWG blueprint claim: the demo's replication code is written against Typhon's public API alone.

AC-20 of design/Subscriptions/07-delivery.md makes two claims about `demo/SwgTatooine`, and this script is what
stops either of them decaying into a comment nobody re-checks:

  1. The replication code contains no framing, codec, varint or socket code. That is the whole blueprint claim —
     an application declares WHAT to replicate and the engine owns HOW. A single hand-rolled varint in there would
     mean the public surface was not sufficient and nobody noticed.

  2. No file under the demo reaches into `Typhon.Engine.Internals` beyond a recorded allow-list, and the list only
     ever shrinks. The engine still grants `SwgTatooine` friend access because ~980 lines of MEASUREMENT code
     (the work probes and the spatial census) need cluster state the public API does not expose. That is a known
     open item, not a licence: the ratchet below means the debt cannot quietly grow while it waits.

The second check is a ratchet rather than a ban precisely because the honest state today is "partly clean". A ban
would have to be switched off, and a check that is off proves nothing.
"""

import argparse
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

#: The blueprint itself — the code that says what to replicate. Held to the strict standard.
BLUEPRINT_DIRS = ["demo/SwgTatooine/Replication"]

#: Every C# file under the demo is scanned for the internals dependency.
DEMO_ROOT = "demo/SwgTatooine"

#: The engine's own assembly-level grant, which must stay justified.
ASSEMBLY_INFO = "src/Typhon.Engine/Properties/AssemblyInfo.cs"

#: Files permitted to depend on `Typhon.Engine.Internals` today, each with the reason it is still there.
#:
#: This list is a DEBT REGISTER, not a configuration. Removing an entry is the goal; adding one needs the same
#: argument the friend line itself needed, which is why the script prints that argument when it fails.
INTERNALS_ALLOWLIST = {
    "demo/SwgTatooine/GlobalUsings.cs": "the global using itself; it goes when the last entry below goes",
    "demo/SwgTatooine/Sim/TatooineSim.cs": "three benchmark knobs (SpatialQueryTuning, ArchetypeClusterState) — P1-23 moves them to SwgTatooine.Bench",
    "demo/SwgTatooine/Sim/SimBridge.Shuttles.cs": "shuttle-port work probes read ArchetypeClusterState — measurement, moves to SwgTatooine.Bench",
    "demo/SwgTatooine/Sim/SimBridge.WorkProbe.cs": "per-query work accounting reads ArchetypeClusterState — measurement, moves to SwgTatooine.Bench",
    "demo/SwgTatooine/Sim/SpatialCensus.cs": "the census reads cluster state and is the last EpochGuard user — measurement, moves to SwgTatooine.Bench",
}

#: Internal types the demo can reach only through the friend grant. Named explicitly because the demo's global
#: using makes them look like ordinary types at the call site — a namespace grep alone would miss every one.
INTERNAL_TYPES = [
    "Typhon.Engine.Internals",
    "ArchetypeClusterState",
    "EpochGuard",
    "SpatialQueryTuning",
    "EntityId.FromRaw",
]

#: Public types that merely LOOK internal because the demo's global using flattens both namespaces at the call
#: site. `ClusterSpatialQuery` is the one that matters: it lives in `Spatial/public/` and the demo is entitled to
#: it. Listing it here rather than leaving it out of INTERNAL_TYPES keeps the reason with the decision.
PUBLIC_LOOKALIKES = ["ClusterSpatialQuery"]

#: Vocabulary that has no business in an application's replication declaration. Each entry is a thing the engine
#: owns: the wire's framing, its codecs, its integer encoding, and the socket underneath.
FORBIDDEN_IN_BLUEPRINT = [
    (r"\bWireWriter\b|\bWireReader\b", "wire framing — the engine encodes; the blueprint declares"),
    (r"\bWriteVaru\b|\bReadVaru\b|\bvarint\b", "varint encoding — the engine owns the integer encoding"),
    (r"\bFieldCodec\b|\bWireMath\b", "codec arithmetic — declare a Codec, do not implement one"),
    (r"\bSocket\b|\bWebSocket\b|\bTcpClient\b|\bTcpListener\b", "socket code — the transport seam exists so this is not here"),
    (r"\bBitConverter\b|\bMemoryMarshal\b|\bstackalloc\b", "hand-rolled serialization — a projection declares fields, it does not pack bytes"),
]


def check_lists_are_sane(violations):
    """A public type must never find its way into the internals list — that would register honest code as debt."""
    for name in PUBLIC_LOOKALIKES:
        if name in INTERNAL_TYPES:
            violations.append(
                f"{os.path.basename(__file__)}: '{name}' is public (Spatial/public/) but is listed in INTERNAL_TYPES. "
                f"It would put every honest caller on the debt register and make the ratchet meaningless.")


def read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        return handle.read()


def cs_files(root_abs, root_rel):
    """Every C# source file under a directory, as repository-relative paths with forward slashes."""
    found = []
    for base, dirs, names in os.walk(root_abs):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in names:
            if name.endswith(".cs"):
                absolute = os.path.join(base, name)
                found.append(os.path.relpath(absolute, REPO).replace(os.sep, "/"))
    return sorted(found)


def check_blueprint(violations):
    """The replication declaration must contain none of the engine's own vocabulary."""
    for directory in BLUEPRINT_DIRS:
        root = os.path.join(REPO, directory)
        if not os.path.isdir(root):
            violations.append(f"{directory}: the blueprint directory does not exist — the check has nothing to stand on")
            continue

        for rel in cs_files(root, directory):
            text = read(os.path.join(REPO, rel))
            for line_no, line in enumerate(text.splitlines(), 1):
                stripped = line.strip()
                if stripped.startswith("//") or stripped.startswith("///") or stripped.startswith("*"):
                    continue
                for pattern, why in FORBIDDEN_IN_BLUEPRINT:
                    if re.search(pattern, line):
                        violations.append(f"{rel}:{line_no}: {why} — AC-20")


def check_internals_ratchet(violations, quiet):
    """The set of demo files reaching into engine internals may shrink, never grow."""
    root = os.path.join(REPO, DEMO_ROOT)
    if not os.path.isdir(root):
        violations.append(f"{DEMO_ROOT}: not found — run from the repository, or the layout moved")
        return

    using = set()
    for rel in cs_files(root, DEMO_ROOT):
        text = read(os.path.join(REPO, rel))
        for name in INTERNAL_TYPES:
            if re.search(r"\b" + re.escape(name).replace(r"\.", r"\.") + r"\b", text):
                using.add(rel)
                break

    unexpected = sorted(using - set(INTERNALS_ALLOWLIST))
    for rel in unexpected:
        violations.append(
            f"{rel}: reaches into Typhon.Engine.Internals and is not on the allow-list. The demo is a BLUEPRINT: "
            f"every internal it touches is a gap in the public API. Close the gap, or add the file to "
            f"INTERNALS_ALLOWLIST in {os.path.basename(__file__)} with the reason and the slice that removes it.")

    cleaned = sorted(set(INTERNALS_ALLOWLIST) - using)
    for rel in cleaned:
        violations.append(
            f"{rel}: no longer touches engine internals — remove it from INTERNALS_ALLOWLIST so the ratchet holds "
            f"the ground you just took.")

    if not quiet:
        print(f"internals allow-list: {len(using)} of {len(INTERNALS_ALLOWLIST)} entries still in debt")


def check_friend_line(violations):
    """The engine's friend grant must still name the demo, and must not be silently widened."""
    path = os.path.join(REPO, ASSEMBLY_INFO)
    if not os.path.exists(path):
        violations.append(f"{ASSEMBLY_INFO}: not found")
        return

    text = read(path)
    granted = re.search(r'InternalsVisibleTo\("SwgTatooine"\)', text) is not None
    debt = len(INTERNALS_ALLOWLIST) > 0

    if granted and not debt:
        violations.append(
            f"{ASSEMBLY_INFO}: the SwgTatooine friend line is still there but no demo file needs it any more — "
            f"remove it and close AC-20.")

    if not granted and debt:
        violations.append(
            f"{ASSEMBLY_INFO}: the SwgTatooine friend line is gone but {len(INTERNALS_ALLOWLIST)} demo file(s) still "
            f"depend on internals — the demo cannot build.")


def main():
    ap = argparse.ArgumentParser(description="Verify the SWG demo's blueprint claim (AC-20).")
    ap.add_argument("--quiet", action="store_true", help="print only violations")
    args = ap.parse_args()

    violations = []
    check_lists_are_sane(violations)
    check_blueprint(violations)
    check_internals_ratchet(violations, args.quiet)
    check_friend_line(violations)

    if violations:
        print(f"\n=== BLUEPRINT_PUBLIC_API — {len(violations)} finding(s) ===", file=sys.stderr)
        for violation in violations:
            print(f"  {violation}", file=sys.stderr)
        return 1

    if not args.quiet:
        print("blueprint public-api check: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
