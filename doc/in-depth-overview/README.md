# Typhon — In-Depth Overview

This series is the **architectural reference** for the Typhon engine: how it's structured, what each subsystem does, how the pieces fit together. It's aimed at two audiences:

- **Engine adopters** — you're embedding Typhon in your application and want to reason about durability, MVCC, querying, the runtime model, and what the engine guarantees vs leaves to you.
- **New contributors** — you're about to read or modify engine code and want a map of the territory before opening the first `.cs` file.

The series is *in-depth* in the sense that it explains mechanism (structures, lifecycles, invariants) rather than skimming. It's not API reference documentation (that lives in the source); it's the **why** behind the API and the **what** behind the names.

> **What Typhon is.** A real-time, low-latency ACID database engine with microsecond-level commit targets. Uses an ECS data model (entities, archetypes, components), MVCC snapshot isolation for reads, a write-ahead log + checkpoint loop for durability, and a tick-driven scheduler for coordinated parallelism. In-process — Typhon ships as a .NET library, not a server. One `DatabaseEngine` per process.

> **What Typhon isn't.** Not SQL. Not a networked database. Not a key-value store. Not a queue. No built-in replication. No multi-process clustering. The engine focuses on extracting maximum throughput from a single machine.

---

## The chapters

The series follows the engine's folder layout — every `src/Typhon.Engine/<Folder>/` maps to exactly one chapter (see [§4 Folder ↔ chapter invariant](#4-folder--chapter-invariant)). Numbering reflects suggested reading order for newcomers, not strict dependency order.

| # | Chapter | Covers |
|---|---|---|
| **01** | [Foundation](01-foundation.md) | Synchronization primitives (AccessControl, Deadline, WaitContext, AdaptiveWaiter), epoch-based reclamation, false-sharing avoidance, concurrent collections, the memory allocator, hosting helpers. The pile of primitives every other subsystem stands on. |
| **02** | [Storage](02-storage.md) | The paged memory-mapped file (PagedMMF) and its accessors. Page cache, clock-sweep eviction, segments (`LogicalSegment`, `ChunkBasedSegment`), `ChunkAccessor` SOA layout, dirty tracking via ChangeSet, backpressure, FPI capture, seqlock writes. |
| **03** | [Indexing](03-indexing.md) | B+Tree as the universal index. Node layout, capacity variants (L16/L32/L64/String64), the two-phase `SpinWriteLock`, `OlcLatch` for optimistic reads, structural mutations (split/merge), multi-tree segments. |
| **04** | [Schema](04-schema.md) | Component and field definitions. The `FieldType` enum (including AABB/BSphere/Unsigned/DoubleFloat flags), persistence via `ComponentR1` / `FieldR1` / `ArchetypeR1` / `SchemaHistoryR1` system entities, schema evolution (eager migration on reopen), the diff model. |
| **05** | [Revision (MVCC)](05-revision.md) | How snapshot isolation actually works. `CompRevStorageElement` layout (12 B with packed TSN + UowId + IsolationFlag), revision chains, write-time UowId stamping, the snapshot read walk, `EnabledBits` overrides. |
| **06** | [ECS](06-ecs.md) | The API users actually touch. `EntityId`, `Comp<T>`, `Archetype<TSelf>` declaration, `Spawn`/`Destroy`/`Open`/`OpenMut`, `EntityRef`, `EntityLink<T>`, `PointInTimeAccessor` for parallel reads, cluster storage, the three storage modes (Versioned/SingleVersion/Transient). |
| **07** | [Spatial](07-spatial.md) | Per-archetype broad-phase grid + per-component R-Tree for AABB/radius/ray queries. Spatial maintainer, geometric primitives, the `[SpatialIndex]` attribute, query operators. |
| **08** | [Transactions](08-transactions.md) | `UnitOfWork`, `Transaction`, the TransactionChain (singly-linked, CAS PushHead), `UowRegistry`, durability modes (Deferred/GroupCommit/Immediate), deferred cleanup, deadlines. The mutation entry point. |
| **09** | [Querying](09-querying.md) | `EcsQuery`, DNF predicate parsing, plan building, the pipeline executor, the view system (`EcsView`, `ViewDeltaRingBuffer`, delta computation), statistics (HLL/MCV/Histogram), selectivity estimation. Plus a brief Subscriptions section. |
| **10** | [Runtime](10-runtime.md) | The scheduler. TickDriver, tracks (Engine-Pre / Public / Engine-Post), DAG construction from access patterns, worker threads, the parallel fence, overload management. |
| **11** | [Durability](11-durability.md) | WAL writer (group commit), wire format (chunk types, record header, FPI), checkpoint pipeline (8 steps), recovery (7 phases), the UoW state machine, fail-fast (per ADR). |
| **12** | [Observability](12-observability.md) | Zero-overhead typed event pipeline (`TyphonEvent.Begin*`/`Emit*`), gate flags (`TelemetryConfig`), the ~217 event kinds, source location attribution, wire protocol, profiler engine pipeline, Workbench viewer, OTel integration. |
| **13** | [Resources](13-resources.md) | The resource graph — every long-lived engine object as `IResource`. Metrics (Memory/Capacity/DiskIO/Throughput/Duration), snapshots, alerts, configuration (`ResourceOptions`), exhaustion policies. |
| **14** | [Errors](14-errors.md) | The exception hierarchy, error codes, the `Result<TValue,TStatus>` zero-cost pattern, status enums, the throw-don't-retry philosophy. |

