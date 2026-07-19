#!/usr/bin/env python3
"""ONE-TIME bootstrap: emit the declarative telemetry catalog from today's TelemetryConfig.cs.

Produces ``src/Typhon.Engine/Observability/telemetry-flags.jsonc`` — the single source of truth for the
~224 telemetry gate flags (Feature #522 / T1). Reuses the parser in ``gen-telemetry-reference.py`` to
extract every config key, default and description, then:

  * classifies each key into a *kind* (master | compositeActive | rawLeaf | subtreeResolved | group), and
  * captures the EXACT C# field name(s) the current static ctor assigns for that key.

Field names are PRESERVED verbatim (not derived): they are an ABI — 96 ``[TraceEvent(Gate="…")]``
strings plus every call site bind to them, and the names use irregular acronym casing
(``Data:MVCC:ChainWalk`` -> ``DataMvccChainWalkActive``). The generator emits them unchanged.

FAITHFULNESS is self-checked: the catalog is re-emitted as ``typhon.telemetry.template.jsonc`` and must
match the committed template byte-for-byte. Field-name correctness is validated downstream by the
engine build (the 96 gate strings + call sites resolve) and the generator's tests.

    python3 scripts/bootstrap-telemetry-catalog.py            # write the catalog + verify
    python3 scripts/bootstrap-telemetry-catalog.py --check    # verify only (CI-style)
"""

from __future__ import annotations

import argparse
import importlib.util
import re
import sys
from collections import OrderedDict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
GEN_PATH = REPO / "scripts" / "gen-telemetry-reference.py"
OUT = REPO / "src" / "Typhon.Engine" / "Observability" / "telemetry-flags.jsonc"

PREFIX = "Typhon:Profiler"
MASTER = f"{PREFIX}:Enabled"
COMPOSITE = {f"{PREFIX}:{n}:Enabled" for n in ("GcTracing", "MemoryAllocations", "CpuSampling", "Gauges")}
RAWLEAF = {
    f"{PREFIX}:Scheduler:Gauges:TransitionLatency:Enabled",
    f"{PREFIX}:Scheduler:Gauges:WorkerUtilization:Enabled",
    f"{PREFIX}:Scheduler:Gauges:StragglerGap:Enabled",
    f"{PREFIX}:Scheduler:ArchetypeTouches:Enabled",
}


def _load_gen():
    spec = importlib.util.spec_from_file_location("gen_telemetry_reference", GEN_PATH)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


GEN = _load_gen()


def kind_of(key: str) -> str:
    if key == MASTER:
        return "master"
    if key in COMPOSITE:
        return "compositeActive"
    if key in RAWLEAF:
        return "rawLeaf"
    return "subtreeResolved"


# ── field-name capture from the static ctor ──────────────────────────────────────
class Fields:
    """Resolves the exact C# field name(s) each config key maps to in the current ctor."""

    def __init__(self, text: str):
        ctor = text[text.index("static TelemetryConfig()"):]
        # <field> = <mapvar>["path"];  → subtree gate fields
        self.map_lhs = {
            f"{PREFIX}:{path}:Enabled": lhs
            for lhs, _mapv, path in re.findall(r"(\w+)\s*=\s*(\w+[Mm]ap)\[\"([^\"]+)\"\]\s*;", ctor)
        }
        # <lhs> = ReadBool(config, "key", default);  → raw reads (composite *Enabled, raw leaves, root locals)
        self.readbool_lhs = {
            key: lhs
            for lhs, key in re.findall(r"(\w+)\s*=\s*ReadBool\(\s*config\s*,\s*\"([^\"]+)\"\s*,\s*(?:true|false)\s*\)", ctor)
        }
        # <active> = (ProfilerActive|Enabled) && <raw>;  → composite derived-Active FIELDS only.
        # Skip `var` local temporaries (e.g. `var schedulerDepthRootEffective = Enabled && SchedulerEnabled;`)
        # so the real field (`SchedulerActive = Enabled && SchedulerEnabled;`) wins for the same raw.
        self.composite_active = {}
        for m in re.finditer(r"(?m)^\s*(var\s+)?(\w+)\s*=\s*(?:ProfilerActive|Enabled)\s*&&\s*(\w+)\s*;", ctor):
            if m.group(1):
                continue
            self.composite_active[m.group(3)] = m.group(2)

    def resolve(self, key: str, kind: str):
        """Return (field, enabledField) for a key. enabledField is None except for composites."""
        if kind == "master":
            return "ProfilerActive", None
        if kind == "compositeActive":
            raw = self.readbool_lhs.get(key)
            return self.composite_active.get(raw, raw), raw
        if kind == "rawLeaf":
            return self.readbool_lhs.get(key), None
        # subtreeResolved: prefer the map assignment; fall back to the composite path (Scheduler hybrid).
        if key in self.map_lhs:
            return self.map_lhs[key], None
        raw = self.readbool_lhs.get(key)
        return self.composite_active.get(raw, raw), None


# ── catalog tree ─────────────────────────────────────────────────────────────────
class Cat:
    __slots__ = ("name", "kind", "default", "desc", "field", "enabledField", "children")

    def __init__(self, name):
        self.name = name
        self.kind = "group"
        self.default = None
        self.desc = None
        self.field = None
        self.enabledField = None
        self.children = OrderedDict()

    def child(self, name):
        if name not in self.children:
            self.children[name] = Cat(name)
        return self.children[name]


