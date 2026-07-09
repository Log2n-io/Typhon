---
uid: feature-profiler-unmanaged-allocation-tracing
title: 'Unmanaged Memory Allocation Tracing'
description: 'See every native (unmanaged) allocation and free on the profiler timeline, tagged by subsystem.'
---

# Unmanaged Memory Allocation Tracing
> See every native (unmanaged) allocation and free on the profiler timeline, tagged by subsystem.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Typhon keeps hot-path data off the managed heap deliberately — page cache buffers, WAL staging buffers, the
transient store, and other large regions are allocated as native memory. That's good for GC pressure, but it
also makes native allocation churn invisible to normal .NET tooling: a leak or an unexpected size spike in
native memory won't show up in a heap snapshot. This feature puts every native allocate/free directly on the
profiler timeline, correlated with whatever engine activity triggered it, so you can see growth, churn, and
who's responsible without attaching a separate native memory profiler.

## ⚙️ How it works (in brief)

Every engine subsystem that needs native memory allocates it through one funnel: a pinned, aligned memory
block backed by `NativeMemory`. That funnel's construct and dispose paths each emit a `MemoryAllocEvent`
instant carrying the direction (alloc/free), a subsystem source tag, the size, and the running total after
the operation — one instrumentation point, 100% coverage of native traffic, no per-subsystem work needed.
Ordinary managed byte-array allocations are tracked separately and never move the native total. Arena/pool/
`ArrayPool`/stackalloc-site tracing is a follow-on effort — see Guarantees & limits.

## 💻 Usage

Nothing to call for Typhon's own internal allocations — flip the config flag and every native block the
engine creates is traced automatically:

```json
// typhon.telemetry.json next to your executable
{
  "Typhon": {
    "Telemetry": { "Enabled": true },
    "Profiler": {
      "Enabled": true,
      "MemoryAllocations": { "Enabled": true }
    }
  }
}
```

`IMemoryAllocator`/`PinnedMemoryBlock` are `internal` to `Typhon.Engine` — there is no public entry point for
application code to route its own native allocations through this funnel today; every traced allocation is one
the engine itself made on your behalf (page cache, WAL, transient store, etc.), not something host code opts
into.

| Option | Default | Effect |
|---|---|---|
| `Typhon:Profiler:MemoryAllocations:Enabled` | `false` | Emits a `MemoryAllocEvent` instant on every native alloc/free; populates the Memory — Unmanaged gauge track |

## ⚠️ Guarantees & limits

- **100% coverage of native memory traffic** — every subsystem allocates unmanaged memory through the same
  funnel, so this single instrumentation point sees all of it: page cache, WAL staging, WAL commit buffer,
  transient store, and anything else built on it.
- **Managed allocations are excluded by design** — ordinary `byte[]` array allocations are tracked in a
  separate running total and never inflate the native/unmanaged numbers; the two are kept visually distinct
  in the viewer.
- **Source-tagged** — each record carries a `u16` tag identifying which subsystem made the allocation (WAL
  staging, page cache, transient store, WAL commit buffer, or unattributed), so the viewer can colour-code and
  aggregate markers per subsystem.
- **Alloc/free are symmetric** — a free event carries the same source tag as its matching alloc, so per-tag
  totals reconcile correctly over the trace.
- **Independently gated** — its own config flag, separate from GC tracing and gauges; costs nothing when off
  (JIT-eliminated), and a few nanoseconds per alloc/free plus ~19 bytes on the wire when on.
- **Resolved once at startup** — like every profiler sub-flag, read at class load; editing the JSON after the
  process starts requires a restart to take effect.
- **Not yet covered**: arena/pool allocations, `ArrayPool<byte>` rentals — only the
  pinned-native-block funnel is instrumented today. Planned as a separate reserved event-kind range; won't
  change this feature's wire format.
- **Viewer**: markers appear on the Memory — Unmanaged gauge track alongside a running-total area, a
  dashed peak-reference line, and a live-blocks count.

## 🧪 Tests

- [MemoryAllocatorInstrumentationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Memory/MemoryAllocatorInstrumentationTests.cs) — pinned-byte/live-block counters, source-tag propagation, managed-vs-pinned accounting separation (the counter half the gauge track reads; the gated event-emit half is off by default in tests)

## 🔗 Related

- Sibling features: [GC Event Tracing](./gc-event-tracing.md), [Configuration & Performance Tuning](./profiler-configuration-tuning.md), [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md)
- Source: `src/Typhon.Engine/Foundation/Memory/internals/{MemoryAllocator,PinnedMemoryBlock,MemoryBlockBase}.cs`, `src/Typhon.Engine/Profiler/internals/InstantEvents.cs`, `src/Typhon.Profiler/MemoryAllocEnums.cs`

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §6.7, claude/design/Profiler/06-profiler-feature-roadmap.md §3.2 -->
<!-- User manual: claude/design/Profiler/profiler-user-manual.md §3.1, §5.3, §8.3 -->
