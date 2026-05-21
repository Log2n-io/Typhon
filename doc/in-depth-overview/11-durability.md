# 11 — Durability

**Code:** [`src/Typhon.Engine/Durability/`](../../src/Typhon.Engine/Durability/)

Durability is what makes Typhon ACID's "D". The contract is the usual one: once `Commit()` returns under a durable mode, the change survives a process or machine crash. Two cooperating pipelines deliver it — a **Write-Ahead Log** that hardens transaction effects into a sequential journal at commit time, and a **Checkpoint** that periodically writes dirty data pages to the main data file and recycles the WAL.

Both pipelines run on dedicated background threads. Commit-time work on the application thread is minimal: serialize the records into a ring buffer, optionally wait for the WAL writer to confirm durability, return. Page writes are deferred — they're a background activity decoupled from the commit path.

This doc covers the WAL (writer, segments, wire format), the checkpoint (8-step pipeline, staging pool, FPI integration), recovery (the 7-phase replay), and the durability invariants that hold across all of them.

<a href="assets/typhon-durability-overview.svg">
  <img src="assets/typhon-durability-overview.svg" width="1044" alt="Durability subsystem overview">
</a>

---

## 1. Overview

The durability layer sits between transactions (which produce changes) and storage (which holds them):

- **WAL** — every committed mutation is serialized into a sequential journal *before* its in-memory page write can be considered durable. The journal is partitioned into fixed-size segment files on disk.
- **Checkpoint** — periodically writes dirty pages from the page cache to the data file, fsyncs, advances `CheckpointLSN`, and deletes WAL segments whose records are all below that point.
- **Recovery** — on engine restart, replays committed WAL records since the last checkpoint LSN to reconstruct any in-memory state that wasn't flushed before the crash.

### Fail-fast on WAL write error (per ADR)

A WAL write I/O failure is *not* recoverable in-place. The writer catches the exception, latches it, and every subsequent attempt to wait for durability throws [`WalWriteException`](../../src/Typhon.Engine/Errors/public/WalWriteException.cs) (`IsTransient = false`). There is no retry, no degraded mode, no partial-commit window — the engine refuses further durable commits until restart. This is the entire mechanism: a single sticky `_fatalError` field on [`WalWriter`](../../src/Typhon.Engine/Durability/internals/WalWriter.cs) propagated through `WaitForDurable`.

The rationale (per ADR) is that any half-broken WAL is worse than a stopped engine: it can silently produce phantom commits, corrupt the LSN chain, or hide further failures. Fail-fast keeps the contract simple — either the WAL is healthy or the engine is down.

### Pipeline-level invariants

```
CheckpointLSN ≤ DurableLSN ≤ CurrentLSN
```

- `CurrentLSN` — the highest LSN allocated by the commit buffer (may not be written yet).
- `DurableLSN` — the highest LSN durably on disk via `fsync` / FUA.
- `CheckpointLSN` — the highest LSN whose pre-images are reflected in the data file on disk.

WAL segments whose `LastLSN < CheckpointLSN` can be deleted. Recovery only needs records above `CheckpointLSN`.

---

## 2. WAL writer

[`Durability/internals/WalWriter.cs`](../../src/Typhon.Engine/Durability/internals/WalWriter.cs), [`WalManager.cs`](../../src/Typhon.Engine/Durability/internals/WalManager.cs), [`WalCommitBuffer.cs`](../../src/Typhon.Engine/Durability/internals/WalCommitBuffer.cs)

The WAL writer is a single dedicated OS thread:

| Property | Value |
|---|---|
| Thread name | `Typhon-WAL-Writer` |
| Priority | `ThreadPriority.AboveNormal` |
| Background | true |

It is the single consumer of an MPSC (multi-producer, single-consumer) commit buffer ([`WalCommitBuffer`](../../src/Typhon.Engine/Durability/internals/WalCommitBuffer.cs)). Application threads commit a transaction by serializing records into the buffer via an atomic tail-increment claim, then publishing a frame header. The writer drains published frames, copies them into a 4096-byte-aligned staging buffer, and writes that buffer to the active segment file with `RandomAccess.Write`.

### Ring buffer sizing

