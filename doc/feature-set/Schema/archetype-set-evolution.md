---
uid: feature-schema-archetype-set-evolution
title: 'Archetype Component-Set Evolution'
description: 'Add, insert or reorder a component of a populated archetype: the open migrates every entity in place, the added component absent, and the first tick moves nothing.'
---

# Archetype Component-Set Evolution
> Add, insert or reorder a component of a populated archetype: the open migrates every entity in place, the added component absent, and the first tick moves nothing.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Schema](./README.md)

## 🎯 What it solves

An archetype's component set is part of its schema: it fixes the cluster geometry every entity is stored in. A long-lived world eventually gains a
component — a `Player` that needs `PlayerGear`, then `PlayerBuffs`. Without a migration the only answer is to recreate the world. And a set that changed
without anyone noticing — a `Register<T>()` line moved, one component swapped for another of the same count — must never open reading every column at the
wrong offset. Archetype component-set evolution makes the first case a routine schema migration at open, and the second a refusal.

## ⚙️ How it works (in brief)

Every archetype row stores its components' names in slot order. At open, each archetype is matched against its row **by component name** (through the
rename journal), never by slot index. A component added — appended, inserted mid-list, or added to a parent archetype — or a reorder, together with a
higher `[Archetype(Revision)]`, migrates the archetype: the stored cluster layout is rebuilt from the persisted catalog, and every entity is re-placed into
the new layout in its old order, each surviving component carried across by name. The added component is **absent** on every existing entity, exactly
as for a spawn that did not supply it. Spatial archetypes keep their entities grouped by home cell and realm, so no entity becomes a stray and the first
tick fence migrates nothing. The new archetype row commits with the open, in the same single meta flip as component migrations; a crash before it leaves
the pre-migration file, and the next open migrates again.

## 💻 Usage

```csharp
// Revision 3, already in the world:
[Archetype(3, Name = "Player")]
class PlayerV3 : Archetype<PlayerV3>
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<Stats> Stats = Register<Stats>();
}

// Revision 4 gains a component. Both classes may stay in source: the highest revision whose components are registered is opened.
[Archetype(4, Name = "Player")]
class Player : Archetype<Player>
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<Stats> Stats = Register<Stats>();
    public static readonly Comp<PlayerGear> Gear = Register<PlayerGear>();
}

dbe.RegisterComponentFromAccessor<Position>();
dbe.RegisterComponentFromAccessor<Stats>();
dbe.RegisterComponentFromAccessor<PlayerGear>();
dbe.InitializeArchetypes();                       // migrates Player 3 -> 4

foreach (var m in dbe.LastOpenArchetypeSetMigrations)
{
    log.LogInformation("{Name} {From}->{To}: {Changes}, {Count} entities", m.ArchetypeName, m.FromRevision, m.ToRevision, m.Changes, m.EntitiesMigrated);
}

// Existing players have no gear yet: supply it when the game decides.
tx.OpenMut(player).Enable(Player.Gear, new PlayerGear { Weapon = sword });
```

| Option | Default | Effect |
|---|---|---|
| `DatabaseEngineOptions.ArchetypeSetMigration = Apply` | Default | Migrate at open |
| `DatabaseEngineOptions.ArchetypeSetMigration = Refuse` | — | Skip an archetype that would migrate and leave the file as stored; it is listed in `RefusedArchetypeSetChanges`. For inspectors (the Workbench uses it) |

`tsh migrate` reports the archetypes it migrated; `tsh schema-validate` lists the ones the next open would migrate, skip (a component not registered)
or refuse, without changing the file. Refusals that depend on the data — an added unique index, position, realm key or Versioned component on a populated
archetype — are named as not checked: only the open reads the entities.

## ⚠️ Guarantees & limits

- Slots are matched by component name. A set or order change **without** a revision increase is refused, a same-count swap included; nothing of the
  archetype is touched.
- Removing a component is refused in this version, as is a lower runtime revision (the file was written by a newer build).
- No default or backfill: an added component is absent — a Versioned one has no value until `Enable(comp, value)` (`Enable(comp)` alone throws), a SingleVersion one reads zero
  once `Enable(comp)` turns it on.
- An added component cannot carry a populated spatial archetype's position or realm key, nor a unique non-sparse index: those opens are refused. After
  a crash, the entities the WAL replays count as populating it too.
- A crash open under `Refuse` whose WAL holds records of an archetype the policy skipped fails with error 7013, nothing applied: open with `Apply`.
- Entity ids and keys are preserved, so `EntityLink`s held elsewhere still resolve.
- A non-spatial archetype is packed densely; a spatial one never mixes two home cells in one cluster, which can cost up to ~1.5x a fresh world's
  cluster count in the worst case, about 1.0x in the common one.
- The migration runs synchronously in `InitializeArchetypes`, in time linear in the entity count.

## 🧪 Tests

- [ArchetypeSetMigrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/ArchetypeSetMigrationTests.cs) — adds per storage mode, mid-list and parent inserts, enabled states by name, indexes, journal, integrity scan
- [ArchetypeSchemaValidationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/ArchetypeSchemaValidationTests.cs) — refusals, renames, duplicate names, the `Refuse` policy
- [ArchetypeSetMigrationSpatialTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/ArchetypeSetMigrationSpatialTests.cs) — SWG-shaped realm-keyed world: zero strays, first fence migrates nothing
- [MigrationTopologyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/MigrationTopologyTests.cs) — old-order placement, density, geometry validation
- [ArchetypeSetMigrationCrashTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/ArchetypeSetMigrationCrashTests.cs) — commit stages, WAL window under the old set

## 🔗 Related

- Related feature: [Compatible Schema Evolution](./compatible-evolution/README.md) (the component-level migration it extends)
- Related feature: [Schema Validation on Reopen](./schema-validation.md)
- Related feature: [tsh Schema Shell Commands](./tsh-schema-commands.md)

<!-- Overview: claude/overview/04-data.md §4.10 Schema Evolution — Archetype Component-Set Evolution -->
<!-- Design: claude/design/Schema/07-archetype-set-evolution.md -->