Each chapter is self-contained. Cross-references between chapters are marked `[NN-name](NN-name.md)` and only point where the next-step detail genuinely lives.

---

## 1. Suggested reading paths

The chapters are numbered, but you don't have to read them linearly. Pick a path that matches what you're trying to do:

### "I'm new to Typhon. What is it?"

Read in order: **README → 06 (ECS) → 08 (Transactions) → 11 (Durability) → 09 (Querying)**.

That gives you: data model → how mutations work → what survives a crash → how reads happen. You can come back to 01/02/03/05/10 when you need to reason about the lower layers.

### "I'm building on Typhon and need to make a design decision."

- Tuning latency vs throughput → **08** (durability modes) + **11** (commit path) + **10** (tick rate)
- Designing a schema → **04** (component model) + **06** (archetypes) + **07** (if spatial)
- Adding observability → **12** (gate flags, event kinds) + **13** (metric/resource model)
- Reasoning about cost → **05** (MVCC cost) + **02** (page cache pressure) + **10** (worker model)

### "I'm reading or modifying engine code."

Start at the bottom: **01 → 02 → 03 → 05 → 06**. The lower-layer primitives explain everything above them. Once you can read those without confusion, the upper chapters (08, 09, 10, 11, 12) read like application code.

### "I just need to find one specific thing."

Use the table above as an index. Each chapter's section headers in its own ToC tell you whether what you want is there.

---

## 2. Layering & dependency direction

Typhon's layering is mostly strict: lower layers don't know about upper layers. Cross-cutting concerns (Observability, Resources, Errors) thread through everything.

<a href="assets/typhon-architecture-layers.svg">
  <img src="assets/typhon-architecture-layers.svg" width="1101" alt="Typhon architecture layers">
</a>
<br>
<sub>The full layer stack (high → low): Apps → Subscriptions/Runtime → Querying (← Spatial) → Transactions → ECS → Revision/Schema → Indexing → Storage → Foundation, with representative components per layer. Durability runs parallel (commit → WAL, checkpoint → storage); Observability / Resources / Errors thread through all layers; Hosting and Profiler are satellites.</sub>

<a href="assets/typhon-dependency-flow.svg">
  <img src="assets/typhon-dependency-flow.svg" width="1069" alt="Dependency flow">
</a>
<br>
<sub>The subsystem dependency spine (downward = depends on): Spatial feeds Querying, Schema sits beside ECS, Durability hangs off Transactions, cross-cutting concerns instrument every layer, and Hosting/Profiler are satellites outside the spine.</sub>

`Hosting` (DI service registration) and `Profiler` sit outside this spine: Hosting wires everything up at startup; Profiler is a satellite of Observability that reads ring buffers and writes trace files.

---

## 3. Glossary

Terms that show up across multiple chapters. Each entry points to where the type lives (or to the chapter that introduces it).

