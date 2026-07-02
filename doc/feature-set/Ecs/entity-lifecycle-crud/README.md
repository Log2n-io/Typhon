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

[Archetype(42)]
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

// ─── Destroy — tombstones now (cascade-deletes configured children); freed later by GC ───
using var dtx = dbe.CreateQuickTransaction();
dtx.Destroy(id);
dtx.Commit();
```

## ⚠️ Guarantees & limits

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
- The old flat CRUD API (`CreateEntity`/`ReadEntity`/`UpdateEntity`/`DeleteEntity`) is gone — `EntityRef` is the
  only entity manipulation path.

## 🧪 Tests

- [EntitySpawnTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EntitySpawnTests.cs) — Spawn/Open/OpenMut/Read/Write core paths, `TryOpen` on a stale id, rollback-doesn't-leak-chunks
- [EntityDestroyTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EntityDestroyTests.cs) — Destroy tombstoning, visibility after commit vs. same-transaction, cascade through `EntityLink`
- [EnableDisableTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EnableDisableTests.cs) — Enable/Disable bit-flip semantics, data preservation across re-enable, MVCC visibility of enabled-bits history

## 🔗 Related

- Sub-features: [Generated Multi-Component Accessors](./generated-multi-component-accessors.md), [Batch & SoA Spawn](./batch-soa-spawn.md), [Enable/Disable Components](./enable-disable-components.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md, claude/design/Ecs/entity-accessor-comparison.md -->