def build_catalog(entries, fields: Fields):
    root = Cat("Profiler")
    for e in entries:
        inner = e.key[len(PREFIX) + 1: -len(":Enabled")] if e.key != MASTER else ""
        node = root
        if inner:
            for seg in inner.split(":"):
                node = node.child(seg)
        node.kind = kind_of(e.key)
        node.default = e.default
        node.desc = e.desc
        node.field, node.enabledField = fields.resolve(e.key, node.kind)
    return root


BANNER = "GENERATED once by scripts/bootstrap-telemetry-catalog.py from TelemetryConfig.cs (Feature #522, T1)."


def emit_jsonc(root: Cat) -> str:
    lines = [
        f"// {BANNER}",
        "// Single source of truth for Typhon's telemetry gate flags. The source generator emits both the",
        "// perf projection (TelemetryConfig.g.cs) and the runtime catalog (TelemetryFlagCatalog.g.cs) from this;",
        "// the docs (telemetry-flags-reference.md + typhon.telemetry.template.jsonc) regenerate from it too.",
        "// `field` = the exact C# field name (an ABI: 96 [TraceEvent(Gate=...)] strings + call sites bind to it).",
        "// `desc` is user-facing (docs + CLI tooltips). `//` comments are author-only. `kind`: master |",
        "// compositeActive | rawLeaf | subtreeResolved | group. `default` omitted = false.",
        "{",
        f'  "prefix": "{PREFIX}",',
        '  "root":',
    ]

    def esc(s):
        return (s or "").replace("\\", "\\\\").replace('"', '\\"')

    def walk(node: Cat, indent: int, trailing_comma: bool):
        pad = "  " * indent
        lines.append(f"{pad}{{")
        inner = "  " * (indent + 1)
        lines.append(f'{inner}"name": "{node.name}",')
        lines.append(f'{inner}"kind": "{node.kind}",')
        if node.field:
            lines.append(f'{inner}"field": "{node.field}",')
        if node.enabledField:
            lines.append(f'{inner}"enabledField": "{node.enabledField}",')
        if node.default:
            lines.append(f'{inner}"default": true,')
        if node.desc:
            lines.append(f'{inner}"desc": "{esc(node.desc)}",')
        kids = list(node.children.values())
        if kids:
            lines.append(f'{inner}"children": [')
            for i, k in enumerate(kids):
                walk(k, indent + 2, i != len(kids) - 1)
            lines.append(f"{inner}]")
        else:
            lines[-1] = lines[-1].rstrip(",")
        lines.append(f"{pad}}}{',' if trailing_comma else ''}")

    walk(root, 2, False)
    lines.append("}")
    return "\n".join(lines) + "\n"


def catalog_to_gen_tree(root: Cat) -> "GEN.Node":
    def conv(node: Cat) -> GEN.Node:
        g = GEN.Node()
        if node.default is not None or node.desc:
            g.toggle = (bool(node.default), node.desc or "")
        for k in node.children.values():
            g.kids[k.name] = conv(k)
        return g
    typhon = GEN.Node()
    typhon.kids["Typhon"] = GEN.Node()
    typhon.kids["Typhon"].kids["Profiler"] = conv(root)
    return typhon


def faithfulness_ok(root: Cat):
    committed = (REPO / "doc" / "feature-set" / "Observability" / "typhon.telemetry.template.jsonc")
    expected = committed.read_text(encoding="utf-8") if committed.exists() else None
    regenerated = GEN.emit_jsonc(catalog_to_gen_tree(root))
    return expected == regenerated


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="verify only; do not write the catalog")
    ap.add_argument("--source", help="override path to the hand-written TelemetryConfig.cs to parse (one-time bootstrap)")
    args = ap.parse_args()

    src_path = Path(args.source) if args.source else GEN.SRC
    text = src_path.read_text(encoding="utf-8")
    summaries = GEN.parse_field_summaries(text)
    entries = GEN.parse_keys(text, summaries)
    if not entries:
        print("ERROR: no telemetry flags parsed", file=sys.stderr)
        return 2

    fields = Fields(text)
    root = build_catalog(entries, fields)

    # every keyed node must have resolved a field name
    missing = [e.key for e, n in ((e, _find(root, e.key)) for e in entries) if n is not None and n.field is None]
    if missing:
        print("ERROR: could not resolve a C# field name for:", file=sys.stderr)
        for k in missing:
            print("  " + k, file=sys.stderr)
        return 2

    if not faithfulness_ok(root):
        print("FAITHFULNESS FAILED: catalog does not round-trip to the committed template.", file=sys.stderr)
        return 1
    print(f"faithfulness OK — {len(entries)} flags round-trip to the committed template.")

    emitted = emit_jsonc(root)
    if args.check:
        cur = OUT.read_text(encoding="utf-8") if OUT.exists() else None
        if cur != emitted:
            print(f"STALE: {OUT.relative_to(REPO)} differs from a fresh bootstrap.", file=sys.stderr)
            return 1
        print(f"{OUT.relative_to(REPO)} up to date.")
        return 0

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(emitted, encoding="utf-8")
    print(f"wrote {OUT.relative_to(REPO)} ({len(entries)} flags).")
    return 0


def _find(root: Cat, key: str):
    inner = key[len(PREFIX) + 1: -len(":Enabled")] if key != MASTER else ""
    node = root
    if inner:
        for seg in inner.split(":"):
            if seg not in node.children:
                return None
            node = node.children[seg]
    return node


if __name__ == "__main__":
    raise SystemExit(main())