| Term | What it is | Lives in |
|---|---|---|
| **Archetype** | A typed shape — the fixed set of components an entity has | [06-ecs](06-ecs.md) |
| **`AccessControl` / `AccessControlSmall` / `ResourceAccessControl`** | The three reader/writer-style synchronization primitives | [01-foundation](01-foundation.md) |
| **`AdaptiveWaiter`** | Zero-alloc spin/yield/sleep wrapper around `SpinWait` | [01-foundation](01-foundation.md) |
| **B+Tree** | The universal index implementation (L16/L32/L64/String64 variants) | [03-indexing](03-indexing.md) |
| **ChangeSet** | Per-transaction dirty-page accumulator; gates checkpoint visibility | [02-storage](02-storage.md), [08-transactions](08-transactions.md) |
| **Checkpoint** | The background process that pushes dirty pages to the data file and advances the durable LSN | [11-durability](11-durability.md) |
| **Cluster** | A 8–64-entity group stored Structure-of-Arrays within one chunk | [06-ecs](06-ecs.md) |
| **`Comp<T>`** | A typed component handle, declared via `Archetype.Register<T>()` | [06-ecs](06-ecs.md) |
| **`CompRevStorageElement`** | The 12-byte revision element — TSN + UowId + IsolationFlag | [05-revision](05-revision.md) |
| **DAG** | The directed acyclic graph of systems, edges derived from access patterns | [10-runtime](10-runtime.md) |
| **`Deadline`** | Monotonic absolute timeout (8 B struct) | [01-foundation](01-foundation.md) |
| **DurabilityMode** | `Deferred` / `GroupCommit` / `Immediate` — per-UoW persistence policy | [08-transactions](08-transactions.md), [11-durability](11-durability.md) |
| **`EntityId`** | 64-bit ID: 52-bit monotonic key + 12-bit ArchetypeId | [06-ecs](06-ecs.md) |
| **`EntityLink<T>`** | Typed entity reference (polymorphic over archetype hierarchy) | [06-ecs](06-ecs.md) |
| **`EntityRef`** | `ref struct` working handle returned by `Open`/`OpenMut` | [06-ecs](06-ecs.md) |
| **Epoch / `EpochGuard`** | Per-thread page protection — pages tagged ≥ MinActiveEpoch can't be evicted | [01-foundation](01-foundation.md) |
| **FPI** | Full Page Image — the WAL-resident copy of a page captured at first modification per checkpoint cycle | [11-durability](11-durability.md), [02-storage](02-storage.md) |
| **LSN** | Log Sequence Number — monotonic position in the WAL stream | [11-durability](11-durability.md) |
| **MVCC** | Multi-Version Concurrency Control — Typhon's snapshot isolation model | [05-revision](05-revision.md) |
| **`OlcLatch`** | Optimistic lock-coupling latch (32 bits) used by B+Tree readers | [01-foundation](01-foundation.md), [03-indexing](03-indexing.md) |
| **`PointInTimeAccessor`** (PTA) | Parallel read accessor — one frozen TSN snapshot across N workers | [06-ecs](06-ecs.md) |
| **Snapshot / TSN** | A logical timestamp; a Transaction's `TSN` is its snapshot point | [05-revision](05-revision.md), [08-transactions](08-transactions.md) |
| **Storage mode** | Per-component policy: `Versioned` (MVCC), `SingleVersion`, `Transient` (in-memory only) | [06-ecs](06-ecs.md), [04-schema](04-schema.md) |
| **Track** | One of `Engine-Pre`, `Public`, `Engine-Post` — top-level DAG container | [10-runtime](10-runtime.md) |
| **Transaction** | Single-thread-affine mutation context; one per System per tick | [08-transactions](08-transactions.md) |
| **UnitOfWork (UoW)** | Durability boundary — wraps N Transactions, decides when/how to persist | [08-transactions](08-transactions.md) |
| **`UnitOfWorkContext`** | 24 B execution context flowing through every UoW operation (WaitContext + UowId + holdoff) | [01-foundation](01-foundation.md) |
| **UowId** | 15-bit identifier allocated by `UowRegistry`, stamped on every revision element written by that UoW | [05-revision](05-revision.md), [08-transactions](08-transactions.md) |
| **`WaitContext`** | 16 B value carrying `Deadline` + `CancellationToken`; passed `ref` to every blocking primitive | [01-foundation](01-foundation.md) |
| **WAL** | Write-Ahead Log — durability backbone | [11-durability](11-durability.md) |

