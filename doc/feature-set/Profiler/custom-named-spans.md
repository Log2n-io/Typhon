---
uid: feature-profiler-custom-named-spans
title: 'Custom Application-Defined Spans'
description: 'A reserved wire-format span kind for app-defined names, designed to nest into the profiler flame graph — not yet callable from application code.'
---

# Custom Application-Defined Spans
> A reserved wire-format span kind for app-defined names, designed to nest into the profiler flame graph — not yet callable from application code.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Application code often has its own hot paths — a save pipeline step, a custom job, a request handler — that a
developer wants to see in the same flame graph as Typhon's built-in transaction/B+Tree/page-cache spans, without
Typhon engine changes for every new span name. Today only Typhon's own instrumented call sites emit spans; there is
no supported way for host application code to add its own span into the same trace.

## ⚙️ How it works (in brief)

The wire format reserves a span kind for exactly this purpose: `TraceEventKind.NamedSpan` (wire ID `246`) carries an
inline UTF-8 name instead of a fixed typed payload, and the trace file / sidecar cache formats both support an
optional trailing name-intern table (`SpanNameTable` section, magic `"SPAN"`) so repeated names aren't re-written in
full on every span. That plumbing exists on the read/replay side (`Typhon.Profiler` library reads and replays a
`SpanNameTable` if one is present). What's missing is the producer half: no factory on `TyphonEvent` mints a
`NamedSpan` record from an app-supplied string today.

## 💻 Usage

Not implemented — no factory exists yet, so nothing below compiles against the current engine.

```csharp
// Illustrative only — not a real/current Typhon API. TyphonEvent has no BeginNamedSpan/EmitNamedSpan
// factory today. This is the shape the design docs describe, but it does not exist in source.
// using var scope = TyphonEvent.BeginNamedSpan("MyCustomSpan");
// ... app-specific work ...
```

## ⚠️ Guarantees & limits

- **Not implemented.** `src/Typhon.Engine/Profiler/internals/TyphonEvent.cs` — the producer surface every other
  span factory (`BeginBTreeInsert`, `BeginTransactionCommit`, etc.) lives on — has no `BeginNamedSpan` or
  `EmitNamedSpan` method. `Typhon.Generators.TraceEventGenerator` has no NamedSpan-specific codegen either. The only
  reference to `TraceEventKind.NamedSpan` in `src/Typhon.Engine` or `test/` is a single entry in an
  enum-exhaustiveness suppression test (`TyphonEventKindSuppressionTests.cs`) — there is no producer call site
  anywhere in the codebase.
- The wire slot is reserved and stable: `TraceEventKind.NamedSpan = 246` in the current `.typhon-trace` format
  (v8+). It was `200` until 2026-05-10, when it was found to collide with `EcsQueryMaskAnd` and was reassigned; v7
  traces carrying a kind-200 `NamedSpan` record are unaffected (older format), but the two kinds are indistinguishable
  under the pre-fix numbering.
- The `SpanNameTable` intern-table format exists only in the file writer/reader and sidecar-cache
  writer/reader (`Typhon.Profiler`); it is populated by replaying a table that was already in a source file, never
  by the engine's own `FileExporter`, which never constructs one.
- The Workbench viewer's chunk decoder recognizes kind `246` but falls back to a generic span decode — the interned
  name is not surfaced on the server DTO or rendered distinctly in the UI today.
- `claude/design/Profiler/typhon-profiler.md` and `claude/design/Profiler/profiler-user-manual.md` both describe a
  `TyphonEvent.BeginNamedSpan(string)` factory as if already usable, at wire kind `200`. Both statements are stale
  against current source — treat those sections as a design target, not a working API, until this feature is built.

## 🔗 Related

- Sibling: [Built-in Engine Instrumentation Catalog](./builtin-subsystem-instrumentation.md) — the engine-side equivalent this app-defined span kind is designed to parallel
- Sibling: [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md) — the producer surface (`TyphonEvent`) a future `BeginNamedSpan` factory would extend
- Source: `src/Typhon.Profiler/TraceEventKind.cs` (`NamedSpan = 246`), `src/Typhon.Profiler/TraceFileWriter.cs`,
  `TraceFileCacheWriter.cs` / `TraceFileCacheReader.cs` (`SpanNameTable` plumbing)

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §4.4, §5 (Producer API) — describes the intended shape; needs a refresh once implemented -->
<!-- claude/design/Profiler/profiler-user-manual.md §2.1 — user-facing mention, also stale -->
