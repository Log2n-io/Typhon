# Checkpoint v2 (SnapshotStore pipeline)
> Background pipeline that consolidates dirty pages into the data file and advances CheckpointLSN only over pages it actually wrote.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](../README.md)

## 🎯 What it solves

The WAL grows without bound, and crash-recovery replay time grows with it, unless committed changes are periodically consolidated from the log into the data file. A naive "just write the dirty pages" approach risks two failure modes: persisting bytes whose WAL record isn't durable yet (phantom data after a crash) or advancing the recovery watermark past a page an active writer hadn't finished mutating (the data silently vanishes once its WAL segment is reclaimed). Applications need WAL disk usage and recovery time bounded automatically, without trading away crash correctness or stalling commits to get it.

## ⚙️ How it works (in brief)

Each cycle starts with a durability barrier — the WAL is flushed through everything appended so far — then every dirty page is snapshotted through a lock-free, seqlock-protected copy; a page an active writer is mid-mutation on is simply skipped this cycle. The WAL is flushed a second time through the high-water LSN the captured copies can reflect, *before* those copies are fsynced to the data file, so the data file can never hold bytes whose WAL record could still be lost. `CheckpointLSN` — the watermark that bounds recovery replay and gates WAL segment recycling — only advances if every page collected at cycle start was actually captured this cycle (the coverage gate); a page skipped for an in-flight writer holds the watermark back, not the correctness, until a later cycle catches it. Cycles run automatically on a timer or a dirty-page threshold, or can be triggered on demand.

## 💻 Usage

```csharp
services
    .AddScopedManagedPagedMemoryMappedFile(o =>
    {
        o.DatabaseName = "skirmish";
        o.DatabaseDirectory = ".";
    })
    .AddScopedDatabaseEngine(o =>
    {
        o.Resources.CheckpointIntervalMs = 30_000;        // background cadence when idle
        o.Resources.CheckpointMaxDirtyPages = 10_000;      // force an early cycle past this many dirty pages
        o.Resources.CheckpointBarrierTimeoutMs = 30_000;   // bound on the per-cycle WAL durability-barrier wait
    });

// Checkpointing runs automatically — call ForceCheckpoint() only when you need to push a cycle now,
// e.g. before a planned shutdown or right after a large bulk import.
dbe.ForceCheckpoint();
```

| Option | Default | Effect |
|---|---|---|
| `CheckpointIntervalMs` | 30000 | Background cycle cadence while idle |
| `CheckpointMaxDirtyPages` | 10000 | Dirty-page count that forces an earlier cycle |
| `CheckpointBarrierTimeoutMs` | 30000 | Timeout for a cycle's WAL durability-barrier waits; on expiry the cycle is classified transient and retried next tick |

## ⚠️ Guarantees & limits

- **Captured ⊆ durable** — the WAL is always flushed through a captured page's high-water LSN before that page's bytes reach the data file; the data file can never contain a change whose WAL record could still be lost.
- **Never advances past unwritten data** — `CheckpointLSN` only moves forward when every page collected that cycle was captured; WAL segments are only ever recycled below the persisted `CheckpointLSN`, so recovery replay window and WAL disk usage are both bounded by the last successful cycle.
- **Failure-isolated** — a transient I/O or back-pressure failure degrades health and retries next cycle; it never silently and permanently disables checkpointing. A non-transient failure halts periodic cycles, but shutdown still attempts one last-chance flush.
- **`ForceCheckpoint()` doesn't block** — it wakes the background cycle and returns immediately; pair it with the [Metric Reporting](../../Resources/metric-reporting.md) snapshot (`ResourceGraph.GetSnapshot()`, node `"Durability/CheckpointManager"`) if the caller needs to confirm a cycle completed.
- **Cadence is a throughput/recovery-time trade-off** — a shorter `CheckpointIntervalMs` bounds crash-recovery replay tighter at the cost of more frequent page writes; a longer one amortizes I/O but lengthens the post-crash recovery window.
- A page with an unusually long-lived active writer can repeatedly miss the coverage gate; this only delays `CheckpointLSN` advancement for that page, it never advances incorrectly.

## 🧪 Tests

- [CheckpointManagerTests](../../../../test/Typhon.Engine.Tests/Durability/CheckpointManagerTests.cs) — coverage gate (skipped vs. released pages), `CheckpointLSN` advancement, timer/force-cycle triggering, WAL segment reclaim
- [CheckpointResilienceTests](../../../../test/Typhon.Engine.Tests/Durability/CheckpointResilienceTests.cs) — transient-fault retry vs. fatal-fault halt, last-chance flush at shutdown

## 🔗 Related

- Sub-features: [A/B Protected-Page Slot-Pairing](./protected-page-pairing.md)
- Sibling: [Memory-Mapped Page Cache & Clock-Sweep Eviction](../../Storage/page-cache.md) — checkpoint copies pages straight out of the page cache it consolidates

<!-- Deep dive: claude/overview/06-durability.md §6.4, claude/design/Durability/MinimalWal/04-checkpoint.md -->
<!-- ADR: claude/adr/025-checkpoint-manager-sole-fsync-owner.md -->
<!-- Rules: claude/rules/durability.md — module CK -->
