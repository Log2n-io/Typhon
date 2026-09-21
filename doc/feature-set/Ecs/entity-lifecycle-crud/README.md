---
uid: feature-ecs-entity-lifecycle-crud-index
title: 'Entity Lifecycle & CRUD API'
description: 'Zero-copy EntityRef accessor for Spawn, Open, Read, Write, Destroy, Enable/Disable — the sole entity manipulation API.'
---

# Entity Lifecycle & CRUD API
> Zero-copy EntityRef accessor for Spawn, Open, Read, Write, Destroy, Enable/Disable — the sole entity manipulation API.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Every entity touchpoint — create, read, mutate, delete, toggle a component — needs to resolve "where is this
entity's data" before doing anything useful. A naive API re-resolves that on every call; for code that reads or
writes several components on the same entity, that's redundant hashmap work per component.

## ⚙️ How it works (in brief)

`tx.Open(id)` / `tx.OpenMut(id)` probe the entity's per-archetype LinearHash once, check MVCC visibility at the
transaction's TSN, and return an `EntityRef` — a `ref struct` caching the per-slot component locations. From
there, `Read<T>(Comp<T>)` / `Write<T>(Comp<T>)` resolve a component slot in O(1) and return a typed ref straight
into chunk or cluster memory: `Versioned` writes copy-on-write into a new revision, `SingleVersion`/`Transient`
writes mutate in place. `Spawn` allocates all of an archetype's components up front (omitted ones
zero-initialized and disabled) and stages the entity invisibly until commit; `Destroy` tombstones it
(cascade-deleting configured children) — data is freed later by deferred GC, never by the destroying
transaction itself.

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
struct Position { public float X, Y, Z; }

[Component("Game.UnitStats", 1, StorageMode = StorageMode.Versioned)]
struct UnitStats { public int Health, MaxHealth; }

[Archetype]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<UnitStats> Stats = Register<UnitStats>();
}

// ─── Spawn — all components provided up front; omitted ones are zero-init + disabled ───
using var tx = dbe.CreateQuickTransaction();
EntityId id = tx.Spawn<Unit>(
    Unit.Pos.Set(new Position { X = 0, Y = 0, Z = 0 }),
    Unit.Stats.Set(new UnitStats { Health = 100, MaxHealth = 100 }));
tx.Commit();

// ─── Read — one Open() amortized across every component access on `e` ───
using var rtx = dbe.CreateQuickTransaction();
if (rtx.TryOpen(id, out EntityRef e))             // try-pattern — no exception on a stale reference
{
    ref readonly Position pos = ref e.Read(Unit.Pos);
}

// ─── Write — OpenMut once, write/disable several components ───
using var wtx = dbe.CreateQuickTransaction();
EntityRef m = wtx.OpenMut(id);
ref Position p = ref m.Write(Unit.Pos);
p.X += 1f;
m.Disable(Unit.Stats);          // O(1) bit flip — data preserved, not freed, instantly re-enable-able
wtx.Commit();

// ─── Optional target + write — TryOpen is a read-only existence guard ───
using var guarded = dbe.CreateQuickTransaction();
if (guarded.TryOpen(id, out _))
{
    ref Position guardedPos = ref guarded.OpenMut(id).Write(Unit.Pos);
    guardedPos.X += 1f;
    guarded.Commit();
}

// ─── Destroy — tombstones now (cascade-deletes configured children); freed later by GC ───
using var dtx = dbe.CreateQuickTransaction();
dtx.Destroy(id);
dtx.Commit();
```

## ⚠️ Guarantees & limits

- `TryOpen` is the non-throwing form of the read-only `Open`, not of `OpenMut`. An `EntityRef` returned by `TryOpen` cannot be written through.
  When an id may be stale and the caller intends to mutate it, use `TryOpen(id, out _)` as the guard and then `OpenMut(id)` for the writable open.
  Both resolve at the same transaction TSN, so the visibility decision is stable for that transaction. `EntityId` carries the target archetype routing id,
  so the target may belong to a different archetype than a system's input.
- The guarded `TryOpen` + `OpenMut` pattern performs two entity lookups. There is intentionally no `TryOpenMut` API today; adding a single-lookup
  writable try-open is an API/performance decision, not required for correct cross-archetype mutation.
- One LinearHash probe per `Open`/`OpenMut` (~350ns), amortized across every subsequent `Read`/`Write` on that
  `EntityRef` (~1-5ns per component for `SingleVersion`/`Transient`).
- `EntityRef` is a `ref struct` — stack-only, cannot escape its creating accessor/transaction, cannot be stored
  in a field or passed across threads.
- `Write<T>` is the dirty boundary — marks dirty (or stages copy-on-write) the moment it's called; no separate
  `MarkDirty` step.
- Writing a `Versioned` component requires a full `Transaction` — a bare `EntityAccessor` or `PointInTimeAccessor`
  worker accessor throws on `Write` to a `Versioned` slot (read-only there).
- `Spawn`/`SpawnBatch` entities are invisible to other transactions until commit (`BornTSN = commit TSN`);
  `Destroy` only tombstones (`DiedTSN = commit TSN`) — entries/chunks are reclaimed by deferred GC once no live
  transaction can still see the entity.
- Enable/Disable never frees or reallocates a chunk — data is preserved for an immediate, zero-cost re-enable.
  Two-state only (no partial); zero overhead unless a concurrent transaction is mid-`Enable`/`Disable`.
- There is no `tx.Read<T>(id)` shorthand — always `Open`/`OpenMut` first to obtain an `EntityRef`.
- There is no flat CRUD API (`CreateEntity`/`ReadEntity`/`UpdateEntity`/`DeleteEntity`) — `EntityRef` is the
  only entity manipulation path.

## 🧪 Tests

- [EntitySpawnTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntitySpawnTests.cs) — Spawn/Open/OpenMut/Read/Write core paths, `TryOpen` on a stale id, rollback-doesn't-leak-chunks
- [EntityDestroyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityDestroyTests.cs) — Destroy tombstoning, visibility after commit vs. same-transaction, cascade through `EntityLink`
- [EnableDisableTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EnableDisableTests.cs) — Enable/Disable bit-flip semantics, data preservation across re-enable, MVCC visibility of enabled-bits history

## 🔗 Related

- Sub-features: [Generated Multi-Component Accessors](./generated-multi-component-accessors.md), [Batch & SoA Spawn](./batch-soa-spawn.md), [Enable/Disable Components](./enable-disable-components.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md, claude/design/Ecs/entity-accessor-comparison.md -->