Default is **8 MB total** (`ResourceOptions.WalRingBufferSizeBytes = 8 * 1024 * 1024`). The buffer is split into two halves of 4 MB each — producers fill one half while the writer drains the other (Aeron-style ping-pong, per ADR). When the active half fills up, producers wait for the writer to swap.

### GroupCommit (default mode)

[`WalWriterOptions.GroupCommitIntervalMs = 5`](../../src/Typhon.Engine/Durability/public/WalWriterOptions.cs#L17) — the WAL writer auto-flushes the staging buffer every 5 ms when running under [`DurabilityMode.GroupCommit`](../../src/Typhon.Engine/Transactions/public/DurabilityMode.cs#L21). Commit latency is ~1-2 µs (the producer doesn't block), and data-at-risk is bounded by the GroupCommit interval.

### Three durability modes

[`DurabilityMode`](../../src/Typhon.Engine/Transactions/public/DurabilityMode.cs) is specified per **UnitOfWork**, not per transaction:

| Mode | Commit latency | Data-at-risk | Use case |
|---|---|---|---|
| `Deferred` | ~1-2 µs | Until explicit `Flush()` | Game ticks, batch imports |
| `GroupCommit` | ~1-2 µs | ≤ 5 ms (interval) | General server workload |
| `Immediate` | ~15-85 µs | Zero | Financial trades |

Under `Immediate`, `Transaction.PersistAndFinalize` calls `WalManager.RequestFlush()` and then blocks in `WalManager.WaitForDurable(walHighLsn, ref ctx)` until the LSN is on stable media.

### `DurabilityOverride`

[`DurabilityOverride`](../../src/Typhon.Engine/Transactions/public/DurabilityMode.cs#L34) is declared as a public enum (`Default = 0`, `Immediate = 1`) intended for per-transaction escalation. It is **not currently referenced** by `Transaction.Commit` — there is no `Commit(DurabilityOverride)` overload. Treat the enum as reserved API surface for a future feature.

---

## 3. Wire format

Every WAL chunk has the same envelope:

```
┌────────────────────┬──────────────┬─────────────────┐
│ WalChunkHeader 8 B │  Body (var)  │ WalChunkFooter  │
│  Type/Size/PrevCRC │              │     CRC 4 B     │
└────────────────────┴──────────────┴─────────────────┘
```

[`WalChunkHeader`](../../src/Typhon.Engine/Durability/internals/WalChunkHeader.cs) (8 bytes, `Pack = 1`):

| Field | Type | Notes |
|---|---|---|
| `ChunkType` | `ushort` | Discriminator — see chunk types below |
| `ChunkSize` | `ushort` | Header (8) + body + footer (4); enables forward-compat skipping |
| `PrevCRC` | `uint` | Footer CRC of the previous chunk — patched by the writer thread |

`WalChunkFooter` is just a `uint CRC` (CRC32C over `[0, ChunkSize - 4)`, i.e. header + body excluding the footer). Producers write 0 placeholders for `PrevCRC` and the footer; the single-threaded writer patches both during the staging-buffer copy. Centralizing CRC chain management on the writer thread is what keeps the chain intact when FPI records interleave with transaction records.

### Chunk types

[`WalChunkType`](../../src/Typhon.Engine/Durability/internals/WalChunkHeader.cs#L10):

| Value | Type | Body |
|---|---|---|
| `1` | `Transaction` | `WalRecordHeader` + component payload |
| `2` | `FullPageImage` | `long LSN` + `FpiMetadata (16 B)` + page data (optionally LZ4-compressed) |
| `3` | `TickFence` | `TickFenceHeader (24 B)` + N entries of `(ChunkId:4 B, ComponentData:PayloadStride B)` |
| `4` | `ClusterTickFence` | `ClusterTickFenceHeader (24 B)` + N entries of `(EntityIndex:4 B, AllComponentData)` |

TickFence and ClusterTickFence are SingleVersion / cluster-storage recovery chunks emitted at tick boundaries — see [06-ecs §8](06-ecs.md) for the storage-mode story.

### `WalRecordHeader` (32 bytes)

[`WalRecordHeader`](../../src/Typhon.Engine/Durability/internals/WalRecordHeader.cs) is the body of a `Transaction` chunk:

| Field | Type | Notes |
|---|---|---|
| `LSN` | `long` | Monotonic, globally unique |
| `TransactionTSN` | `long` | MVCC snapshot timestamp |
| `UowEpoch` | `ushort` | Registry link — identifies the owning UoW (also called UowId in [08-transactions](08-transactions.md)) |
| `ComponentTypeId` | `ushort` | Routes replay to the right component table |
| `EntityId` | `long` | Primary key |
| `PayloadLength` | `ushort` | Component data bytes after this header |
| `OperationType` | `byte` | `Create = 1`, `Update = 2`, `Delete = 3` |
| `Flags` | `byte` | See below |

`WalRecordFlags`:

- `UowBegin = 0x01` — first record in a Unit of Work
- `UowCommit = 0x02` — last record (commit marker)

Recovery uses these flags to decide which UoWs are crash-safe: only those with a `UowCommit` record in the WAL are promoted from `Pending` to `WalDurable`.

CRC and `PrevCRC` are *not* part of `WalRecordHeader` — they live in `WalChunkHeader` / `WalChunkFooter`. This was a deliberate split when chunk types proliferated; it lets one writer thread own the CRC chain without producers needing to know about it.

---

## 4. Segment management

[`WalSegmentManager`](../../src/Typhon.Engine/Durability/internals/WalSegmentManager.cs)

WAL records are written into fixed-size segment files named `{segmentId:D16}.wal` in the configured WAL directory (default `wal/`). Each segment starts with a 4096-byte `WalSegmentHeader` ([file](../../src/Typhon.Engine/Durability/internals/WalSegmentHeader.cs)) carrying magic (`TYFW`), version, segment ID, first/prev LSN, and a CRC32C — sized for one aligned disk page so the first record sits at a 4096-byte boundary (required for `O_DIRECT` / `FILE_FLAG_NO_BUFFERING`).

### Defaults

| Knob | Default | Source |
|---|---|---|
| Segment size | 64 MB | `WalWriterOptions.SegmentSize` |
| Pre-allocated segments | 4 | `WalWriterOptions.PreAllocateSegments` |
| Rotation threshold | 75 % utilization | `WalWriter.RotationThreshold` constant |

When the active segment passes 75 % utilization, the writer seals it, opens the next pre-allocated segment, writes its header, and replenishes the pre-allocation pool. Pre-allocation creates new empty files of full segment size via `RandomAccess.SetLength` so rotation doesn't pay metadata-write latency on the hot path.

### Segments are deleted, not "recycled"

This is worth being explicit about: `WalSegmentManager.MarkReclaimable(checkpointLSN)` walks the sealed-segment list and calls `_fileIO.Delete(path)` for every segment whose `LastLSN < checkpointLSN`. There is no rename, no reuse, no recycling. Pre-allocation creates fresh files on demand.

```csharp
// WalSegmentManager.cs:189
if (lastLSN < checkpointLSN)
{
    _fileIO.Delete(path);
    _sealedSegments.RemoveAt(i);
    reclaimed++;
}
```

### File flags by platform

[`WalFileIO.OpenSegment`](../../src/Typhon.Engine/Durability/internals/WalFileIO.cs#L28):

```csharp
var options = OperatingSystem.IsWindows() ? NoBuffering : FileOptions.None;
if (withFUA)
{
    options |= FileOptions.WriteThrough;
}
return File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, options);
```

- **Windows** — `FILE_FLAG_NO_BUFFERING` (the `(FileOptions)0x20000000` constant) bypasses the OS page cache. FUA (`FILE_FLAG_WRITE_THROUGH`) adds per-write durability.
- **Linux / macOS** — `NoBuffering` is omitted; durability relies on `FileOptions.WriteThrough` (which maps to FUA on supporting hardware) plus explicit `RandomAccess.FlushToDisk` (`fsync`/`fdatasync`).

There is **no `O_DIRECT | O_DSYNC` P/Invoke** in the code — earlier drafts of this doc referenced one that never existed. `FileOptions.WriteThrough` is the only mechanism.

---

## 5. Checkpoint

[`Durability/internals/CheckpointManager.cs`](../../src/Typhon.Engine/Durability/internals/CheckpointManager.cs)

The checkpoint is a single dedicated OS thread:

| Property | Value |
|---|---|
| Thread name | `Typhon-Checkpoint` |
| Priority | `ThreadPriority.Normal` |
| Background | true |

It wakes on a `ManualResetEventSlim` either at the configured interval, on `ForceCheckpoint`, or at shutdown.

### Triggers

The checkpoint loop runs a cycle when:

1. **Timer expiry** — `_wakeEvent.Wait(CheckpointIntervalMs)` returns. Default `CheckpointIntervalMs = 30000` (30 s).
2. **`ForceCheckpoint`** — invoked by `DatabaseEngine.ForceCheckpoint()` or by storage-layer backpressure.
3. **Shutdown** — one final cycle to flush remaining dirty pages.

[`ResourceOptions.CheckpointMaxDirtyPages`](../../src/Typhon.Engine/Resources/public/ResourceOptions.cs#L117) (default 10 000) is *declared* but currently **not consulted** by `CheckpointLoop` — it's reserved for a future per-cycle write cap. There is no WAL-segment-threshold trigger either.

### Backpressure-driven force

[02-storage](02-storage.md) sets `MMF.OnBackpressure = () => CheckpointManager?.ForceCheckpoint()` during engine initialization ([`DatabaseEngine.cs:672`](../../src/Typhon.Engine/Ecs/public/DatabaseEngine.cs)). When the page cache detects sustained back-pressure (no clean pages to evict), it fires the callback to ask the checkpoint to free dirty pages by writing them out.

### 8-step pipeline

[`RunCheckpointCycle`](../../src/Typhon.Engine/Durability/internals/CheckpointManager.cs#L268):

| Step | Action |
|---|---|
| 0 | **Reset FPI bitmap** — `_mmf.FpiBitmap?.ClearAll()`. Modifications from now on need fresh FPIs. |
| 1 | **Capture `DurableLsn`** — atomic read of `_walManager.DurableLsn`. This is the cycle's `targetLsn`. |
| 2 | **Collect dirty pages** — `_mmf.CollectDirtyMemPageIndices()` returns the `int[]` of cache slots with DC > 0. |
| 3 | **Write dirty pages via staging** — `_mmf.WritePagesForCheckpoint(dirtyPages, _stagingPool, out writtenCount)`. The staging pool's snapshot semantics ensure ACW = 0 at copy time. |
| 4 | **Fsync the data file** — `_mmf.FlushToDisk()`. |
| 5 | **Decrement DirtyCounter** — only for pages we actually wrote; re-dirtied pages stay > 0. |
| 6 | **Transition `WalDurable` → `Committed`** — `_uowRegistry.TransitionWalDurableToCommitted()` walks the UoW registry. |
| 7 | **Update CheckpointLSN + fsync** — `_mmf.UpdateCheckpointLSN(targetLsn, _epochManager)` writes the header and fsyncs. |
| 8 | **Recycle WAL segments** — `_walManager.SegmentManager.MarkReclaimable(trimLsn)` deletes sealed segments. The `trimLsn` is `Min(targetLsn, lastTickFenceLsn)` so TickFence-only data isn't lost. |

### `StagingBufferPool`

[`StagingBufferPool.cs`](../../src/Typhon.Engine/Durability/internals/StagingBufferPool.cs) — pre-allocated, 4096-byte-aligned, page-sized buffers for snapshot-based checkpoint writes:

| Knob | Value |
|---|---|
| `BufferSize` | 8192 (one database page) |
| `BufferAlignment` | 4096 (matches OS page size for `O_DIRECT`) |
| `MinCapacity` | 16 |
| `DefaultCapacity` | 512 |
| `MaxCapacity` | 4096 |

The pool uses a bitmap free-list (one bit per slot, `BitOperations.TrailingZeroCount` for O(1) acquisition) plus a `SemaphoreSlim` for backpressure. Renting blocks via the semaphore when all slots are in use; the wait is wrapped in a `Durability:Checkpoint:Backpressure` span so the cost is observable.

---

## 6. Full-Page Images (FPI)

Torn-page protection. When a checkpoint is in progress and a page is being modified concurrently, we need to ensure that even if the disk write tears (sector-level partial write under power loss), recovery can repair it.

### `FpiBitmap`

[`FpiBitmap.cs`](../../src/Typhon.Engine/Durability/internals/FpiBitmap.cs) — one bit per page cache slot, lock-free `Interlocked.Or` for `TestAndSet`:

```csharp
public bool TestAndSet(int memPageIndex)
{
    var wordIndex = memPageIndex >> 6;
    var mask = 1UL << (memPageIndex & 0x3F);
    var previous = Interlocked.Or(ref _words[wordIndex], mask);
    return (previous & mask) != 0;   // true if bit was already set
}
```

The return value is the **prior** state of the bit. The page-write path uses it: first writer in this checkpoint cycle gets `false` and must emit an FPI; subsequent writers get `true` and skip. `ClearAll()` is called at Step 0 of every checkpoint cycle to start a fresh window.

### `FpiMetadata` (16 bytes)

[`FpiMetadata.cs`](../../src/Typhon.Engine/Durability/internals/FpiMetadata.cs):

| Field | Type | Notes |
|---|---|---|
| `FilePageIndex` | `int` | Global page index in the data file |
| `SegmentId` | `int` | Reserved for multi-segment data files (always 0 for now) |
| `ChangeRevision` | `int` | Page's `ChangeRevision` at capture time |
| `UncompressedSize` | `ushort` | Always `PagedMMF.PageSize` (8192) — used during decompression |
| `CompressionAlgo` | `byte` | `0 = none`, `1 = LZ4` |
| `Reserved` | `byte` | Alignment padding |

FPI chunk body layout: `[LSN: 8 B] [FpiMetadata: 16 B] [page payload: variable]`. The payload is either raw 8192 bytes or LZ4-compressed (controlled by `WalWriterOptions.EnableFpiCompression`).

### Repair-before-replay

During recovery (Phase 4 below) FPI repair runs **before** transaction replay. The flow per FPI entry:

1. Read the current page from disk (`_mmf.ReadPageDirect`).
2. If stored CRC is 0, the page was never checkpointed — skip.
3. Compute CRC; if it matches stored CRC, the page is consistent — skip.
4. CRC mismatch → page is torn → overwrite from FPI (`_mmf.WritePageDirect`).
5. After all repairs: `_mmf.FlushToDisk()`.

This restores the data file to a state where the replayed transaction records can be applied without ambiguity.

---

## 7. Recovery (7 phases)

[`Durability/internals/WalRecovery.cs`](../../src/Typhon.Engine/Durability/internals/WalRecovery.cs) — `Recover(registry, checkpointLSN, dbe)` returns a [`WalRecoveryResult`](../../src/Typhon.Engine/Durability/public/WalRecoveryResult.cs).

Every phase emits a typed span ([`Durability:Recovery:*`](../../src/Typhon.Engine/Profiler/), see [12-observability](12-observability.md)) so the cost breakdown is visible in the profiler.

| Phase | Name | What it does |
|---|---|---|
| 1 | **Discover** | Enumerate WAL segment files |
| 2 | **Scan** | For each segment, read chunks; classify into UoW state map, FPI map, TickFence list, ClusterTickFence list. Stop at first truncation. |
| 3 | **Cross-reference** | For each UoW with `UowCommit` marker: `registry.PromoteToWalDurable`. Then `VoidRemainingPending()` voids any other `Pending` entries (crash before commit). |
| 4 | **FPI** | Torn-page repair (see §6 — runs **before** transaction replay). |
| 5 | **Redo** | Replay each committed UoW's records in LSN order via `WalReplayHelper.ReplayRecord(dbe, ref header, payload)`. |
| 6 | **TickFence replay** | Apply `TickFence` (per-SV-table) and `ClusterTickFence` (per-archetype) entries — these reconstruct SingleVersion and cluster-storage state that has no per-record WAL trail. |
| 7 | **Finalize** | `WalReplayHelper.ResetReplayState()` clears the lazy per-table replay PK map; emit final stats. |

### Page checksum verification

[`PageChecksumVerification`](../../src/Typhon.Engine/Resources/public/ResourceOptions.cs#L10):

| Mode | Behavior |
|---|---|
| `OnLoad` (default) | Verify page CRC on every load from disk. Detects corruption on first access; triggers FPI repair. |
| `RecoveryOnly` | Skip CRC checks during normal operation; only verify during crash recovery. Lower overhead, narrower detection. |

### Recovery metrics

`WalRecoveryResult` exposes everything the operator needs to audit recovery:

```csharp
public struct WalRecoveryResult
{
    public int SegmentsScanned;
    public int RecordsScanned;
    public int UowsPromoted;            // Pending → WalDurable
    public int UowsVoided;              // Pending with no commit marker
    public int RecordsReplayed;
    public int FpiRecordsApplied;       // torn-page repairs
    public int TickFenceChunksProcessed;
    public int TickFenceEntriesReplayed;
    public long LastValidLSN;
    public long ElapsedMicroseconds;
}
```

---

## 8. UoW state machine

[`UnitOfWorkState`](../../src/Typhon.Engine/Transactions/public/DurabilityMode.cs#L53) — one byte, five states. Owned by the `UowRegistry` (see [08-transactions](08-transactions.md)); transitions are one-way.

| Value | State | Meaning |
|---|---|---|
| `0` | `Free` | Slot available for reuse. Zero-initialized memory is automatically Free. |
| `1` | `Pending` | Created; transactions may be in progress. WAL records volatile. |
| `2` | `WalDurable` | WAL flush complete (FUA). Survives crash. Pages may still be dirty. |
| `3` | `Committed` | Data pages checkpointed. WAL segments recyclable. |
| `4` | `Void` | Crash recovery: UoW was Pending at crash time. All its revisions are invisible. |

### Normal-path transitions

```
Free → Pending → WalDurable → Committed → Free
```

- `Pending` → `WalDurable`: `UnitOfWork.TransitionToWalDurable()` after `WaitForDurable` confirms LSN is on stable media (Immediate mode) or after RequestFlush (GroupCommit). Without WAL (in-memory mode), goes straight to `Committed`.
- `WalDurable` → `Committed`: `UowRegistry.TransitionWalDurableToCommitted()` invoked by the checkpoint at Step 6.
- `Committed` → `Free`: GC pass after no active snapshot can see the UoW any more (see `DeferredCleanupManager` in [08-transactions](08-transactions.md)).

### Crash-recovery path

```
Free → Pending → Void → Free
```

A `Pending` UoW at crash time becomes `Void` during Phase 3 (`VoidRemainingPending`) — its revisions are stamped as invisible to all future snapshots. GC eventually reclaims the slot.

---

## 9. Fail-fast semantics (per ADR)

The full contract:

- Any WAL write I/O failure is captured in `WalWriter._fatalError` (single volatile field).
- `WaitForDurable` checks this field first; if non-null, throws [`WalWriteException`](../../src/Typhon.Engine/Errors/public/WalWriteException.cs).
- `WalWriteException : DurabilityException`, `IsTransient = false`. Restart required.
- The writer thread is *not* restarted in-process. There is no retry logic, no fallback, no degraded mode.
- Other WAL exceptions follow the same fail-fast intent: [`WalClaimTooLargeException`](../../src/Typhon.Engine/Errors/public/WalClaimTooLargeException.cs) (a single record exceeds the ring buffer's capacity), [`WalSegmentException`](../../src/Typhon.Engine/Errors/public/WalSegmentException.cs) (segment file is malformed or inconsistent).

The reasoning (per ADR): every alternative considered — buffer-and-retry, degraded read-only mode, partial commits — opens a hole somewhere in the durability contract. A stopped engine is the only state where the invariant "`Commit()` returned ⇒ data is durable" is unambiguously true.

---

## See also

- [01-foundation](01-foundation.md) — `WaitContext`, `Stopwatch` math, `EpochManager` (epoch-pinned page access during checkpoint)
- [02-storage](02-storage.md) — DirtyCounter / ActiveChunkWriters, `MMF.OnBackpressure`, FPI integration with the page cache
- [08-transactions](08-transactions.md) — UoW state machine in detail, `Transaction.Commit` invoking WAL via `WalSerializer.SerializeToWal`
- [14-errors](14-errors.md) — `WalWriteException`, `WalClaimTooLargeException`, `WalSegmentException`, `CorruptionException`
