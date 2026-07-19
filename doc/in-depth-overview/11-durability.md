---
uid: overview-durability
title: '11 — Durability'
description: 'Durability is what makes Typhon ACID''s "D". The contract is the usual one: once Commit() returns under a durable mode, the change survives a process or…'
---

# 11 — Durability

**Code:** [`src/Typhon.Engine/Durability/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Durability)

Durability is what makes Typhon ACID's "D". The contract is the usual one: once `Commit()` returns under a durable mode, the change survives a process or machine crash. Two cooperating pipelines deliver it — a **Write-Ahead Log** that hardens transaction effects into a sequential journal at commit time, and a **Checkpoint** that periodically writes dirty data pages to the main data file and recycles the WAL.

Both pipelines run on dedicated background threads. Commit-time work on the application thread is minimal: serialize the records into a ring buffer, optionally wait for the WAL writer to confirm durability, return. Page writes are deferred — they're a background activity decoupled from the commit path.

> **This chapter describes the "Minimal WAL" redesign (v2).** The WAL now carries **logical** records — `(EntityId, slot)` (the per-archetype component slot under the EntityId's routing id) and a value, never pages or chunk ids — written by a single `RecordCodec`, and recovery re-applies them through the engine's own write primitives (`RecoveryApplier`) and then **rebuilds** derived structures instead of repairing pages. Full-Page Images are gone. The full design lives in `claude/design/Durability/MinimalWal/`; correctness is gated on invariant rules (`claude/rules/durability.md`), a crash-sim sweep, and TLA+ specs.
>
> **Transitional note:** the v1 **persisted UoW Registry** and the v1 `WalRecovery` scan still exist and run alongside the v2 path (§7, §8). Commit *fate* for logical records is already decided by the WAL commit marker, not the registry, so the registry is redundant for fate — but its removal is an independent, still-pending cleanup (not part of the now-shipped Committed discipline).

This doc covers the WAL (writer, segments, wire format), the checkpoint (v2 cycle, staging pool, A/B meta-pair), recovery (`RecoveryDriver` + the surviving v1 scan), torn-page safety without FPI, and the durability invariants that hold across all of them.

<a href="assets/typhon-durability-overview.svg">
  <img src="assets/typhon-durability-overview.svg" width="1200" alt="Durability subsystem overview">
</a>

---

## 1. Overview

The durability layer sits between transactions (which produce changes) and storage (which holds them):

- **WAL** — every committed mutation is serialized into a sequential journal *before* its in-memory effect is considered durable. Records are **logical**: a record says *"entity E's component C now has these bytes"*, never *"page P chunk K"*. The journal is partitioned into fixed-size segment files on disk.
- **Checkpoint** — periodically captures dirty pages from the page cache into the data file, fsyncs, advances `CheckpointLSN` (persisted in an A/B meta-pair), and deletes WAL segments whose records are all below that point.
- **Recovery** — on restart, replays the committed WAL records above the last `CheckpointLSN` through the engine's own write paths, then scrubs revision chains and rebuilds every derived structure.

### Fail-fast on WAL write error (per ADR)

A WAL write I/O failure is *not* recoverable in-place. The writer catches the exception, latches it, and every subsequent attempt to wait for durability throws [`WalWriteException`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Errors/public/WalWriteException.cs) (`IsTransient = false`). There is no retry, no degraded mode, no partial-commit window — the engine refuses further durable commits until restart. This is the entire mechanism: a single sticky `_fatalError` field on [`WalWriter`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalWriter.cs) propagated through `WaitForDurable`.

The rationale (per ADR) is that any half-broken WAL is worse than a stopped engine: it can silently produce phantom commits, corrupt the LSN chain, or hide further failures. Fail-fast keeps the contract simple — either the WAL is healthy or the engine is down.

### Pipeline-level invariants

```
CheckpointLSN ≤ DurableLSN ≤ CurrentLSN
```

- `CurrentLSN` — the highest LSN allocated by the commit buffer (may not be written yet).
- `DurableLSN` — the highest LSN durably on disk via `fsync` / FUA. Honest by construction (LOG-05): the drain records each slot's `LastLsn` at publish, so the watermark never exceeds what is physically fsynced.
- `CheckpointLSN` — the highest LSN whose effects are consolidated into the data file.

WAL segments whose `LastLSN < CheckpointLSN` can be deleted. Recovery only needs records above `CheckpointLSN`.

---

## 2. WAL writer

[`Durability/internals/WalWriter.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalWriter.cs), [`WalManager.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalManager.cs), [`WalCommitBuffer.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalCommitBuffer.cs)

