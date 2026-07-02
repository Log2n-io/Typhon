# UoW Identity & Crash-Safe Recovery Boundary
> A bounded 15-bit ID stamps every revision a UoW writes — crash recovery erases unconfirmed work instantly, with no replay.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Transactions](./README.md)

## 🎯 What it solves
After a crash, some revisions on disk may belong to work the application never got confirmation for. The read
path needs an O(1) way to tell "this revision belongs to a UoW that survived the crash" from "this revision
belongs to one that didn't" — without re-validating or replaying every individual write. The same identity also
doubles as the resource the engine uses to cap how many UoWs can be in flight at once, so an app that opens UoWs
faster than it closes them gets pushed back instead of growing the engine's in-flight state without bound.

## ⚙️ How it works (in brief)
`CreateUnitOfWork` draws a UoW ID (1–32767) from a persistent registry, bitmap-allocated for O(1) claim/release.
That ID is stamped on every revision any `Transaction` inside the UoW commits. On reopen after a crash, the
registry's on-disk slot state is cross-referenced against the durable WAL: a UoW whose work made it to durable
WAL is kept; a UoW still `Pending` is marked `Void` — every revision carrying its ID becomes invisible to readers
immediately, with no per-row undo. ID `0` is never handed out; it is a reserved sentinel meaning "always
committed," used internally for writes that aren't UoW-scoped. When all slots are in use, `CreateUnitOfWork`
blocks (no busy-spin) until one frees or its timeout elapses, then throws.

## 💻 Usage
```csharp
try
{
    using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit, timeout: TimeSpan.FromSeconds(2));
    Console.WriteLine($"UoW id: {uow.UowId}");          // 1..32767 while this UoW is alive

    using var tx = uow.CreateTransaction();
    tx.Spawn<Position>(Position.X.Set(1.0f));
    tx.Commit();                                         // revision stamped with uow.UowId
}
catch (ResourceExhaustedException ex)
{
    // Every UoW registry slot was in use and `timeout` expired before one freed.
    log.LogWarning("UoW registry saturated: {Usage}/{Limit}", ex.CurrentUsage, ex.Limit);
}
```

| Option | Default | Effect |
|---|---|---|
| `timeout` (on `CreateUnitOfWork`) | `TimeoutOptions.Current.DefaultUowTimeout` (30s) | Max wait for a free UoW ID before throwing `ResourceExhaustedException`; also becomes the new UoW's own deadline. |

## ⚠️ Guarantees & limits
- **32,767 concurrent UoWs max** — the hard ceiling of the 15-bit ID space (ID `0` is reserved and never assigned
  to a UoW).
- **Voiding is per-UoW, not per-transaction** — if a UoW is still `Pending` when the crash happens, every revision
  stamped with its ID is voided together, even if individual `Transaction.Commit()` calls inside it had already
  returned successfully; the crash-safety contract belongs to the UoW, not to any one transaction inside it.
- **No replay needed to hide voided work** — a voided UoW's revisions disappear via the ID check alone; nothing is
  rewritten, scanned, or undone row by row.
- **Slot recycling waits on the oldest active reader** — a UoW ID returns to the free pool only once no live
  transaction's snapshot can still observe its revisions; a long-lived read transaction can stall new-UoW
  admission under sustained load.
- **Back-pressure is enforced, not advisory** — `CreateUnitOfWork` throws `ResourceExhaustedException` once the
  timeout expires with no free slot; this is the engine's defense against an unbounded number of simultaneously
  open UoWs.
- **ID reservation, not ID identity** — a UoW's ID is only meaningful while it is allocated; once released, a
  later, unrelated UoW can be assigned the same value.

## 🧪 Tests
- [UowRegistryTests](../../../test/Typhon.Engine.Tests/Execution/UowRegistryTests.cs) — ID allocation/release,
  crash-recovery voiding of `Pending` entries (`Registry_CrashRecovery_VoidsPending`,
  `Registry_TwoPhaseRecovery_FullWorkflow`), back-pressure wait/timeout/cancellation, capacity growth beyond the
  initial registry size

## 🔗 Related
- Related features: [Unit of Work (durability boundary)](./unit-of-work.md), [Commit/Rollback Pipeline](./commit-rollback-pipeline.md), [Durability Modes](./durability-modes/README.md)

<!-- Deep dive: claude/design/Transactions/05-unit-of-work.md §6 — UoW Registry, claude/rules/durability.md — Module: UoW Registry (UR-01..UR-07) -->
<!-- Overview: claude/overview/02-execution.md — UoW Lifecycle / UoW ID Recycling (#uow-id-recycling) -->
