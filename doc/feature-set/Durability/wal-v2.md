---
uid: feature-durability-wal-v2
title: 'Write-Ahead Log (WAL v2 logical records)'
description: 'The single source of durability truth: logical (EntityId, slot) records, one codec, a sequential CRC-chained log.'
---

# Write-Ahead Log (WAL v2 logical records)
> The single source of durability truth: logical `(EntityId, slot)` records, one codec, a sequential CRC-chained log.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)

## 🎯 What it solves

A crash must never lose a write the application was told succeeded, and recovery must never have to guess what physically changed on disk. Logging entire data pages for a small component update wastes most of the I/O and ties the log to wherever the data happened to live — a relocation or compaction breaks replay. Typhon's WAL records only the logical fact — *this entity's component now has these bytes* — so log volume tracks the size of your changes, not your page size, and replay survives a different on-disk layout than the one that produced the log.

## ⚙️ How it works (in brief)

Every commit assembles its changes — spawns, component upserts, collection edits, destroys — into one batch and hands it to a single codec, which serializes it as logical records into a sequential per-database log. Records are framed in CRC-chained chunks, so a torn last write is detected and the log truncates cleanly at the first invalid chunk rather than being misread. Producer threads (your transactions) claim space in a lock-free buffer; a dedicated writer thread drains it and flushes to disk, optionally with Force-Unit-Access so a confirmed write is physically durable, not just OS-buffered. No record ever names a page, chunk, or buffer — only `(EntityId, slot)`, the per-archetype component slot resolved under the EntityId's routing id — so the physical placement is re-derived at apply time.

## 💻 Usage

The WAL is always on and runs transparently under every commit — you configure it once at engine setup and otherwise never touch it directly:

```csharp
services
    .AddScopedManagedPagedMemoryMappedFile(o =>
    {
        o.DatabaseName = "skirmish";
        o.DatabaseDirectory = ".";
    })
    .AddScopedDatabaseEngine(o =>
    {
        o.Wal = new WalWriterOptions
        {
            WalDirectory = "wal",
            SegmentSize = 64 * 1024 * 1024,    // 64 MB segments
            GroupCommitIntervalMs = 5,
        };
    });

// Every Commit() builds a logical record batch and appends it — no separate WAL API to call.
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx = uow.CreateTransaction();
var e = tx.OpenMut(soldier);
e.Write(Unit.Health).Current -= 25;
tx.Commit();   // batch appended now; durable on the next GroupCommit flush
```

| Option | Default | Effect |
|---|---|---|
| `WalDirectory` | `"wal"` | Directory holding WAL segment files |
| `SegmentSize` | 64 MB | Size of each pre-allocated segment file |
| `PreAllocateSegments` | 4 | Segments kept pre-allocated ahead of the write position |
| `GroupCommitIntervalMs` | 5 | Auto-flush interval consumed by `DurabilityMode.GroupCommit` |
| `UseFUA` | `true` | Per-write Force-Unit-Access durability vs. relying on explicit flush |
| `StagingBufferSize` | 256 KB | Aligned staging buffer size used for direct I/O writes |
| `WriterThreadCoreAffinity` | -1 (none) | Pin the WAL writer thread to a logical core |

## ⚠️ Guarantees & limits

- **Mandatory, not optional** — every `DatabaseEngine` runs the WAL; it cannot be turned off (only its disk backend can be swapped for an in-process one in tests/benchmarks).
- **Logical-only records** — never a page, chunk, or buffer ID — so replay tolerates a different allocation outcome than the run that produced the log.
- **One codec, one format** — all record bytes are written and read by a single codec on both the commit and recovery paths; no second, divergent path can drift out of sync.
- **Torn-tail safe** — chunks are CRC-chained; a partial last write is detected and the log truncates at the first invalid chunk instead of being misread as valid data.
- **Honest watermarks** — `CheckpointLSN ≤ DurableLsn ≤ LastAppendedLsn` always holds; `DurableLsn` never claims a record durable before it is actually fsynced.
- **Bounded record size** — a single record (header + payload) is capped at chunk size minus envelope (~64 KB); oversized component/collection payloads are rejected at schema registration, not at WAL-write time.
- **Commit stays cheap** — no disk I/O on the commit path itself beyond serializing into the in-memory buffer (~1–2 µs); FUA cost (~10–80 µs) is paid only when a mode actually waits for it.
- **Backpressure, not silent loss** — a full commit buffer surfaces as a transient `WalBackPressureTimeoutException`; a single claim larger than the buffer throws `WalClaimTooLargeException`. Append either appends every record or throws — never a partial write.
- **Not a torn-page repair tool** — the WAL records logical changes, not page images; recovering a torn *data page* is the job of checkpoint A/B pairing and structure rebuild (see the Checkpoint and Crash Recovery entries), not the log itself.

## 🧪 Tests

- [WalCommitBufferTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalCommitBufferTests.cs) — producer-side lock-free claim/publish/drain/overflow semantics
- [WalWriterTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalWriterTests.cs) — writer-thread drain/flush pipeline, FUA vs. buffered durability
- [WalRecordHeaderTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalRecordHeaderTests.cs) — on-disk logical record/frame/chunk layout (the `(EntityId, slot)` format itself)
- [WalIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalIntegrationTests.cs) — end-to-end WAL pipeline across all three `DurabilityMode`s with real disk I/O

## 🔗 Related

- Sibling: [Commit Pipeline (append-before-publish)](./commit-pipeline.md) — APPEND is the pipeline's point of no return, writing straight into this log
- Sibling: [Unit of Work (durability boundary)](../Transactions/unit-of-work.md) — the UoW's `DurabilityMode` decides when this WAL's records are flushed to stable media

<!-- Deep dive: claude/overview/06-durability.md §6.1 -->
<!-- Design: claude/design/Durability/MinimalWal/02-wal-format.md -->
<!-- ADRs: 011 (claude/adr/011-logical-wal-records.md), 012 (claude/adr/012-mpsc-ring-buffer-wal.md), 015 (claude/adr/015-crc32c-page-checksums.md), 020 (claude/adr/020-dedicated-wal-writer-thread.md) -->
<!-- Rules: claude/rules/durability.md — module LOG -->