<a href="assets/typhon-glossary-core-relationships.svg">
  <img src="assets/typhon-glossary-core-relationships.svg" width="1200" alt="Core type relationships">
</a>
<br>
<sub>How the core types relate across six layers — Execution, ECS (Archetype / EntityId / Comp&lt;T&gt; / EntityRef / PointInTimeAccessor), Data &amp; MVCC (ComponentTable / EntityMap / revision chains / B+Tree / TSN), Storage, Durability, and Concurrency (AccessControl(Small) / ResourceAccessControl / EpochGuard / AdaptiveWaiter / WaitContext / Deadline).</sub>

---

## 4. Folder ↔ chapter invariant

Every chapter in this series maps to **exactly one** `src/Typhon.Engine/<Folder>/`. Two exceptions, declared up-front:

- **01-foundation** also covers a few small helpers from `src/Typhon.Engine/Hosting/` (§9), which doesn't have enough mass to justify its own chapter, and points to sibling-project types in `src/Typhon.Schema.Definition/` users may encounter.
- **12-observability** merges `src/Typhon.Engine/Observability/` and `src/Typhon.Engine/Profiler/` because the typed event pipeline they implement is one cohesive story.

`Subscriptions/` gets a short section inside [09-querying](09-querying.md) for now; it will graduate to its own chapter when it grows beyond the streaming-result-set surface.

This rule is load-bearing: it's the structural decision that lets us catch documentation drift mechanically. When you `git mv` a folder, the chapter moves with it in the same commit. When a type is deleted, the chapter that *owns* it is the one to update. Internal-only details (file:line citations, implementation classes that aren't part of the public surface) are deliberately kept out — those rot fastest. Where a piece of context is invariant (a bit layout, a constant, an invariant of an algorithm), we document it; where it's incidental, we link to the source.

---

## 5. Public vs internal API discipline

Typhon enforces a strict separation between **public API** (callable by application code) and **internal API** (engine-only). Folders carry the discipline in their layout:

```
src/Typhon.Engine/<Folder>/
├── public/      ← public types — stable contract, breaking changes are versioned
└── internals/   ← internal types — engine private, free to change
```

- `public/` types live in the `Typhon.Engine` namespace.
- `internals/` types live in the `Typhon.Engine.Internals` namespace.
- The [`TYPHON008` Roslyn analyzer](../../src/Typhon.Analyzers/InternalApiLeakAnalyzer.cs) enforces that public-surface types don't leak `internals/` types in their signatures (parameters, return types, generic constraints, etc.).

When this series links to source files, the URL tells you instantly which side of the line you're looking at. A user of the engine should be able to reason about everything in `public/`; an engine contributor will care about both.

A handful of folders deviate from the split (`Foundation/Collections/`, `Foundation/Memory/`, `Resources/`) — these are inherently internal or were structured before the convention was codified. Where this matters, the relevant chapter notes it.

---

## 6. About diagrams

Diagrams are authored in [D2](https://d2lang.com/) and rendered to SVG. Rendered SVGs live in this folder's `assets/` directory.

A diagram embedded with `⚠` in its caption indicates known drift — the diagram is currently inaccurate in a specific way that the caption calls out. Treat the prose as authoritative until the diagram refresh lands; the SVG is included so you can compare its structure (which is usually still useful) to what the text says.

All diagrams are currently in sync with the prose. If you spot a discrepancy not flagged in a caption, please open an issue.

---

## 7. Contributing to these docs

The chapters live in the engine repository (this folder) and follow the repository's normal review process — PRs welcome. Style guidelines:

- **Cite the code.** A factual claim about Typhon should have a markdown link to the file it comes from. Folder-level links are usually enough; file-level for type-specific details.
- **Explain *why*, not just *what*.** The code shows what. The doc earns its keep by explaining the trade-off, the invariant, or the design pressure that produced the code.
- **Keep examples honest.** If a code snippet won't compile against the current engine, the snippet is wrong. Don't paste pseudo-API.
- **Match the folder.** A change to a chapter's content should reflect a change to the corresponding `src/Typhon.Engine/<Folder>/`. If you find yourself documenting something that has no code, you're probably writing a design doc — that belongs elsewhere.

Drift happens. The best defence is small, frequent updates in the same PR as the code change. The worst is letting a chapter ossify until it's wrong about half its content.
