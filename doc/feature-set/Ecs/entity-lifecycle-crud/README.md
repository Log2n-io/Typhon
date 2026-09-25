---
uid: feature-ecs-entity-lifecycle-crud-index
title: 'Entity Lifecycle & CRUD API'
description: 'Zero-copy EntityRef / EntityRefMut handles for Spawn, Open, Read, Write, Destroy, Enable/Disable — the sole entity manipulation API.'
---

# Entity Lifecycle & CRUD API
> Zero-copy EntityRef / EntityRefMut handles for Spawn, Open, Read, Write, Destroy, Enable/Disable — the sole entity manipulation API.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Every entity touchpoint — create, read, mutate, delete, toggle a component — needs to resolve "where is this
entity's data" before doing anything useful. A naive API re-resolves that on every call; for code that reads or
writes several components on the same entity, that's redundant hashmap work per component.

## ⚙️ How it works (in brief)

`tx.Open(id)` / `tx.OpenMut(id)` probe the entity's per-archetype LinearHash once, check MVCC visibility at the
transaction's TSN, and return a handle — a `ref struct` caching the per-slot component locations. The handle's
type carries the access: `Open` / `TryOpen` return a read-only `EntityRef`, `OpenMut` / `TryOpenMut` an
`EntityRefMut` that also writes (and converts implicitly to `EntityRef`). Writing through a read-only open does not
compile. From there, `Read<T>(Comp<T>)` / `Write<T>(Comp<T>)` resolve a component slot in O(1) and return a typed
ref straight into chunk or cluster memory: `Versioned` writes copy-on-write into a new revision, `SingleVersion`/`Transient`
writes mutate in place. At `Spawn`, an omitted `Versioned` component is *absent* — no chunk and no revision
chain are allocated for it — while an omitted `SingleVersion`/`Transient` component is zero-initialized and
disabled. The entity is staged invisibly until commit; `Destroy` tombstones it
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

// ─── Spawn — supply any subset; omitted Versioned components are absent (not zero-init) ───
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
EntityRefMut m = wtx.OpenMut(id);
ref Position p = ref m.Write(Unit.Pos);
p.X += 1f;
m.Disable(Unit.Stats);          // O(1) bit flip — data preserved, not freed, instantly re-enable-able
wtx.Commit();

// ─── Maybe-stale target (a stored id, a link) — one resolve, no exception ───
using var ttx = dbe.CreateQuickTransaction();
if (ttx.TryOpenMut(id, out EntityRefMut t))
{
    t.Write(Unit.Stats).Health -= 10;
}
bool alive = ttx.IsAlive(id);   // the existence probe — a lookup, no open
ttx.Commit();

// ─── Destroy — tombstones now (cascade-deletes configured children); freed later by GC ───
using var dtx = dbe.CreateQuickTransaction();
dtx.Destroy(id);
dtx.Commit();
```

## ⚠️ Guarantees & limits

- One LinearHash probe per `Open`/`OpenMut`/`TryOpen`/`TryOpenMut`, amortized across every subsequent `Read`/`Write`
  on that handle (~1-5ns per component for `SingleVersion`/`Transient`). Measured open-plus-one-access, warm caches,
  50 000 cluster entities in spawn order, Ryzen 9 7950X (`Typhon.Benchmark --aa-bench`, 2026-09-25): ~90 ns for
  `tx.Open` + `Read`, ~100 ns for `tx.OpenMut` + `Write`, ~55 ns / ~75 ns through `tx.For<T>()`. Expect more when
  the lookup misses the CPU cache (random ids over a large table). For "write it if it still
  exists", `TryOpenMut` is that one probe — not `TryOpen` followed by `OpenMut`.
- Every accessor — `Transaction` / `EntityAccessor`, `PointInTimeAccessor` workers, `ArchetypeAccessor<TArch>`
  (`tx.For<TArch>()`) — has the same five: `Open`/`OpenMut` throw on a missing or invisible entity,
  `TryOpen`/`TryOpenMut` return `false`, `IsAlive` is the existence probe. `BulkLoadSession` exposes the `Mut` pair.
  `ArchetypeAccessor` treats its transaction's pending destroys as missing, like the transaction does, but does not
  see the transaction's own spawns: they are not in the EntityMap until commit.
- `OpenMut`/`TryOpenMut` run the transaction's mutation check first, found or not: on a read-only or finished
  transaction they throw.
- `EntityRef` and `EntityRefMut` are `ref struct`s — stack-only, cannot escape their creating accessor/transaction,
  cannot be stored in a field or passed across threads. `EntityRefMut` → `EntityRef` is an implicit copy, so
  read-only helpers take an `EntityRef`.
- `Write<T>` is the dirty boundary — marks dirty (or stages copy-on-write) the moment it's called; no separate
  `MarkDirty` step.
- Writing a `Versioned` component requires a full `Transaction` — a bare `EntityAccessor` or `PointInTimeAccessor`
  worker accessor throws on `Write` to a `Versioned` slot (read-only there).
- `Spawn`/`SpawnBatch` entities are invisible to other transactions until commit (`BornTSN = commit TSN`);
  `Destroy` only tombstones (`DiedTSN = commit TSN`) — entries/chunks are reclaimed by deferred GC once no live
  transaction can still see the entity.
- Enable/Disable never frees or reallocates a chunk — data is preserved for an immediate, zero-cost re-enable
  (`Disable` then `Enable` alone, no value required). Three states: *enabled*, *disabled* (value stored, O(1) re-enable),
  and *absent* (Versioned component never supplied at Spawn — `Enable(comp)` throws; use `Enable(comp, in value)`).
  Zero overhead unless a concurrent transaction is mid-`Enable`/`Disable`.
- There is no `tx.Read<T>(id)` shorthand — always `Open`/`OpenMut` first to obtain a handle.
- There is no flat CRUD API (`CreateEntity`/`ReadEntity`/`UpdateEntity`/`DeleteEntity`) — `EntityRef` /
  `EntityRefMut` are the only entity manipulation path.

## 🧪 Tests

- [EntitySpawnTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntitySpawnTests.cs) — Spawn/Open/OpenMut/Read/Write core paths, `TryOpen` on a stale id, rollback-doesn't-leak-chunks
- [EntityDestroyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityDestroyTests.cs) — Destroy tombstoning, visibility after commit vs. same-transaction, cascade through `EntityLink`
- [EnableDisableTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EnableDisableTests.cs) — Enable/Disable bit-flip semantics, data preservation across re-enable, MVCC visibility of enabled-bits history
- [EntityRefMutTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityRefMutTests.cs) — the read-only / writable type split, `TryOpenMut` / `IsAlive` on every accessor, the mutation check on a read-only transaction

## 🔗 Related

- Sub-features: [Generated Multi-Component Accessors](./generated-multi-component-accessors.md), [Batch & SoA Spawn](./batch-soa-spawn.md), [Enable/Disable Components](./enable-disable-components.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md, claude/design/Ecs/entity-accessor-comparison.md -->
