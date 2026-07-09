---
uid: feature-schema-migration-functions
title: 'User-Defined Migration Functions'
description: 'Register a pure transform to carry data through a breaking schema change — type narrowing, semantic conversions, field split/merge.'
---

# User-Defined Migration Functions
> Register a pure transform to carry data through a breaking schema change — type narrowing, semantic conversions, field split/merge.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Schema](./README.md)

## 🎯 What it solves

Schema Validation auto-resolves additions, removals, and lossless type widenings, but some struct evolutions genuinely change the meaning or shape of the data: `int CategoryId` becoming `String64 Category`, `int HealthPercent` (0-100) becoming `float HealthRatio` (0.0-1.0), or one field splitting into two. None of these can be inferred safely — only the application knows the intended conversion. Without an escape hatch, every such change would force a one-off export/import outside the engine. Migration Functions let you register that conversion once, in code, and have it applied to every existing entity the next time the new struct revision is registered.

## ⚙️ How it works (in brief)

You register a `MigrationFunc<TOld, TNew>` (or a byte-level `ByteMigrationFunc` when `TOld` no longer exists in code) before calling `RegisterComponentFromAccessor<TNew>()`. When that call detects a `Breaking` schema change, it looks up a migration path from the persisted revision to the runtime revision — a direct mapping if one was registered, otherwise a multi-step chain resolved automatically across intermediate revisions (e.g. V1→V2→V3 from two registered single-step functions). The chain runs once, eagerly, over every entity at startup; functions are pure value transforms with no database or transaction access. If any entity's transform throws, that entity is logged and skipped, migration continues, and registration fails afterward with the full list — old data is never modified until every entity succeeds.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
struct PlayerV1 { public int Health; public int Mana; }

[Component("Game.Player", 2)]
struct PlayerV2 { public float Health; public int Mana; public int Shield; }

// 1. Register the migration before the new revision is registered.
dbe.RegisterMigration<PlayerV1, PlayerV2>((ref PlayerV1 old, out PlayerV2 new_) =>
{
    new_ = new PlayerV2
    {
        Health = old.Health / 100f,  // semantic conversion: percent -> ratio
        Mana = old.Mana,
        Shield = 0,                  // new field, default value
    };
});

// 2. Register the current revision — triggers validation + migration.
dbe.RegisterComponentFromAccessor<PlayerV2>();
```

Byte-level form, for when `PlayerV1` is no longer compiled into the application:

```csharp
dbe.RegisterByteMigration("Game.Player", fromRevision: 1, toRevision: 2,
    oldSize: 8, newSize: 12,
    (ReadOnlySpan<byte> oldBytes, Span<byte> newBytes) =>
    {
        var health = BitConverter.ToInt32(oldBytes.Slice(0, 4));
        BitConverter.TryWriteBytes(newBytes.Slice(0, 4), health / 100f);
        oldBytes.Slice(4, 4).CopyTo(newBytes.Slice(4, 4));
    });
```

Track progress on large migrations via `dbe.OnMigrationProgress`.

| API | Use when |
|---|---|
| `RegisterMigration<TOld,TNew>(MigrationFunc<TOld,TNew>)` | `TOld` struct is available in code — typed, zero-copy `ref`/`out` |
| `RegisterByteMigration(name, fromRev, toRev, oldSize, newSize, ByteMigrationFunc)` | `TOld` struct no longer exists — manual byte layout |

## ⚠️ Guarantees & limits

- Both registration APIs must run **before** `RegisterComponentFromAccessor<TNew>()` for the target component — migrations are looked up only when a breaking change is detected at registration time.
- Multi-step chains resolve automatically (BFS over registered `(name, fromRev, toRev)` edges) — you only need to register adjacent-revision steps, not every combination.
- A revision with no registered path (direct or chained) throws `SchemaValidationException` at registration, before any data is touched.
- Migration runs once, eagerly, over all entities at startup — there is no lazy/background path. The function should be a fast, allocation-free value transform; if it isn't, that's a signal to optimize the function, not the engine.
- Functions are pure: `ref` access to old data, `out` for new data (or `ReadOnlySpan<byte>`/`Span<byte>` for the byte-level form). No database, transaction, or other-entity access — keeps them fast and testable in isolation.
- A function is sanity-checked once at registration time against zero-initialized input; if it throws there, registration fails immediately with the cause.
- If a function throws for specific entities during the real run, those entities are logged (chunk ID, hex dump of old bytes, exception) and migration continues; afterward the whole call throws `SchemaMigrationException` with every failure. **Old segments are left untouched** in this case — fix the function and re-run; no data is lost.
- Duplicate registration of the same `(componentName, fromRevision, toRevision)` throws `InvalidOperationException`.
- Migration carries only the HEAD revision forward; MVCC history is not replayed through the chain (no active transactions exist at startup, so this doesn't affect snapshot isolation).

## 🧪 Tests

- [MigrationFunctionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/MigrationFunctionTests.cs) — typed single-step and chained (V1→V2→V3) migrations, byte-level migration, per-entity failure logging + `SchemaMigrationException`, missing-path throws, duplicate registration
- [MigrationRegistryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/MigrationRegistryTests.cs) — pure registry: name/revision-ordering validation, BFS chain resolution (direct/multi-step/no-path), zero-init sanity check on registration

## 🔗 Related

- Related feature: [Schema Validation on Reopen](./schema-validation.md) (classifies changes as `Compatible`/`CompatibleWidening`/`Breaking`; only `Breaking` requires a migration function)

<!-- Design: claude/design/Schema/04-migration-functions.md (full API, chain resolution, error handling, MVCC interaction) -->
<!-- Overview: claude/overview/04-data.md §4.10 Schema Evolution -->
