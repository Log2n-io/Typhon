---
uid: feature-transactions-bulk-load-session
title: 'Bulk Load Session'
description: 'An opt-in, exclusive write path that batches writes through a recycled Transaction and commits the whole load atomically.'
---

# Bulk Load Session
> An opt-in, exclusive write path that batches writes through a recycled Transaction and commits the whole load atomically.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Transactions](./README.md)

**Assumes:** [Unit of Work (durability boundary)](./unit-of-work.md)

## 🎯 What it solves
The standard `UnitOfWork`/`Transaction` path is tuned for OLTP: every commit appends a WAL record and every open
transaction pins its touched pages until it commits or rolls back. That is exactly the wrong trade-off for seeding
a multi-million-entity database, importing a dataset, or rebuilding a fixture — at that scale the per-row WAL
traffic and epoch-pinned pages exhaust the page cache and the load aborts with a backpressure timeout, regardless
of `DurabilityMode`. Bulk Load Session is a second write path for exactly this one workload, opt-in and isolated
from every other transaction's durability contract.

## ⚙️ How it works (in brief)
`DatabaseEngine.BeginBulkLoad` opens a session backed by one `UnitOfWork` and one `Transaction`, configured to skip
per-row WAL records entirely. Internally the session periodically commits and replaces its underlying `Transaction`
every few thousand operations so the page cache can keep evicting touched pages — the owning `UnitOfWork` itself
stays open, and MVCC-invisible to everyone else, for the whole session. `CompleteBulkLoad` is the single barrier
that makes the session durable and visible: it commits the final transaction, forces a checkpoint, and waits for a
closing manifest record to reach disk before returning. This axis is orthogonal to `DurabilityMode` — a bulk
session doesn't run in Deferred/GroupCommit/Immediate, it bypasses per-row WAL altogether and substitutes one
session-wide durability barrier.

## 💻 Usage
```csharp
using var session = engine.BeginBulkLoad(new BulkLoadOptions
{
    ProgressReporter = p => Console.WriteLine($"{p.EntitiesSpawned:N0} spawned"),
    CheckpointTimeout = TimeSpan.FromMinutes(10),
});

for (var i = 0; i < 4_000_000; i++)
{
    var id = session.Spawn<ParticleArchetype>();
    session.Update(id, new Position { X = i, Y = 0, Z = 0 });
}

session.CompleteBulkLoad();   // blocks: commit + forced checkpoint + manifest durable
// every entity spawned above is now visible to other transactions
```

| Option | Default | Effect |
|---|---|---|
| `ProgressReporter` | `null` | Callback invoked every `ProgressBatchSize` operations |
| `ProgressBatchSize` | `10_000` | Operations between progress callbacks |
| `CheckpointTimeout` | 5 min | Max wait in `CompleteBulkLoad` for the forced checkpoint; throws `BulkLoadCheckpointTimeoutException` and leaves the session open on expiry |

## ⚠️ Guarantees & limits
- **Exclusive** — one `BulkLoadSession` per engine; a concurrent `BeginBulkLoad` throws `BulkSessionAlreadyActiveException`.
- **Thread-affine** — only the thread that called `BeginBulkLoad` may call methods on the returned session.
- **Same call shape as `Transaction`** — `Spawn`/`OpenMut`/`Update`/`Destroy` mirror their `Transaction` counterparts, so application entity/component code mostly ports unchanged.
- **Update/Destroy scope** — only target entities spawned earlier in the *same* session; mutating pre-existing entities still requires the standard `UnitOfWork` path.
- **All-or-nothing at session granularity** — entities become durable and visible only when `CompleteBulkLoad` returns; `Dispose` without `CompleteBulkLoad`, or a crash mid-session, discards everything written so far.
- **Closed once** — after `CompleteBulkLoad` or `Dispose`, further calls throw `BulkSessionClosedException`.
- **No latency budget** — `CompleteBulkLoad` blocks on a real checkpoint cycle; `CheckpointTimeout` only guards against it never finishing, it doesn't bound how long the call takes.
- **Concurrent readers unaffected** — regular `UnitOfWork`s keep running during the session and see the pre-bulk MVCC snapshot throughout.

## 🧪 Tests
- [BulkLoadApiSurfaceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/BulkLoadApiSurfaceTests.cs) — session
  API shape: exclusivity (`BeginBulkLoad_TwiceWithoutClosing_Throws`), closed-session guards, option defaults
- [BulkLoadWriteTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/BulkLoadWriteTests.cs) — entities visible only
  after `CompleteBulkLoad`, exactly-one bulk-begin/bulk-end WAL record pair (no per-row records), transaction-recycle
  threshold crossing
- [BulkLoadRecoveryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/BulkLoadRecoveryTests.cs) — crash after
  bulk-begin loses everything, crash after `CompleteBulkLoad` survives reopen in full

## 🔗 Related
- Deep dive (write-path internals, manifest, recovery): [Durability — BulkLoad Write Path](../Durability/bulk-load.md)
- Sibling: [Durability Modes](./durability-modes/README.md) — the per-UoW axis this session deliberately bypasses

<!-- Design: claude/design/Durability/BulkLoad/README.md -->
<!-- ADR: 053-bulk-load-write-path — claude/adr/053-bulk-load-write-path.md -->
<!-- Rules: claude/rules/durability.md — module BulkLoad (BL-01..04) -->
