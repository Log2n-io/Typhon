---
uid: feature-indexing-index
title: 'Indexing'
description: 'Concurrent B+Tree secondary indexes — automatically built and maintained per [Index]-tagged ComponentTable field, in four key-width-specialized variants…'
---

# Indexing
> Concurrent B+Tree secondary indexes — automatically built and maintained per `[Index]`-tagged `ComponentTable` field, in four key-width-specialized variants under a per-node optimistic-concurrency (OLC) protocol — back point lookups, ordered range scans, and (on `Versioned` components) MVCC-correct historical reconstruction of index membership over time. Primary-key access is no longer part of this surface: the PK B+Tree has been removed in favor of `EntityMap`/ECS entity APIs.

> 🔬 **Recommended:** read [in-depth-overview/03-indexing.md](../../in-depth-overview/03-indexing.md) (Chapter 03: Indexing) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Secondary Index Storage Modes](secondary-index-storage-modes/README.md) | An indexed field is either unique or `AllowMultiple`; the choice drives the on-disk value representation and which mutation API path is used | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Unique (Single-Value) Secondary Index](secondary-index-storage-modes/unique-secondary-index.md) | One key maps to exactly one entity — the B+Tree value is a chunk-id directly, no buffer indirection | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Multi-Value Secondary Index (AllowMultiple)](secondary-index-storage-modes/multi-value-secondary-index.md) | Many entities share one key — the B+Tree value is a growable HEAD buffer of chunk-ids, at a fixed +4-byte-per-entity cost | ✅ Implemented | 🔵 Core |
| [Lookup and Range-Scan Operations](lookup-and-range-scan.md) | Lock-free point lookups and ordered range scans over any secondary index, MVCC-correct at your transaction's snapshot | ✅ Implemented | 🔵 Core |
| [Index Handle Resolution (IndexRef)](index-ref-resolution.md) | Opaque, zero-allocation handle to a PK or secondary index, resolved once on the cold path via `GetPKIndexRef`/`GetIndexRef` and reused on the hot path with O(1) schema-evolution staleness checks | ✅ Implemented | 🟣 Advanced |
| [Versioned (HEAD/TAIL) Secondary Indexes for MVCC](versioned-secondary-indexes.md) | `AllowMultiple` indexes maintain a HEAD buffer (current set) plus an append-only TAIL of version transitions so index membership stays correct across updates and deletes | ✅ Implemented | 🟣 Advanced |
| [Transaction-Local Index Overlay (Read-Your-Own-Writes)](transaction-local-index-overlay.md) | Planned per-transaction overlay so index lookups see that transaction's own uncommitted writes | 📋 Planned | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [Specialized B+Tree Key-Size Variants](btree-key-variants.md) | Four key-width-specialized B+Tree implementations (16/32/64-bit and `String64`), automatically selected by an indexed field's CLR type | ✅ Implemented |
| [Compound Move/MoveValue (field-update fast path)](compound-move-operations.md) | Atomic remove+insert for indexed-field updates — one traversal, one lock on the common same-leaf case | ✅ Implemented |
| [Temporal (Point-in-Time) Index Query](temporal-index-query.md) | Reconstructs which entities held a key's value at a past TSN by replaying the index's append-only TAIL history | 🚧 Partial |
| [TAIL Retention / Garbage Collection](tail-garbage-collection.md) | Bounds TAIL version-history growth via boundary-sentinel-preserving pruning — built and tested, not yet auto-triggered | 🚧 Partial |
| [Optimistic Lock Coupling (per-node concurrency)](olc-concurrency.md) | Per-node OLC version latches give lock-free optimistic readers and leaf-only write latching for B+Tree/R-Tree index operations | ✅ Implemented |
| [Index Diagnostics & Consistency Checking](btree-diagnostics.md) | Always-on per-instance contention counters plus an on-demand `tsh` structural walk to diagnose B+Tree contention and validate integrity | ✅ Implemented |
| [B+Tree Node Layout and Capacity Tuning](btree-node-layout-tuning.md) | Cache-line-aware 256-byte B+Tree node layout with per-key-type capacities, tuned through a multi-phase profiling effort | ✅ Implemented |
| [Batched Index Maintenance for Bulk Commits](batched-index-maintenance.md) | Commit-path rework that batches secondary-index updates per commit; accessor-reuse has shipped, sorted-key application has not | 🚧 Partial |