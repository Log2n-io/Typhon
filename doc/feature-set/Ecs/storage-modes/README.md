# Storage Modes
> Pick durability and write cost per component type — from microsecond ACID to nanosecond scratch memory, in one engine.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Game and server workloads mix data with wildly different needs: a position updated every tick can tolerate
losing a few frames, while inventory or currency can never be lost or read half-written. Forcing all of it
through one storage tier means either paying full ACID cost for high-frequency data or risking real state on a
fast-but-loose tier. Storage Modes let each *component type* — not the whole database — declare its own point
on the durability/performance spectrum, so a single entity can mix a `Versioned` wallet with a `SingleVersion`
position and a `Transient` animation cursor.

## ⚙️ How it works (in brief)

`StorageMode` is set via `[Component(StorageMode = ...)]` at registration and is immutable for the life of the
database (changing it requires recreating the database). `Versioned` keeps a full MVCC revision chain
(snapshot isolation, zero loss); `SingleVersion` stores one in-place HEAD slot with WAL tick-fence durability
(≤1 tick loss); `Transient` is heap-only and never persisted. All three are read and written through the same
`EntityRef.Read<T>()` / `Write<T>()` calls — only the cost and guarantees differ, not the API. A runtime
durability discipline (`Committed`) layers commit-time, zero-loss atomicity onto the `SingleVersion` layout
without paying for a revision chain.

## Sub-features

| Sub-feature | Use it for | Write cost (Zen 4)            | Durability |
|-------------|-----------|-------------------------------|------------|
| [Versioned](./storage-mode-versioned.md) | Inventory, economy, progression, anything needing snapshot isolation or AS-OF reads | ~150-360 ns                   | Zero loss, full ACID |
| [SingleVersion (Tick-Fence Durability)](./storage-mode-singleversion.md) | Position, velocity, health, cooldowns — high-frequency, loss-tolerant | ~3-10 ns                      | ≤1 tick loss |
| [Transient](./storage-mode-transient.md) | Animation state, input buffers, pathfinding scratch, targeting info | ~3-5 ns                       | None — gone on crash |
| [Committed Durability Discipline](./storage-mode-committed.md) | A `SingleVersion` write that must be atomic and zero-loss without MVCC (teleport, item pickup, currency debit) | ~25 ns stage / ~60 ns publish | Zero loss, atomic, no chain |

## ⚠️ Guarantees & limits

- `StorageMode` is immutable once a component is registered — there is no in-place migration between modes.
- An entity can freely mix component types across all three modes (`Spawn`/`Open`/`OpenMut`/`Destroy` work
  uniformly); rollback semantics differ per mode — see each sub-feature.
- Indexes, spatial queries, and ECS lifecycle (Spawn/Destroy/Enable) are available in all three modes, but with
  different freshness/synchronicity guarantees — check the Storage Mode Feature Matrix (linked below) before
  committing to a mode.
- The mode choice is silent at the API level — picking `Versioned` for scratch data overpays, picking
  `SingleVersion` for currency under-protects. There is no compiler or runtime guard for "wrong mode for this
  data."

## 🧪 Tests

- [StorageModeReadWriteTests](../../../../test/Typhon.Engine.Tests/Data/StorageModeReadWriteTests.cs) — an entity mixing all three modes, uniform `Read`/`Write` across them, per-mode rollback/dirty-bitmap behavior
- [StorageModeInfrastructureTests](../../../../test/Typhon.Engine.Tests/Data/StorageModeInfrastructureTests.cs) — `[Component(StorageMode = ...)]` attribute defaults/overrides, per-mode segment allocation differences

## 🔗 Related

- Sub-features: [Versioned](./storage-mode-versioned.md), [SingleVersion (Tick-Fence Durability)](./storage-mode-singleversion.md), [Transient](./storage-mode-transient.md), [Committed Durability Discipline](./storage-mode-committed.md)

<!-- Deep dive: claude/design/Ecs/06-storage-modes.md, claude/design/Ecs/01-motivation.md, claude/overview/04-data.md — Storage Mode Feature Matrix -->
