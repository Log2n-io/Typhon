# Span Source Attribution (Go-to-Source)
> Every span you see in the Workbench knows the exact file, line, and method that emitted it — one click away.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Profiler](./README.md)

## 🎯 What it solves

A flame graph tells you *what* ran and *how long* it took, but not *where in your code* it came from. Without
attribution, tracking a slow `BTreeInsert` or `TransactionCommit` span back to the call site means grepping the
codebase by hand. Source attribution closes that gap: selecting any span in the Workbench shows its emission
`file:line · method` and offers a one-click jump into your editor, plus an inline read-only preview — no manual
searching, no extra instrumentation to write.

This is **call-site attribution, not call-stack attribution** — it identifies the single line that emitted the
span, not the full caller chain. (Full call stacks are covered separately by CPU sampling's Call Tree view.)

## ⚙️ How it works (in brief)

At build time, every `TyphonEvent.BeginXxx(...)` call site in the engine is discovered and assigned a
deterministic `ushort` id (sorted by file/line/column, so the same site gets the same id on every build). A
C# 14 compiler interceptor silently redirects each call to carry that id along — no change to the code you write.
At runtime the id rides in the span's header and, when the field is non-zero, costs two extra bytes on the wire.
A companion manifest (built once, shipped with the trace) maps each id back to `file`, `line`, and `method`, so
the Workbench never needs your source tree to decode it — only to display and open it.

User-registered ECS systems get the same treatment through a different route: their registration delegate is
resolved via the assembly's portable PDB at scheduler build time, so `MoveAllAnts` or similar callback systems
show their own source, not the scheduler's dispatch site.

## 💻 Usage

There is nothing to call — attribution is automatic for every existing profiler span once a trace is captured:

```csharp
// Nothing new here — this is any already-instrumented engine call, e.g. inside a custom index or system.
// Attribution rides along for free: no BeginXxxWithSiteId, no extra parameter, no opt-in call.
using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});
runtime.Start();

// Open the resulting .typhon-trace (or a live session) in the Workbench, select any span —
// e.g. a BTree.Insert or Transaction.Commit — and the detail panel shows:
//   Source: BTree.Insert.cs:1125 · BTree.Insert(...)
//     [Open in editor]   [Show inline]
```

Editor handoff and workspace-root resolution are configured once in the Workbench, not in engine code:

| Option (Workbench → Options → Editor/Profiler) | Default | Effect |
|---|---|---|
| Editor kind | VS Code | Which app `Open in editor` launches — VS Code, Cursor, Rider, Visual Studio (Windows), or a custom argv template |
| Workspace root | Auto-detected (`.git` walk-up from CWD) | Base directory the trace's repo-relative paths are resolved against |

## ⚠️ Guarantees & limits

- **Near-zero recording cost.** The id is a literal `ushort` baked into IL by the interceptor — no dictionary
  lookup, no reflection, no allocation on the hot path. With telemetry off, the code is JIT-eliminated entirely.
- **Two bytes on the wire per span**, only when the id is non-zero. Manifest itself is written once per
  file/session (a few hundred sites, tens of KB), not per span.
- **Engine spans only.** The generator attributes call sites inside `Typhon.Engine`; sites outside it resolve to
  the "unknown source" sentinel (id `0`) and simply show no Source row. There is no user-facing custom-span API
  yet (see the *Custom Application-Defined Spans* feature), so this currently covers Typhon's built-in span set.
- **User-registered systems are best-effort.** Resolution depends on a portable PDB being available next to the
  assembly (or discoverable by name/version on hosts that report no path, e.g. Godot Mono). Missing PDB or a
  purely synthesized method yields no Source row — the same fallback as id `0`.
- **Backward-compatible wire format.** Older trace files without the manifest still decode; they simply carry no
  source rows.
- **Not stack attribution.** One call site per span, not a full caller chain.
- **Build-configuration sensitive.** Site ids and inlining differ between Debug and Release builds. Fine for
  viewing a single trace; don't diff site ids across configurations — join on `(file, line, method)` instead if
  you need cross-run comparison.

## 🧪 Tests

- [SourceLocationGeneratorTests](../../../test/Typhon.Engine.Tests/Profiler/SourceLocationGeneratorTests.cs) — the emitted `SourceLocations` table is well-formed: non-empty, deterministic ordering, repo-relative paths, IDs starting at 1, no duplicates
- [SourceLocationManifestRoundTripTests](../../../test/Typhon.Engine.Tests/Profiler/SourceLocationManifestRoundTripTests.cs) — manifest write/read round-trip in the trace trailer, including header offset patching
- [SystemSourceResolverByTokenTests](../../../test/Typhon.Engine.Tests/Profiler/SystemSourceResolverByTokenTests.cs) — resolving a user-registered system by `(module, metadataToken)` (the CPU-sample-frame path, no live `MethodInfo`) plus a regression guard on the existing delegate-based path

## 🔗 Related

- Sibling features: [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md), [Custom Application-Defined Spans](./custom-named-spans.md)
- Source: `src/Typhon.Engine/Profiler/internals/SystemSourceResolver.cs`, `src/Typhon.Engine/Profiler/internals/RuntimeSourceLocationManifest.cs`, `src/Typhon.Generators/SourceLocationGenerator.cs`, `src/Typhon.Generators/TraceEventGenerator.cs`

<!-- Deep dive: claude/design/Profiler/10-profiler-source-attribution.md, claude/design/Profiler/11-cpu-sampling-integration.md §7 -->
<!-- Overview: claude/overview/09-observability.md -->