The WAL writer is a single dedicated OS thread:

| Property | Value |
|---|---|
| Thread name | `Typhon-WAL-Writer` |
| Priority | `ThreadPriority.AboveNormal` |
| Background | true |

It is the single consumer of an MPSC (multi-producer, single-consumer) commit buffer ([`WalCommitBuffer`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalCommitBuffer.cs)). Application threads commit a transaction by claiming space via an atomic tail-increment (`TryClaim` → `Interlocked.Add`, which maps to `LOCK XADD` on x64), writing the record batch into the claimed span, then publishing a frame. The writer drains published frames, copies them into a 4096-byte-aligned staging buffer, patches the chunk CRC chain over the **whole drained batch at once**, and writes that buffer to the active segment file with `RandomAccess.Write`. (Patching the entire batch in one shot is what fixed a v1 bug where a chunk straddling a 256 KB write-slice boundary could be left with a zero footer CRC.)

The transport — MPSC buffer, dedicated writer thread, segment management, FUA I/O — is **unchanged from v1**. Only the record *format* (§3) and the recovery/checkpoint logic above it (§5, §7) were redesigned.

### Ring buffer sizing

Default is **8 MB total** (`ResourceOptions.WalRingBufferSizeBytes = 8 * 1024 * 1024`). The buffer is split into two halves of 4 MB each — producers fill one half while the writer drains the other (Aeron-style ping-pong, per ADR). When the active half fills up, producers wait for the writer to swap.

### GroupCommit (default mode)

