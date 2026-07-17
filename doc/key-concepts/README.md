---
uid: key-concepts-index
title: 'Key Concepts'
description: 'The vocabulary and mental models behind Typhon — one short page per concept, cross-linked, each pointing at its API reference and where it is taught. The isolation & durability cluster is the one most worth internalising early.'
---

# Key Concepts

The **vocabulary** of Typhon. Each page is one concept, kept short on purpose: what it *is*, how it relates to the others, the type(s) that back it in the API, and where to learn or look it up. Read them like a wiki — follow the links.

> 💡 **Concept vs feature.** A **concept** page answers *"what is this and how does it relate?"* (the mental model). The **[feature catalog](xref:feature-transactions-index)** answers *"how do I use it?"* (snippets, guarantees, limits). If you want the deep how-to, every concept links to its feature page; come here for the map, go there for the manual.

---

## Data model

The nouns you declare — the shape of your data.

| Concept | In one line |
|---|---|
| **[Component](xref:concept-component)** | A plain blittable struct of data — the atom of the model. |
| **[Component collections](xref:concept-component-collection)** | Per-entity variable-length lists — the one controlled exception to fixed-size. |
| **[Archetype](xref:concept-archetype)** | The fixed shape of an entity — its set of components. |
| **[Entity](xref:concept-entity)** | One instance of an archetype, identified by an `EntityId`. |
| **[EntityLink](xref:concept-entity-link)** | A typed, stored reference from one entity to another. |
| **[Index (secondary)](xref:concept-index)** | An `[Index]` field kept in a B+Tree for direct lookup. |
| **[Spatial index](xref:concept-spatial-index)** | A `[SpatialIndex]` box answering nearby / in-box / ray queries. |
| **[Schema evolution](xref:concept-schema-evolution)** | Changing a component later — versioned and migrated. |

## Isolation & durability

Typhon separates **visibility** (who sees a change) from **durability** (whether it survives a crash), and gives you three independent dials over them. This is the cluster most worth internalising early.

> 📖 **Start with the one-page overview:** the **[Isolation & durability cheat sheet](xref:guide-isolation-durability)** ties all of the below together — the two clocks, the three dials, the guarantee matrix, and the crash contract. The concept pages here are the atoms; the cheat sheet is the picture.

| Concept | In one line |
|---|---|
| **[Transaction](xref:concept-transaction)** | The unit of **isolation** — one writer, one snapshot, one atomic set of changes. |
| **[Unit of Work](xref:concept-unit-of-work)** | The unit of **durability** — groups transactions into one flush cycle. |
| **[Snapshot isolation](xref:concept-snapshot-isolation)** | Every read sees a consistent view frozen at the transaction's start. |
| **[Conflict resolution](xref:concept-conflict-resolution)** | The write side — how write-write conflicts resolve, and the handler that overrides last-writer-wins. |
| **[Storage mode](xref:concept-storage-mode)** | Per-component layout + ACID guarantees: `Versioned` / `SingleVersion` / `Transient`. |
| **[Durability — mode & discipline](xref:concept-durability)** | Two dials: *when* the WAL flushes, and *how* a SingleVersion write is made durable. |
| **[Tick fence](xref:concept-tick-fence)** | The per-tick durability boundary — SingleVersion writes durable by the end of the tick (≤ 1 tick loss). |
| **[Bulk load](xref:concept-bulk-load)** | An exclusive seeding/import path — skips per-row WAL for one session-wide durability barrier. |
| **[PointInTimeAccessor](xref:concept-point-in-time-accessor)** | A frozen *current* snapshot fanned across threads for parallel reads. Not time travel. |

## Querying & reactivity

Asking questions of your data, and keeping the answers current.

| Concept | In one line |
|---|---|
| **[Query](xref:concept-query)** | A one-shot question — by shape, field value, or geometry. |
| **[View](xref:concept-view)** | A query kept current, reporting Added / Removed / Modified deltas. |
| **[Subscription](xref:concept-subscription)** | A view published to remote clients over a transport. |

## Systems & the runtime

Running logic over your data, every tick, in parallel.

| Concept | In one line |
|---|---|
| **[Tick](xref:concept-tick)** | One iteration of the loop — a simulation step / game frame. |
| **[System](xref:concept-system)** | A unit of logic with declared data access. |
| **[TickContext](xref:concept-tick-context)** | What a system's `Execute` receives — transaction, accessor, entities, delta time, side-transactions. |
| **[Typhon runtime](xref:concept-runtime)** | The metronome + worker pool that runs systems each tick. |
| **[Scheduler & phases](xref:concept-scheduler)** | Derives a safe parallel execution graph from declared access. |
| **[Overload management](xref:concept-overload-management)** | Degrade under load instead of crashing — throttle, slow the tick, then signal game code to shed. |
| **[Spatial tiers & dispatch](xref:concept-spatial-tiers)** | Run near entities every tick, far ones at reduced/dormant rates — per cluster, entity-count-independent. |

## Reliability & operations

Running Typhon in production — how failures surface, how deadlines bound them, how you watch pressure build, and how you see inside a live engine.

| Concept | In one line |
|---|---|
| **[Errors & failures](xref:concept-errors)** | The exception hierarchy, error codes, `IsTransient`, and `Result<T>` — what to catch and what's retryable. |
| **[Deadlines & timeouts](xref:concept-deadlines-timeouts)** | Every operation is bounded by an absolute deadline; hangs surface as typed timeout exceptions. |
| **[Resources & budgets](xref:concept-resources)** | A live utilization map — the real-time data you need to throttle *before* you hit a budget. |
| **[Observability & telemetry](xref:concept-observability)** | Gated telemetry, distributed tracing, OTel metrics, and health checks — seeing inside a live engine. |
| **[Profiler](xref:concept-profiler)** | The engine's typed-event profiler — ~25–50 ns capture, exported to a trace file or live to the Workbench. |

## Engine & storage

The engine object and the machinery underneath it.

| Concept | In one line |
|---|---|
| **[DatabaseEngine](xref:concept-database-engine)** | The root object — one per process — that owns everything. |
| **[Page cache & paged store](xref:concept-page-cache)** | Memory-mapped storage; working set resident, the rest on disk. |
| **[Cluster storage](xref:concept-cluster-storage)** | Batched SoA layout — your storage-mode mix silently decides it (~50× bulk iteration). |
| **[WAL & checkpoint](xref:concept-wal-checkpoint)** | The log that makes commits durable; the checkpoint recycles it. |

---

These concepts map onto the [user guide](xref:guide-index): the data model is [ch.1–2](xref:guide-modeling), isolation & durability [ch.3](xref:guide-transactions), querying [ch.4](xref:guide-querying), systems & runtime [ch.5](xref:guide-systems), and reliability & operations [ch.6](xref:guide-operating). Come here for the map; follow a concept into the guide for the how-to.
