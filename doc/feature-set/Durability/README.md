---
uid: feature-durability-index
title: 'Durability'
description: 'Crash-safe persistence for Typhon: a logical Write-Ahead Log (WAL v2) backs an append-before-publish commit pipeline and per-transaction durability…'
---

# Durability
> Crash-safe persistence for Typhon: a logical Write-Ahead Log (WAL v2) backs an append-before-publish commit pipeline and per-transaction durability controls, while a checkpoint pipeline consolidates dirty pages under CRC32C + seqlock protection and a recovery driver rebuilds — never repairs — derived structures from the durably-committed prefix. Every protocol claim is backed by invariant rules and TLA+ models, not tests alone; BulkLoad (throughput) and point-in-time backup (external recovery points) are opt-in paths layered on the same WAL.

> 🔬 **Recommended:** read [in-depth-overview/11-durability.md](../../in-depth-overview/11-durability.md) (Chapter 11: Durability) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Durability Modes](durability-modes/README.md) | Per-Unit-of-Work control over when WAL records become crash-safe — pick latency vs. data-at-risk per workload | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Committed Durability Discipline](durability-modes/committed-discipline.md) | Zero-loss, atomic writes on Typhon's cheapest component layout (`SingleVersion`) without paying for an MVCC revision chain | ✅ Implemented | 🟣 Advanced |
| [Write-Ahead Log (WAL v2 logical records)](wal-v2.md) | The single source of durability truth: logical `(EntityId, slot)` records, one codec, a sequential CRC-chained log | ✅ Implemented | 🟣 Advanced |
| [Commit Pipeline (append-before-publish)](commit-pipeline.md) | `Transaction.Commit`'s VALIDATE→PREPARE→BUILD→APPEND→PUBLISH→WAIT ordering guarantees nothing is visible before its WAL record is appended, and publish never rolls back | ✅ Implemented | 🟣 Advanced |
| [Checkpoint v2 (SnapshotStore pipeline)](checkpoint-v2/README.md) | Background pipeline that consolidates dirty data pages into the data file, advances `CheckpointLSN` only over pages it actually wrote, and recycles WAL segments | ✅ Implemented | 🟣 Advanced |
| [Crash Recovery (RecoveryDriver)](crash-recovery/README.md) | On open, scans the WAL's durably-committed prefix and replays it idempotently, in strict LSN order, through the engine's own write primitives | ✅ Implemented | 🟣 Advanced |
| [Page Checksums & Seqlock Snapshots](page-checksums-seqlock.md) | CRC32C torn-page detection on every page, paired with a lock-free seqlock so checkpoints snapshot live pages without blocking writers | ✅ Implemented | 🟣 Advanced |
| [BulkLoad Write Path](bulk-load.md) | An opt-in, exclusive, throughput-first session API that skips per-row WAL and brackets the whole bulk with a `BulkBegin`/`BulkEnd` manifest pair plus a synchronous checkpoint barrier | 🚧 Partial | 🟣 Advanced |
| [Durability Health & Introspection](durability-introspection.md) | `DurabilityHealth` (Ok/Degraded/Fatal) and checkpoint/WAL-writer cycle counters via the Resource Graph let an operator observe the subsystem without reaching into internals | ✅ Implemented | 🟣 Advanced |
| [Point-in-Time Incremental Backup](pit-backup.md) | Forward-incremental `.pack` backups scoped to changed pages; restore reassembles a base and heals it through crash recovery's `RecoveryDriver` | 📋 Planned | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [A/B Protected-Page Slot-Pairing](checkpoint-v2/protected-page-pairing.md) (part of [Checkpoint v2](checkpoint-v2/README.md)) | Doublewrite-free torn-write protection for the meta page and segment-directory pages that crash recovery can't re-derive | ✅ Implemented |
| [Rebuild of Derived Structures](crash-recovery/rebuild-derived-structures.md) (part of [Crash Recovery (RecoveryDriver)](crash-recovery/README.md)) | Indexes, EntityMap, and occupancy are never repaired after a crash — they're discarded and rebuilt wholesale from the recovered primary data | ✅ Implemented |
| [Formal Proofs & Invariant Rules](correctness-proofs.md) | Durability correctness is gated on falsifiable artifacts — invariant rules, TLA+ specs, and a crash-simulation sweep — enforced in CI | ✅ Implemented |