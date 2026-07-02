# Schema & Constraint Violation Exceptions
> Typed failures for "the engine refuses to proceed": incompatible schema, failed migration, version downgrade, duplicate key.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)

## 🎯 What it solves

Some failures aren't transient contention — they mean the data model itself is in a state the engine cannot safely reconcile: a struct changed in a way that can't be auto-migrated, a user-supplied migration function failed partway through, the database was written by a newer binary than the one opening it, or an insert would create a duplicate key in a column declared unique. Silently proceeding in any of these cases risks misreading bytes or corrupting an index. Typhon refuses instead, and reports exactly which case it hit through a dedicated, typed exception rather than a generic error.

## ⚙️ How it works (in brief)

Four exception types sit directly under `TyphonException`, each non-transient (`IsTransient == false` — retrying without changing the schema or the input never helps): `SchemaValidationException` (breaking schema mismatch on reopen, no migration registered — carries the full `SchemaDiff`), `SchemaMigrationException` (a registered migration function threw for one or more entities — carries per-entity `MigrationFailure` records; old segments are left untouched), `SchemaDowngradeException` (persisted schema revision is newer than the runtime struct's — opening with an older binary is refused outright), and `UniqueConstraintViolationException` (an insert/update would duplicate a key already present in a unique index). `SchemaValidationException` and `SchemaDowngradeException` share the `SchemaValidation` error code — both are "this component cannot be opened" from an operator's point of view; the exception type tells you which.

## 💻 Usage

```csharp
try
{
    dbe.RegisterComponentFromAccessor<Player>();
}
catch (SchemaDowngradeException ex)
{
    log.LogCritical("Refusing to open '{Component}': persisted rev {Persisted} > runtime rev {Runtime}",
        ex.ComponentName, ex.PersistedRevision, ex.RuntimeRevision);
    throw;
}
catch (SchemaValidationException ex)
{
    foreach (var change in ex.Diff.FieldChanges)
    {
        log.LogError("{Field}: {Kind} ({Old} -> {New})", change.FieldName, change.Kind, change.OldType, change.NewType);
    }
    throw;
}
catch (SchemaMigrationException ex)
{
    log.LogError("Migration for '{Component}' failed on {Count} entities", ex.ComponentName, ex.FailedEntityCount);
    foreach (var failure in ex.Failures)
    {
        log.LogError("  ChunkId={ChunkId}: {Exception}", failure.ChunkId, failure.Exception);
    }
    throw; // old segments untouched — fix the migration function and restart
}
```

Duplicate keys surface at commit time, not registration:

```csharp
try
{
    using var tx = dbe.CreateQuickTransaction();
    tx.Spawn<PlayerArch>(PlayerArch.PlayerId.Set(existingId)); // PlayerId has [Index] (unique)
    tx.Commit();
}
catch (UniqueConstraintViolationException)
{
    // Not transient — same key will conflict again until the caller picks a different one.
}
```

## ⚠️ Guarantees & limits

- **Never partially applied.** A `SchemaValidationException` or `SchemaDowngradeException` blocks registration before any data is touched; a `SchemaMigrationException` leaves old segments fully intact — fix the cause and re-run, nothing is lost either way.
- **`IsTransient == false` on all four** — retrying without changing the struct, the migration function, the binary version, or the duplicate key never succeeds.
- **`SchemaValidationException.Diff`** is the full `SchemaDiff` (every `FieldChange`/`IndexChange`, with a `CompatibilityLevel` per change) — enough to log or render without re-deriving it.
- **`SchemaMigrationException.Failures`** caps detailed formatting in the exception message at 10 entries but the `IReadOnlyList<MigrationFailure>` itself carries every failure (`ChunkId`, hex dump of the old bytes, the thrown exception).
- **`SchemaDowngradeException` is unconditional** — there is no `SchemaValidationMode` that allows opening data written by a newer revision.
- **`UniqueConstraintViolationException` carries no typed context** (no key value, no index name) — catch it for control flow, log the surrounding operation for diagnostics.
- All four share the catch-by-type model used across the hierarchy — `catch (TyphonException)` still catches them, but there's no common intermediate type to catch only "schema/constraint" failures as a group; catch each concretely or use `TyphonException`.

## 🧪 Tests

- [MigrationFunctionTests](../../../test/Typhon.Engine.Tests/Data/Schema/MigrationFunctionTests.cs) — a throwing user migration function surfaces as `SchemaMigrationException` (per-entity `MigrationFailure`); a breaking change with no registered migration surfaces as `SchemaValidationException`.
- [SchemaValidationIntegrationTests](../../../test/Typhon.Engine.Tests/Data/Schema/SchemaValidationIntegrationTests.cs) — breaking field/index changes on reopen throw `SchemaValidationException` (`Diff`); a persisted revision newer than the runtime struct throws `SchemaDowngradeException`.

## 🔗 Related

- Related feature: [Schema Validation on Reopen](../Schema/schema-validation.md), [User-Defined Migration Functions](../Schema/migration-functions.md) — the mechanisms that throw `SchemaValidationException`/`SchemaMigrationException`
- Source: `src/Typhon.Engine/Errors/public/SchemaValidationException.cs`, `SchemaMigrationException.cs`, `SchemaDowngradeException.cs`, `UniqueConstraintViolationException.cs`
- Related catalog entries: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md)

<!-- Deep dive: claude/design/Errors/05-public-exception-catalog.md (Schema chain, Index chain tables) -->
<!-- Overview: claude/overview/10-errors.md §10.1, claude/overview/04-data.md §4.10 Schema Evolution -->
