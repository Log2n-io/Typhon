# BulkLoad Write Path
> Skip per-row WAL for mass loads — one checkpoint-backed manifest pair makes the whole load atomic, not each row.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)
**Assumes:** [Durability Modes](durability-modes/README.md)

## 🎯 What it solves

Typhon's default write path is latency-critical: every spawn/update/destroy emits a per-row WAL record and dirties
pages that only the background checkpoint can drain, throwing a backpressure timeout if it can't keep up. That's
the right trade-off for OLTP traffic; it's the wrong one for seeding a multi-million-entity world, importing a
dataset, or rebuilding a fixture. Every mature engine ships a second, throughput-first path for exactly this case
(SQL Server `BULK_LOGGED`, Postgres `pg_bulkload`) — BulkLoad is Typhon's.

## ⚙️ How it works (in brief)

`BeginBulkLoad` opens an exclusive session that writes spawned/updated/destroyed entities straight to pages,
skipping per-row WAL entirely. The session is bracketed by two manifest records: `BulkBegin` at open, `BulkEnd` at
`CompleteBulkLoad`. `CompleteBulkLoad` is the durability barrier — it drains every dirty page, forces a checkpoint
and waits for it, emits `BulkEnd`, and waits for that record to be durable before returning. Only then are the
session's entities visible to other transactions. Closing the session any other way (`Dispose` without
`CompleteBulkLoad`, or a crash) discards the entire session as if it never ran — there is no partial result.

## 💻 Usage

```csharp
using var session = engine.BeginBulkLoad(new BulkLoadOptions
{
    ProgressReporter = p => Console.WriteLine($"{p.EntitiesSpawned:N0} spawned ({p.ElapsedMilliseconds} ms)"),
    ProgressBatchSize = 50_000,
    CheckpointTimeout = TimeSpan.FromMinutes(10),
});

for (int i = 0; i < 4_000_000; i++)
{
    var id = session.Spawn<ParticleArchetype>();
    session.Update(id, new Position { X = i, Y = 0, Z = 0 });
}

try
{
    session.CompleteBulkLoad();   // synchronous: drain + checkpoint + BulkEnd barrier before returning
}
catch (BulkLoadCheckpointTimeoutException)
{
    session.CompleteBulkLoad();  // session is still open — retry, or Dispose() to discard
}
// 4M entities now durable and visible to subsequent transactions.
```

| Option | Default | Effect |
|---|---|---|
| `ProgressReporter` | `null` | Callback invoked every `ProgressBatchSize` operations |
| `ProgressBatchSize` | `10_000` | Operations between progress callbacks |
| `CheckpointTimeout` | 5 min | Max wait in `CompleteBulkLoad` for the forced checkpoint; throws `BulkLoadCheckpointTimeoutException` and leaves the session open on expiry |

## ⚠️ Guarantees & limits

- **Exclusive** — only one `BulkLoadSession` per engine; a second `BeginBulkLoad` while one is open throws `BulkSessionAlreadyActiveException`.
- **Thread-affine** — only the thread that called `BeginBulkLoad` may call methods on the returned session.
- **No per-row WAL** — the only WAL traffic for the session is the `BulkBegin`/`BulkEnd` manifest pair (plus unrelated TickFence activity); `DurabilityMode`/`DurabilityOverride` don't apply inside a session.
- **Session-granularity atomicity, not per-row** — `CompleteBulkLoad` returning is the only success signal: every spawn/update/destroy in the session becomes durable and visible together, or (on `Dispose` without `CompleteBulkLoad`, or a crash) none of them do.
- **Concurrent readers unaffected** — regular `UnitOfWork`s keep running during the session and see the pre-bulk MVCC snapshot; bulk-spawned entities aren't visible to them until `CompleteBulkLoad` returns.
- **Update/Destroy scope** — only target entities spawned earlier in the *same* session; mutating pre-existing entities requires the standard `UnitOfWork` path.
- **No latency budget** — `CompleteBulkLoad` blocks on a real checkpoint cycle; `CheckpointTimeout` only guards against it never finishing, it doesn't bound how long the call takes.
- **Partial status** — discarded-session page reclamation isn't wired yet: pages allocated by a session that's `Dispose`d without `CompleteBulkLoad` (or that crashes) stay marked occupied — not user-visible, but not freed — until a future recovery increment adds allocation tracking and explicit free.

## 🧪 Tests

- [BulkLoadApiSurfaceTests](../../../test/Typhon.Engine.Tests/Durability/BulkLoadApiSurfaceTests.cs) — session exclusivity/lifecycle, `BeginBulkLoad`/`Dispose`/`CompleteBulkLoad` API surface
- [BulkLoadWriteTests](../../../test/Typhon.Engine.Tests/Durability/BulkLoadWriteTests.cs) — no per-row WAL, exactly one `BulkBegin`/`BulkEnd` pair, entities invisible until `CompleteBulkLoad`
- [BulkLoadRecoveryTests](../../../test/Typhon.Engine.Tests/Durability/BulkLoadRecoveryTests.cs) — crash after `BulkBegin` discards the session; crash after `CompleteBulkLoad` survives reopen

## 🔗 Related

- Sibling entry: [TyphonException Hierarchy & Catalog](../Errors/exception-hierarchy.md)

<!-- Deep dive: claude/overview/06-durability.md §6.8 -->
<!-- Design: claude/design/Durability/BulkLoad/README.md (01-api, 02-write-path, 03-recovery, 04-manifest-format, 05-invariants) -->
<!-- ADR: 053-bulk-load-write-path (claude/adr/053-bulk-load-write-path.md) -->
<!-- Rules: claude/rules/durability.md — module BulkLoad (BL-01..04) -->
