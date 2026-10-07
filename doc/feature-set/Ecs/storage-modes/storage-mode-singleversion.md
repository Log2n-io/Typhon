---
uid: feature-ecs-storage-modes-storage-mode-singleversion
title: 'SingleVersion (Tick-Fence Durability)'
description: 'In-place writes at near-zero cost, durable to the last completed game tick.'
---

# SingleVersion (Tick-Fence Durability)
> In-place writes at near-zero cost, durable to the last completed game tick.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](../README.md)

## 🎯 What it solves

High-frequency component data — position, velocity, health, cooldowns — gets rewritten every tick by every
entity. Paying `Versioned`'s copy-on-write and revision-chain cost on every such write would dominate the frame
budget for data that naturally tolerates losing the last few milliseconds on a crash. `SingleVersion` gives
near-Flecs/DOTS write performance while remaining on disk and recoverable, just to a coarser durability
boundary than `Versioned`.

## ⚙️ How it works (in brief)

A `SingleVersion` component has exactly one HEAD slot per entity — writes overwrite it in place, last-writer-
wins, immediately visible to every reader (no isolation). Each write sets a bit in a per-entity dirty bitmap. At
the end of each game tick, `DatabaseEngine.WriteTickFence(tickNumber)` serializes every dirty `SingleVersion`
entity to the WAL as a tick-fence record, establishing a crash-recovery boundary. A crash recovers state as of
the last completed tick fence — at most one tick of writes is lost, never corrupted (the WAL record holds
complete post-tick values and overwrites any torn on-disk state).

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
public struct Position
{
    [Index] public int Zone;
    public float X, Y, Z;
}

[Archetype]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

using var tx = dbe.CreateQuickTransaction();
var id = tx.Spawn<Unit>(Unit.Pos.Set(new Position { X = 0, Y = 0, Z = 0 }));
tx.Commit();

using var tx2 = dbe.CreateQuickTransaction();
var e = tx2.OpenMut(id);
var pos = e.Read(Unit.Pos);
pos.X += dtVelocityX;
e.Set(Unit.Pos, pos);          // in-place — visible to every reader immediately, no commit needed for visibility
tx2.Commit();

// Once per game tick, after all systems have run:
dbe.WriteTickFence(tickNumber);  // batches every dirty SingleVersion component to WAL — the crash-recovery boundary
```

## ⚠️ Guarantees & limits

- Write cost ~40 ns — an in-place store into the pinned page (no allocation, no revision chain); ~6× cheaper than a `Versioned` write.
- Crash recovery to the last completed `WriteTickFence` call — up to one tick of writes can be lost, but state
  is never torn or corrupted.
- Forgetting to call `WriteTickFence` silently degrades a `SingleVersion` component to `Transient`-like
  durability (no crash recovery) — it never corrupts data.
- No MVCC isolation: last-writer-wins, and `tx.Rollback()` does **not** revert a `SingleVersion` write already
  applied in-place — *unless* the transaction was opened with the `Commit` discipline, which stages the write
  and therefore does roll it back, in O(1). See the sub-feature below.
- `ReadsSnapshot` is rejected at scheduler `Build()` time for `SingleVersion` components — use `Versioned` for
  snapshot reads.
- Secondary B+Tree indexes and spatial structures are reconciled at the tick-fence boundary (deferred), not
  synchronously on every write. What an indexed query returns in between is defined below.
- Need **rollback**, atomicity, or zero loss on one `SingleVersion` write without paying for snapshot isolation?
  See [Commit Discipline](./storage-mode-committed.md). Because it stages the write instead of applying it in
  place, it buys all three at once: `Rollback` reverts it, the write is all-or-nothing, and the ≤1-tick loss
  window closes.

### Indexed queries between a write and the tick fence

A write changes the value at once. The secondary index — and the per-cluster value ranges a scan uses to skip clusters —
catch up only at `WriteTickFence`. An indexed query (`WhereField`) that runs in between answers like this:

| | |
|---|---|
| **Never a wrong row** | Every entity returned is live and satisfies the condition on its **current** value — whatever path the planner picks, and for `Execute`, `Count`, `Any` and `ExecuteOrdered` alike (see the known exceptions below). |
| **Possibly a missing row** | An entity whose value **started** matching since the last fence may be missing until the fence. |
| **Ordered results** | A written entity that still matches is placed by its value **as of the last fence**. `Skip` / `Take` count only the rows returned. |
| **After `WriteTickFence`** | Exact. |

Player P's indexed `Score` goes 100 → 250 mid-tick, and these queries run before the fence:

| Query | Result |
|---|---|
| `Score == 100` | P is **not** returned |
| `Score == 250` | P **may be missing** |
| `Score >= 0`, ordered by `Score` | P is returned, at the position of **100** |
| any of these after `WriteTickFence` | exact: P under 250, ordered at 250 |

**Not affected** — the index is updated at commit, so there is no window: writes under the
[Commit discipline](./storage-mode-committed.md), `Versioned` components, and spawns.
**`Transient`** components follow the same rule as this page.

**Need the new value inside the tick?** Write that component under the Commit discipline, or call `WriteTickFence` before
querying. The engine does not look the new values up for you: the index cannot see them until the fence, and finding them
otherwise means checking every entity written since the fence on every query — about +0.9 ms per lookup with 100 000
entities written, where the lookup itself costs ~30 µs.

**What the guarantee costs:** nothing when, since the last fence, the component the query filters on was not written and
no entity of the archetype was destroyed — a tick that only moves positions leaves a query on an indexed `Score`
untouched. (A component-less `MarkCurrentDirty` counts as writing every component.) Otherwise the entities written since the
fence are re-tested against their current value; measured over 1 000 000 rows, that is not measurable on point and range
lookups and +5–9 % on a non-unique key returning 1 000 rows.

**Known exceptions** (bugs, tracked):
- An ordered query ignores a `!=` condition, whether or not anything was written (#1185).
- An ordered query on an archetype whose only indexed component is `Transient` returns nothing (#1186).
- A write through the bulk cluster API (`ClusterRef.GetSpan` / `Get`) is never seen by the indexes, not even at the
  fence: write indexed fields through `OpenMut(...).Set(...)` until #1187 is fixed.
- An entity destroyed after a write, whose slot a spawn reuses before the fence: an ordered query returns the new entity
  twice, and after the fence the indexes answer keys no entity holds (#1188).
- `Transaction.EnumerateIndex` and foreign-key navigation read the index without testing values; they are not queries and
  return index keys as of the last fence.

## 🧪 Tests

- [StorageModeTickFenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/StorageModeTickFenceTests.cs) — `WriteTickFence` dirty-bitmap serialization, Versioned/Transient correctly skipped
- [TickFenceE2ETests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/TickFenceE2ETests.cs) — crash/reopen recovery to the last completed tick fence, multi-entity and multi-update recovery
- [QueryBetweenWriteAndFenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/QueryBetweenWriteAndFenceTests.cs) — every case of the table above, on each query path, plus the Commit-discipline and `Versioned` controls

## 🔗 Related

- Code: `src/Typhon.Engine/Ecs/internals/DirtyBitmap.cs`
- Sub-feature: [Commit Discipline](./storage-mode-committed.md)
- Sibling: [Durability Modes](../../Durability/durability-modes/README.md) — the separate UoW-level commit-durability spectrum; tick-fence durability here is a distinct, component-level mechanism
- Parent feature: [Storage Modes](./README.md)

<!-- Deep dive: claude/design/Ecs/06-storage-modes.md, claude/design/Ecs/07-durability.md — WAL Tick Fence -->