[`WalWriterOptions.GroupCommitIntervalMs = 5`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/public/WalWriterOptions.cs) — the WAL writer auto-flushes the staging buffer every 5 ms when running under [`DurabilityMode.GroupCommit`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/DurabilityMode.cs). Commit latency is ~1-2 µs (the producer doesn't block), and data-at-risk is bounded by the GroupCommit interval.

### Three durability modes

[`DurabilityMode`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/DurabilityMode.cs) is specified per **UnitOfWork**, not per transaction:

| Mode | Commit latency | Data-at-risk | Use case |
|---|---|---|---|
| `Deferred` | ~1-2 µs | Until explicit `Flush()` | Game ticks, batch imports |
| `GroupCommit` | ~1-2 µs | ≤ 5 ms (interval) | General server workload |
| `Immediate` | ~15-85 µs | Zero | Financial trades |

Under `Immediate`, the commit path calls `WalManager.RequestFlush()` and then blocks in `WalManager.WaitForDurable(highLsn, ref ctx)` until the LSN is on stable media.

### `DurabilityOverride`

[`DurabilityOverride`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/DurabilityMode.cs) is a `Default`/`Immediate` enum declared for a *planned* per-transaction escalation knob (ADR-005), but it is **not wired into the engine** — there is no `tx.Commit(DurabilityOverride.Immediate)` overload; `Transaction.Commit` has no `DurabilityOverride` parameter in either overload (`Commit(ref UnitOfWorkContext, ConcurrencyConflictHandler)` and the convenience `Commit(ConcurrencyConflictHandler)`). To give one critical operation zero-loss durability inside an otherwise-`Deferred`/`GroupCommit` batch today, commit it through its own `DurabilityMode.Immediate` UoW — `dbe.CreateQuickTransaction(DurabilityMode.Immediate)`, or `ctx.CreateSideTransaction(DurabilityMode.Immediate)` from a scheduled system.

### `DurabilityDiscipline` (separate enum — not an extension of `DurabilityOverride`)

[`DurabilityDiscipline`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/DurabilityDiscipline.cs) is a **distinct enum** — `TickFence` (default) and `Commit` — selecting the per-component *durability discipline* for a **SingleVersion**-layout component. It is **not** a new `DurabilityOverride` value and **not** a new `StorageMode`: it is an orthogonal axis layered on the existing per-UoW timing knob. `TickFence` keeps the default in-place, last-writer-wins, tick-fence-batched behavior (≤1-tick loss). `Commit` stages writes per transaction and makes them atomic + zero-loss durable at `Transaction.Commit` via a logical-redo WAL record, then publishes in place — read-committed, O(1) rollback, **no revision chain**. It applies only to SingleVersion (Versioned is always commit-scoped; Transient is never durable). Authoritative spec: `claude/design/Ecs/committed-storage-mode.md`.

---

## 3. Wire format

Every WAL chunk has the same envelope:

```
┌────────────────────┬──────────────┬─────────────────┐
│ WalChunkHeader 8 B │  Body (var)  │ WalChunkFooter  │
│  Type/Size/PrevCRC │              │     CRC 4 B     │
└────────────────────┴──────────────┴─────────────────┘
```

[`WalChunkHeader`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalChunkHeader.cs) (8 bytes, `Pack = 1`):

| Field | Type | Notes |
|---|---|---|
| `ChunkType` | `ushort` | Discriminator — see chunk types below |
| `ChunkSize` | `ushort` | Header (8) + body + footer (4); enables forward-compat skipping |
| `PrevCRC` | `uint` | Footer CRC of the previous chunk — patched by the writer thread |

`WalChunkFooter` is just a `uint CRC` (CRC32C over `[0, ChunkSize - 4)`, i.e. header + body excluding the footer). Producers write 0 placeholders for `PrevCRC` and the footer; the single-threaded writer patches both during the staging-buffer copy. Centralizing CRC chain management on the writer thread keeps the chain intact regardless of how records interleave. On recovery, the scan truncates at the first chunk whose CRC fails to validate (LOG-03, torn-tail truncation).

### Chunk types

[`WalChunkType`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalChunkHeader.cs):

| Value | Type | Body |
|---|---|---|
| `1` | `Transaction` | one or more logical records (a `RecordBatch`) — see §3.1 |
| `2` | *(gap)* | **retired** — was `FullPageImage`. Left as a gap so old segments with FPI chunks are skipped (unknown type) rather than mis-parsed |
| `3` | `TickFence` | `TickFenceHeader (24 B)` + N entries of `(ChunkId:4 B, ComponentData:PayloadStride B)` |
| `4` | `ClusterTickFence` | `ClusterTickFenceHeader (24 B)` + N entries of `(EntityIndex:4 B, AllComponentData)` |
| `5` | `BulkBegin` | BulkLoad session begin manifest |
| `6` | `BulkEnd` | BulkLoad session end manifest |

TickFence and ClusterTickFence are SingleVersion / cluster-storage recovery chunks emitted at tick boundaries — see [06-ecs §8](06-ecs.md) for the storage-mode story. BulkBegin/BulkEnd bracket an opt-in [BulkLoad](#) session (see [02-storage](02-storage.md) and the BulkLoad design).

### 3.1 Logical records (`RecordCodec` / `RecordFormat`)

[`RecordFormat.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/RecordFormat.cs), [`RecordCodec.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/RecordCodec.cs)

A `Transaction` chunk's body is a **batch of logical records**, serialized by the single `RecordCodec` (the *only* module allowed to read/write WAL record bytes). Each record begins with a 24-byte common header:

| Field | Type | Notes |
|---|---|---|
| `LSN` | `long` | Monotonic, globally unique |
| `TSN` | `long` | MVCC snapshot timestamp of the committing transaction |
| `UowEpoch` | `ushort` | Diagnostic UoW id stamped on every record |
| `RecordKind` | `byte` | See kinds below |
| `Flags` | `byte` | See flags below |
| `BodyLength` | `uint` | Kind-specific body bytes after this header |

After the header, the body carries the **logical address** (`EntityId : long`, plus the per-archetype component **slot** — 0..15 — under the EntityId's routing id) plus kind-specific data — never a page index, chunk id, or buffer handle (LOG-06: collection-handle byte ranges are explicitly zeroed before they reach the log).

**Record kinds** ([`RecordKind`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/RecordFormat.cs)):

| Value | Kind | Carries | Apply (idempotent) |
|---|---|---|---|
| `1` | `Slot` | EntityId, slot, payload | value overwrite (Versioned HEAD-only) |
| `2` | `Lifecycle` | EntityId + spawn / destroy / set-enabled-bits | spawn-if-absent / destroy-if-present / absolute mask |
| `3` | `CollectionDelta` | EntityId, slot, FieldId, op, index, element | folded, then applied as a `Set` |
| `4` | `BulkManifest` | sessionId, begin LSN, entity/component counts | orphan detection only |

**Record flags** ([`RecordFlags`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/RecordFormat.cs)):

- `TxBegin` — first record of a transaction's batch.
- `TxCommit` — **the commit marker (LOG-04)**. Set on the last record of the batch. A one-record batch carries both.
- `FenceRecord` — a tick-fence snapshot record (committed individually, no Tx markers).
- `Committed` — Committed-discipline marker. Tags records produced under `DurabilityDiscipline.Commit`; per rule CM-06, a Commit-discipline spawn WAL-logs its SingleVersion values (a `Slot` upsert per spawn value) so a cluster all-SV archetype recovers exactly across a crash with no checkpoint.

The batch is built by [`CommitBatchBuilder`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/CommitBatchBuilder.cs), which buckets entries by category so the codec always emits them in **LOG-07 order** (Spawn → Slot/CollectionDelta → Destroy/SetEnabledBits → BulkManifest) — a mis-ordered batch is unconstructible by API shape, so a `Slot` can never arrive before its entity's `Spawn`.

> **`WalRecordHeader.cs` is legacy.** The 32-byte `WalRecordHeader` struct from v1 still exists in the tree but is **not** the logical-record format — `RecordFormat.RecordHeader` (24 B) is what `RecordCodec` reads and writes.

---

## 4. Segment management

[`WalSegmentManager`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalSegmentManager.cs)

WAL records are written into fixed-size segment files named `{segmentId:D16}.wal` in the configured WAL directory (default `wal/`). Each segment starts with a 4096-byte `WalSegmentHeader` ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalSegmentHeader.cs)) carrying magic (`TYFW`), version, segment ID, first/prev LSN, and a CRC32C — sized for one aligned disk page so the first record sits at a 4096-byte boundary (required for `O_DIRECT` / `FILE_FLAG_NO_BUFFERING`).

### Defaults

| Knob | Default | Source |
|---|---|---|
| Segment size | 64 MB | `WalWriterOptions.SegmentSize` |
| Pre-allocated segments | 4 | `WalWriterOptions.PreAllocateSegments` |
| Rotation threshold | 75 % utilization | `WalWriter.RotationThreshold` constant |

When the active segment passes 75 % utilization, the writer seals it, opens the next pre-allocated segment, writes its header, and replenishes the pre-allocation pool. Pre-allocation creates new empty files of full segment size via `RandomAccess.SetLength` so rotation doesn't pay metadata-write latency on the hot path.

### Segments are deleted, not "recycled"

`WalSegmentManager.MarkReclaimable(checkpointLSN)` walks the sealed-segment list and calls `_fileIO.Delete(path)` for every segment whose `LastLSN < checkpointLSN`. There is no rename, no reuse. Pre-allocation creates fresh files on demand. The sealed-segment list is accessed only under its lock (writer, checkpoint, readers — CK-07).

### File flags by platform

[`WalFileIO.OpenSegment`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalFileIO.cs):

- **Windows** — `FILE_FLAG_NO_BUFFERING` bypasses the OS page cache; FUA (`FILE_FLAG_WRITE_THROUGH`) adds per-write durability.
- **Linux / macOS** — `NoBuffering` is omitted; durability relies on `FileOptions.WriteThrough` (FUA on supporting hardware) plus explicit `RandomAccess.FlushToDisk` (`fsync`/`fdatasync`).

`IWalFileIO` is the internal I/O seam; tests substitute [`InMemoryWalFileIO`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/InMemoryWalFileIO.cs) to run the full pipeline without disk (the supported "no disk" mode — there is no no-WAL mode, ADR-054).

---

## 5. Checkpoint (v2)

[`Durability/internals/CheckpointManager.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/CheckpointManager.cs)

The checkpoint is a single dedicated OS thread:

| Property | Value |
|---|---|
| Thread name | `Typhon-Checkpoint` |
| Priority | `ThreadPriority.Normal` |
| Background | true |

It wakes on a `ManualResetEventSlim` either at the configured interval (default `CheckpointIntervalMs = 30000`), on `ForceCheckpoint`, or at shutdown. The page cache's backpressure callback (`MMF.OnBackpressure`) also forces a cycle when there are no clean pages to evict.

### The cycle (`RunCheckpointCycle`)

The v2 cycle never persists never-durable bytes (CK-02) and never advances past a page it failed to capture (CK-03):

| Step | Action | Rule |
|---|---|---|
| 1 | **Barrier** — flush the WAL and capture `barrierLsn = DurableLsn`, the durable frontier for this cycle. | CK-01 |
| 2 | **Collect dirty pages** — `CollectDirtyMemPageIndices()` returns the cache slots with DirtyCounter > 0. | |
| 3 | **Capture + write (coverage passes)** — for each page: seqlock-snapshot into a staging buffer (CRC stamped on the copy), **skip** a page with a writer in flight (ACW > 0); `flush2` the WAL through the just-captured high-water LSN *before* the data fsync; write captured copies → data file → fsync; decrement DirtyCounter for written pages. Skipped pages are retried for up to `MaxCoveragePasses`. | CK-02 |
| 4 | **Coverage gate** — only if the skip list is empty: advance the checkpoint. A page still skipped after the passes holds `CheckpointLSN` back until a later cycle captures it. | CK-03 |
| 5 | **Advance `CheckpointLSN`** — `DurabilityWatermarks.UpdateCheckpointLsn(_mmf, barrierLsn)` writes the watermark block to the meta-pair's **alternate** slot (gen+1, CRC, fsync); the generation flip is the cycle's atomic commit point. | CK-05 |
| 6 | **Recycle** — `SegmentManager.MarkReclaimable(trimLsn)` deletes sealed segments below the persisted checkpoint, where `trimLsn = Min(checkpointLsn, lastTickFenceLsn)` so TickFence-only data isn't lost. | CK-04 |

There is **no FPI-bitmap reset step** — FPI is gone (§6). (Transitional: the cycle also calls `_uowRegistry.TransitionWalDurableToCommitted()` while the v1 registry is still present; see §8.)

A **flush-only cycle** (`FlushOnlyCycle` — capture + write + DC-decrement, *no* barrier/gate/meta-flip/recycle) keeps the page cache drainable during a large recovery window without advancing `CheckpointLSN` (CK-08).

### A/B slot-pairing — the doublewrite-free torn-write net (CK-05)

The meta page (root header + bootstrap dictionary + the `DurabilityWatermarks` block) and every segment-directory page occupy **two physical slots**. A write always targets the *non-current* slot with `PairGeneration = current+1` + a fresh CRC, fsyncs, then flips the in-memory current pointer. The current-valid slot is **never** overwritten, so a torn write can't destroy the only good copy — reopen selects the highest-generation CRC-valid slot; both-invalid fails the open loudly. This replaces FPI for the structural pages that rebuild (§6) can't re-derive.

### [`DurabilityWatermarks`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/DurabilityWatermarks.cs)

The watermark block persisted in the meta-pair carries:

| Field | Notes |
|---|---|
| `CheckpointLSN` | highest LSN consolidated into the data file (stored as lo32/hi32) |
| `CleanShutdown` | set on graceful shutdown; a missing/false flag at open ⇒ crash path |

`UpdateCheckpointLsn` advances the LSN and flips the meta pair atomically; `Read` / `ReadCheckpointLsn` / `ReadCleanShutdown` are used at open. (`NextFreeTSN` is *not* persisted here — it is restored from the recovered records, RB-05, §7.)

### [`StagingBufferPool`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/StagingBufferPool.cs)

Pre-allocated, 4096-byte-aligned, page-sized buffers for snapshot-based checkpoint writes:

| Knob | Value |
|---|---|
| `BufferSize` | 8192 (one database page) |
| `BufferAlignment` | 4096 (matches OS page size for `O_DIRECT`) |
| `MinCapacity` / `DefaultCapacity` / `MaxCapacity` | 16 / 512 / 4096 |

The pool uses a bitmap free-list (`BitOperations.TrailingZeroCount` for O(1) acquisition) plus a `SemaphoreSlim` for backpressure; renting blocks under a `Durability:Checkpoint:Backpressure` span so the cost is observable. The pool is shared with backup.

<a href="assets/typhon-checkpoint-pipeline.svg">
  <img src="assets/typhon-checkpoint-pipeline.svg" width="812" alt="Checkpoint v2 pipeline">
</a>

---

## 6. Torn-page safety (no FPI)

Typhon's 8 KB pages span two 4 KB device blocks; consumer NVMe makes 8 KB writes non-atomic, so a crash mid-write can **tear** a page. v1 repaired torn pages with Full-Page Images (a before-image per page per checkpoint cycle, replayed before WAL apply). **v2 retired FPI entirely** — `FpiBitmap`, `FpiCompression`, `FpiMetadata`, the `WriteFpiRecord` capture path, `SearchFpiForPage`, and the `FullPageImage` chunk type are all deleted. The replacement net:

| Page class | On CRC failure during recovery | Rule |
|---|---|---|
| **Derived** (Index, Spatial, Occupancy) | Always healed — the structure is discarded and **rebuilt** from primary data (`RebuildSecondaryIndexes`, `RederiveOccupancyOnCrash`). | RB-01 / CK-09 |
| **Primary** (component/revision content, EntityMap, cluster, collections, string table, system) | **Heal-or-loud-fail**: recorded *suspect* during recovery; resolved after rebuild — if the page no longer backs a live chunk (entity re-created in-window, scrub freed the old) → healed; if it still backs a live primary chunk → **the open FAILS LOUDLY** naming the page (`ResolveSuspectPrimaryPages`). | RB-04 |

This is the defining safety property of the redesign: a torn primary page is never silently served as if intact. Because every primary segment is a `ChunkBasedSegment`, `ResolveSuspectPrimaryPages` (`IsDerivedSegmentKind` = `Index | Spatial | Occupancy`) loud-fails uniformly — there is no silent-corruption path. The A/B slot-pairing (§5) covers the structural meta/directory pages that rebuild can't re-derive.

> CRC *detection* is unchanged from v1 — only the *response* changed, from FPI repair to rebuild / loud-fail. An uncovered torn primary page is genuinely lost data, and failing the open is the honest outcome.

---

## 7. Recovery

Recovery runs at engine open, before any transaction is accepted. In the current (transitional) code it is **two cooperating passes**:

### 7.1 v1 scan — `WalRecovery` (surviving)

[`Durability/internals/WalRecovery.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/WalRecovery.cs) — invoked from `DatabaseEngine` open as `new WalRecovery(...).Recover(UowRegistry, checkpointLSN, …)`. It still performs:

| Phase | What it does |
|---|---|
| 1 — Discover | Enumerate WAL segment files. |
| 2 — Scan | Read chunks; collect committed-UoW info + TickFence / ClusterTickFence lists. Stop at first truncation. |
| 3 — Cross-reference | Promote WAL-confirmed UoWs to `WalDurable`; `VoidRemainingPending()` voids any `Pending` left (crash before commit). |
| 6 — TickFence replay | Apply `TickFence` (per-SV-table) and `ClusterTickFence` (per-archetype) entries — SingleVersion / cluster state that has no per-record WAL trail. |
| 7 — Finalize | Emit stats. |

> Phase 4 (FPI repair) and Phase 5 (committed-transaction replay via the old `WalReplayHelper`) are **deleted** — the v2 `RecoveryDriver` owns logical-record apply, and rebuild replaces FPI. This surviving pass and the persisted `UowRegistry` it consults are slated for removal in an independent, still-pending cleanup (the Committed discipline has already shipped and does not depend on it).

### 7.2 v2 logical apply — `RecoveryDriver` + `RecoveryApplier`

[`RecoveryDriver.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/RecoveryDriver.cs) (`Run(walIO, walDir, dbe, checkpointLsn)`) runs **after** archetype initialization and owns the logical-record apply:

```
SCAN          read segments in LSN order; CRC-chain-check every chunk; truncate at first mismatch (LOG-03);
              skip non-Transaction chunks; index each logical record (no payload copy)
COMMIT FATE   committedTx = { tsn | a record with the TxCommit flag for tsn is in the valid prefix }   (LOG-04)
APPLY         strict ascending LSN (AP-11), idempotent (AP-12), via RecoveryApplier through the engine's
              own write primitives — Spawn (spawn-if-absent) / Destroy (destroy-if-present) /
              SetEnabledBits (absolute) / Slot (value overwrite, HEAD-only) / CollectionDelta (folded → Set)
RESTORE TSN   NextFreeTSN resumed past the max recovered TSN (RB-05)
```

[`RecoveryApplier`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/internals/RecoveryApplier.cs) reuses the **same** primitives the live commit path uses — `EntityMap.InsertNew` / `Upsert`, `ComponentRevisionManager.AllocCompRevStorage` for committed chain roots (mirroring `FinalizeSpawns`), `DiedTSN` for destroy — so there is no second write path, the structural fix for the whole `WalReplayHelper` bug class. Because every apply is idempotent, a crash *during* recovery is safe: CK-04 holds WAL recycling until the post-recovery seal, so a re-run sees the same window over a further-applied base (AP-12).

### 7.3 Scrub, rebuild, seal

After apply, `DatabaseEngine.RunWalV2Recovery` completes the base:

1. **Scrub (RB-03)** — collapse every Versioned revision chain to its single committed HEAD; free non-head revision / overflow chunks; sweep orphaned chunks.
2. **Rebuild (RB-01)** — rebuild every derived structure from the scrubbed primary data: secondary B+Trees (`RebuildSecondaryIndexes`), EntityMap, occupancy bitmap (`RederiveOccupancyOnCrash`, CK-09).
3. **Suspect resolution (RB-04)** — classify pages that failed CRC during recovery (§6): derived/orphaned suspects are already healed; a suspect still backing a live primary chunk fails the open loudly.
4. **Seal** — a final checkpoint cycle persists the recovered base; `CheckpointLSN` advances; WAL becomes recyclable.

### Page checksum verification

[`PageChecksumVerification`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceOptions.cs):

| Mode | Behavior |
|---|---|
| `OnLoad` (default) | Verify page CRC on every load from disk. On mismatch a page is recorded *suspect* (RecoverySuspect mode during recovery) or throws `PageCorruptionException` (normal operation). |
| `RecoveryOnly` | Skip CRC checks during normal operation; verify only during recovery. |

During the crash path the engine stays in `RecoveryOnly` through apply (there is no on-load FPI repair fallback any more), then `InitializeArchetypes` restores the configured mode after `RunWalV2Recovery` completes.

### Recovery metrics

The v2 driver returns a `RecoveryDriver.Result` (`SegmentsScanned`, `RecordsScanned`, `RecordsApplied`, `TxCommitted`, `MaxTsn`, `MaxLsn`) — every field is test-asserted. The surviving v1 pass returns [`WalRecoveryResult`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/public/WalRecoveryResult.cs) (`SegmentsScanned`, `UowsPromoted`, `UowsVoided`, `TickFenceChunksProcessed`, `BulkBeginCount`/`BulkEndCount`, `LastValidLSN`, `ElapsedMicroseconds`; its `FpiRecordsApplied` field remains for binary-compat but is never populated).

---

## 8. UoW state machine (transitional)

[`UnitOfWorkState`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/DurabilityMode.cs) — one byte, five states. Owned by the [`UowRegistry`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/internals/UowRegistry.cs) (see [08-transactions](08-transactions.md)); transitions are one-way.

| Value | State | Meaning |
|---|---|---|
| `0` | `Free` | Slot available for reuse. Zero-initialized memory is automatically Free. |
| `1` | `Pending` | Created; transactions may be in progress. |
| `2` | `WalDurable` | WAL flush complete (FUA). Survives crash. Pages may still be dirty. |
| `3` | `Committed` | Data pages checkpointed. WAL segments recyclable. |
| `4` | `Void` | Crash recovery: UoW was `Pending` at crash time. Its revisions are invisible. |

```
Free → Pending → WalDurable → Committed → Free        (normal)
Free → Pending → Void → Free                          (crash recovery)
```

- `Pending → WalDurable` after `WaitForDurable` confirms the LSN is durable.
- `WalDurable → Committed` via `UowRegistry.TransitionWalDurableToCommitted()`, invoked by the checkpoint (§5).
- `Pending → Void` during recovery's cross-reference phase (`VoidRemainingPending`); a committed bitmap then filters ghost revisions for post-crash visibility.

> **Why this is transitional.** Under the Minimal-WAL design, commit fate is the WAL `TxCommit` marker (§7.2), so the persisted registry is redundant for *fate*; its remaining role is post-crash ghost-visibility filtering. The registry is **to be demoted to a volatile in-memory id allocator** — dropping the persistence, the `Void` state, and the committed bitmap — as an independent, still-pending cleanup (not gated on the now-shipped Committed discipline). They are documented here because they are still present in, and run from, the current code.

---

## 9. Fail-fast semantics (per ADR)

The full contract:

- Any WAL write I/O failure is captured in `WalWriter._fatalError` (single field).
- `WaitForDurable` checks this field first; if non-null, throws [`WalWriteException`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Errors/public/WalWriteException.cs) (`IsTransient = false`). Restart required.
- The writer thread is *not* restarted in-process. No retry, no fallback, no degraded mode.
- Related: [`WalClaimTooLargeException`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Errors/public/WalClaimTooLargeException.cs) (a single record exceeds the ring buffer's capacity), [`WalSegmentException`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Errors/public/WalSegmentException.cs) (segment file malformed). A checkpoint cycle's *transient* exception retries next cycle; a *fatal* latch surfaces in `DurabilitySnapshot.Health` (CK-06).

The reasoning (per ADR): every alternative — buffer-and-retry, degraded read-only mode, partial commits — opens a hole in the durability contract. A stopped engine is the only state where "`Commit()` returned ⇒ data is durable" is unambiguously true.

---

## See also

- [01-foundation](01-foundation.md) — `WaitContext`, `EpochManager` (epoch-pinned page access during checkpoint)
- [02-storage](02-storage.md) — DirtyCounter / ActiveChunkWriters, `MMF.OnBackpressure`, page CRC & seqlock snapshots
- [08-transactions](08-transactions.md) — the UoW registry and state machine, `Transaction.Commit` invoking the WAL via `DurabilityLog.Append`
- [14-errors](14-errors.md) — `WalWriteException`, `WalClaimTooLargeException`, `WalSegmentException`, `CorruptionException`
- Design: `claude/design/Durability/MinimalWal/` · Rules: `claude/rules/durability.md`
