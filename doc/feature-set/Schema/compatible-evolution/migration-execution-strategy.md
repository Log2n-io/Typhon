---
uid: feature-schema-compatible-evolution-migration-execution-strategy
title: 'Migration Execution Strategy'
description: 'Migration runs eagerly and synchronously at database open — before any user transaction — with progress events and an offline dry-run check.'
---

# Migration Execution Strategy
> Migration runs eagerly and synchronously at database open — before any user transaction — with progress events and an offline dry-run check.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Schema](../README.md)

## 🎯 What it solves

A schema migration has to happen *somewhere* in the lifecycle, and the choice has real consequences: migrate
lazily on first read and every read path forever carries a "which layout is this?" branch; migrate eagerly and
startup pays a one-time, predictable cost instead. Typhon commits to eager migration, but that only helps if
callers can reason about it — know it blocks startup, know it's safe to retry after a crash, and (for large
databases) get visibility into how far along it is instead of an opaque hang.

## ⚙️ How it works (in brief)

`RegisterComponentFromAccessor<T>()` performs migration **inline and synchronously** — it does not return until
every affected entity has been migrated (or an exception is thrown). The engine flushes the migration's own
`ChangeSet` to disk and repoints the database's root metadata to the new segments *before* WAL replay begins;
if the process crashes mid-migration, the old segments are still authoritative and the next open re-runs
migration from scratch (rule: migration completion is all-or-nothing per reopen). While running, the engine
raises `DatabaseEngine.OnMigrationProgress` synchronously on the calling thread, once per `MigrationPhase`
transition, so you can log or display progress without polling. Separately, `DatabaseSchema.ValidateEvolution()`
runs the same diff/classification logic offline against the database file — no engine instance, no data
mutated — to answer "would this reopen succeed, and what would it cost?" before you deploy.

## 💻 Usage

```csharp
// Subscribe before registering — events fire synchronously inside RegisterComponentFromAccessor<T>().
using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
dbe.OnMigrationProgress += (_, args) =>
{
    log.LogInformation("{Component} migration: {Phase} ({Done}/{Total}, {Pct:F0}%)",
        args.ComponentName, args.Phase, args.EntitiesMigrated, args.TotalEntities, args.PercentComplete);
};

dbe.RegisterComponentFromAccessor<PlayerV2>();   // blocks until migration completes, or throws
```

Check feasibility offline, before shipping the new binary against production data:

```csharp
var result = DatabaseSchema.ValidateEvolution("game.tdb", registrar =>
{
    registrar.RegisterComponent<PlayerV2>();
});

if (!result.IsValid)
{
    foreach (var err in result.Errors)
    {
        log.LogError("Schema evolution blocked: {Error}", err);
    }
}
```

`MigrationPhase` values, in the order they're raised:

| Phase | Meaning |
|---|---|
| `Analyzing` | SchemaDiff computed, change classified `Compatible`/`CompatibleWidening`/`Breaking` |
| `AllocatingSegments` | New stride segment(s) allocated |
| `MigratingEntities` | Per-entity field copy / zero-fill / widening in progress |
| `RecreatingRevisionChain` | New HEAD-only revision chain written |
| `Flushing` | Migration's `ChangeSet` flushed to disk before WAL replay |
| `Complete` | Migration done; new segments are now authoritative |

(`BuildingNewIndexes` and `UpdatingMetadata` are reserved `MigrationPhase` values not currently emitted by the engine — index builds and metadata persistence happen inline within the phases above.)

## ⚠️ Guarantees & limits

- Fully synchronous and blocking — there is no background or async migration mode; `RegisterComponentFromAccessor<T>()` owns the calling thread until done.
- Runs once per affected component per reopen, before WAL replay and before any user transaction can start — no caller can ever observe a partially-migrated entity.
- `OnMigrationProgress` fires on the calling thread in monotonically non-decreasing `MigrationPhase` order, always starting at `Analyzing` and ending at `Complete` — safe to drive a log/progress bar, not a substitute for cross-thread coordination.
- Crash mid-migration is safe to retry: the database's root metadata only repoints to the new segments after migration's own flush succeeds, so a crash before that leaves the prior (pre-migration) segments authoritative and the next open re-runs migration in full.
- `DatabaseSchema.ValidateEvolution()` mutates nothing — it's read-only analysis against the file, suitable for a pre-deploy CI check.
- Cost is proportional to entity count and paid once at startup (sub-millisecond to tens of milliseconds for typical sizes; an added index is the only path with O(N) B+Tree cost) — there is no incremental or amortized migration for very large databases today.

## 🧪 Tests

- [OperationalToolingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/OperationalToolingTests.cs) — `MigrationProgress_EventsFiredInOrder` (monotonic phase sequence, `Analyzing`→`Complete`), `ValidateEvolution_CompatibleChange_IsValid` (offline dry-run, no mutation)
- [SchemaEvolutionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/SchemaEvolutionTests.cs) — `BulkMigration_Performance` times a 10K-entity eager migration, illustrating the synchronous startup cost

## 🔗 Related

- Parent feature: [Compatible Schema Evolution (Auto-Migration)](./README.md)

<!-- Deep dive: claude/design/Schema/03-compatible-evolution.md §10 Why Eager Migration, claude/design/Schema/04-migration-functions.md §4 Execution Strategy -->
