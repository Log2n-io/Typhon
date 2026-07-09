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

[Archetype(11)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

using var tx = dbe.CreateQuickTransaction();
var id = tx.Spawn<Unit>(Unit.Pos.Set(new Position { X = 0, Y = 0, Z = 0 }));
tx.Commit();

using var tx2 = dbe.CreateQuickTransaction();
var e = tx2.OpenMut(id);
ref var pos = ref e.Write(Unit.Pos);
pos.X += dtVelocityX;          // in-place — visible to every reader immediately, no commit needed for visibility
tx2.Commit();

// Once per game tick, after all systems have run:
dbe.WriteTickFence(tickNumber);  // batches every dirty SingleVersion component to WAL — the crash-recovery boundary
```

## ⚠️ Guarantees & limits

- Write cost ~3-10 ns — an in-place store, no allocation, no revision chain.
- Crash recovery to the last completed `WriteTickFence` call — up to one tick of writes can be lost, but state
  is never torn or corrupted.
- Forgetting to call `WriteTickFence` silently degrades a `SingleVersion` component to `Transient`-like
  durability (no crash recovery) — it never corrupts data.
- No MVCC isolation: last-writer-wins, and `tx.Rollback()` does **not** revert a `SingleVersion` write already
  applied in-place.
- `ReadsSnapshot` is rejected at scheduler `Build()` time for `SingleVersion` components — use `Versioned` for
  snapshot reads.
- Secondary B+Tree indexes and spatial structures are reconciled at the tick-fence boundary (deferred), not
  synchronously on every write.
- Need atomicity and zero loss for one write without paying for snapshot isolation? See
  [Committed Durability Discipline](./storage-mode-committed.md) — it escalates a `SingleVersion` write to
  commit-time durability, closing the ≤1-tick loss window.

## 🧪 Tests

- [StorageModeTickFenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/StorageModeTickFenceTests.cs) — `WriteTickFence` dirty-bitmap serialization, Versioned/Transient correctly skipped
- [TickFenceE2ETests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/TickFenceE2ETests.cs) — crash/reopen recovery to the last completed tick fence, multi-entity and multi-update recovery

## 🔗 Related

- Code: `src/Typhon.Engine/Ecs/internals/DirtyBitmap.cs`, `src/Typhon.Engine/Ecs/internals/DirtyBitmapRing.cs`
- Sub-feature: [Committed Durability Discipline](./storage-mode-committed.md)
- Sibling: [Durability Modes](../../Durability/durability-modes/README.md) — the separate UoW-level commit-durability spectrum; tick-fence durability here is a distinct, component-level mechanism
- Parent feature: [Storage Modes](./README.md)

<!-- Deep dive: claude/design/Ecs/06-storage-modes.md, claude/design/Ecs/07-durability.md — WAL Tick Fence -->
