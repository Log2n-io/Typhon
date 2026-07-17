---
uid: concept-bulk-load
title: 'Bulk load'
description: 'An opt-in, exclusive second write path for seeding and importing — it skips per-row WAL entirely and substitutes one session-wide durability barrier. A third axis, orthogonal to DurabilityMode.'
---

# Bulk load

> **In one line:** an opt-in, **exclusive** second write path for seeding/importing — it skips per-row WAL entirely and substitutes **one session-wide durability barrier**. A third axis, orthogonal to [`DurabilityMode`](xref:concept-durability).

The normal [transaction](xref:concept-transaction) path is tuned for OLTP: every commit appends a WAL record, and every open transaction pins the pages it touched. Seeding a multi-million-entity database inverts that trade — the per-row WAL traffic and epoch-pinned pages exhaust the [page cache](xref:concept-page-cache) and the load dies on a backpressure timeout, *whatever* `DurabilityMode` you picked. Bulk load exists for that one workload, and is isolated from every other transaction's durability contract.

`BeginBulkLoad` opens a session over a single [unit of work](xref:concept-unit-of-work) and transaction, configured to write **no per-row WAL records**. Internally it recycles its transaction every few thousand operations so the page cache can keep evicting; the UoW itself stays open — and MVCC-invisible to everyone else — for the whole session. `CompleteBulkLoad()` is the single barrier that makes the load durable *and* visible: commit the last transaction, force a checkpoint, and wait for a closing manifest record to reach disk.

> ⚠️ **All-or-nothing at *session* granularity.** Nothing you write is durable or visible until `CompleteBulkLoad()` returns — `Dispose` without it, or a crash mid-session, discards the entire load. It is also **exclusive** (one per engine) and **thread-affine**; `Update`/`Destroy` only reach entities spawned in the same session; and `CompleteBulkLoad` blocks on a real checkpoint cycle with no latency budget. Concurrent readers are unaffected throughout — they keep seeing the pre-bulk snapshot.

## How it relates

- **[Durability — mode & discipline](xref:concept-durability)** — the per-UoW axis this path deliberately **bypasses** rather than tunes.
- **[Transaction](xref:concept-transaction)** / **[Unit of Work](xref:concept-unit-of-work)** — the normal path, and what the session wraps.
- **[Page cache & paged store](xref:concept-page-cache)** — the pressure this exists to relieve.
- **[WAL & checkpoint](xref:concept-wal-checkpoint)** — no per-row records; one forced checkpoint closes the session.

## In the API

- [`DatabaseEngine.BeginBulkLoad(BulkLoadOptions)`](xref:Typhon.Engine.DatabaseEngine.BeginBulkLoad*) → [`BulkLoadSession`](xref:Typhon.Engine.BulkLoadSession) — [`Spawn`](xref:Typhon.Engine.BulkLoadSession.Spawn*) / [`Update`](xref:Typhon.Engine.BulkLoadSession.Update*) / [`OpenMut`](xref:Typhon.Engine.BulkLoadSession.OpenMut*), then [`CompleteBulkLoad()`](xref:Typhon.Engine.BulkLoadSession.CompleteBulkLoad*).
- [`BulkLoadOptions`](xref:Typhon.Engine.BulkLoadOptions) — [`ProgressReporter`](xref:Typhon.Engine.BulkLoadOptions.ProgressReporter) / [`CheckpointTimeout`](xref:Typhon.Engine.BulkLoadOptions.CheckpointTimeout).
- [`BulkSessionAlreadyActiveException`](xref:Typhon.Engine.BulkSessionAlreadyActiveException) · [`BulkSessionClosedException`](xref:Typhon.Engine.BulkSessionClosedException) · [`BulkLoadCheckpointTimeoutException`](xref:Typhon.Engine.BulkLoadCheckpointTimeoutException).

## Learn & use

- **Feature detail:** [Bulk load session](xref:feature-transactions-bulk-load-session)
- **Narrative:** [Guide ch.3 — transactions](xref:guide-transactions)
