# Schema Validation on Reopen
> Compares persisted schema against your current struct on every reopen and refuses to touch data it can't reconcile safely.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Schema](./README.md)

## 🎯 What it solves

A component's data on disk was written under whatever struct layout existed when it was last persisted. If you rename a field's C# type, narrow it, reorder fields, or change an array length and just reopen the database, the runtime struct and the on-disk bytes silently disagree — reads return garbage instead of failing. Schema Validation closes that gap: on every reopen it compares what's persisted against what your current struct declares, classifies every difference, and only lets registration proceed once every difference is provably safe or explicitly resolved.

## ⚙️ How it works (in brief)

Each registered component's field metadata (name, FieldId, type, offset, size) is persisted alongside the data. On reopen, `RegisterComponentFromAccessor<T>()` loads that metadata and diffs it against the runtime struct, producing a `SchemaDiff` with one `FieldChange` per difference. Each change is classified into a `CompatibilityLevel` — `Identical`, `InformationOnly` (renames), `Compatible` (add/remove/reorder a field), `CompatibleWidening` (lossless type promotion, e.g. `int`→`long`), or `Breaking` (anything that could lose or misinterpret data). The diff's overall level is the worst level among its changes. `Compatible`/`CompatibleWidening` changes are auto-migrated transparently (see Schema Evolution). A `Breaking` level with no migration function registered throws `SchemaValidationException` before any data is touched — registration fails, the database stays closed for that component. Separately, if the persisted revision is *newer* than the runtime struct's revision, registration throws `SchemaDowngradeException` — running an older binary against newer data is never allowed, validation or not.

## 💻 Usage

```csharp
// Struct evolved from V1 (int Score) to V2 (string Score) between releases — incompatible.
[Component("Game.Player", 2)]
struct Player
{
    public String64 Score;
    public int Level;
}

try
{
    dbe.RegisterComponentFromAccessor<Player>();
}
catch (SchemaValidationException ex)
{
    // ex.Diff carries the full breakdown for diagnostics/logging
    foreach (var change in ex.Diff.FieldChanges)
    {
        log.LogError("{Field}: {Kind} ({Old} -> {New})", change.FieldName, change.Kind, change.OldType, change.NewType);
    }
    throw;
}
```

To force registration through a breaking change anyway (e.g. a throwaway dev database you're willing to risk):

```csharp
dbe.RegisterComponentFromAccessor<Player>(schemaValidation: SchemaValidationMode.Skip);
```

| Option | Default | Effect |
|---|---|---|
| `schemaValidation: SchemaValidationMode.Enforce` | Default | Throw `SchemaValidationException` on any unresolved breaking change |
| `schemaValidation: SchemaValidationMode.Skip` | — | Bypass the breaking-change throw; data is read/written under the runtime layout regardless of mismatch — **unsafe** |

## ⚠️ Guarantees & limits

- Validation runs before any user transaction touches the database — a failing component blocks startup, it never lets the application proceed with a half-trusted schema.
- Identical schemas hit a fast path: no diff overhead beyond the metadata comparison itself.
- Classification is conservative by construction: only changes proven lossless (the widening table) are ever auto-resolved; everything else is `Breaking` until you supply a migration function or pass `Skip`.
- A persisted revision newer than the runtime struct always throws `SchemaDowngradeException`, independent of `SchemaValidationMode` — there is no way to force-open data written by a newer binary.
- `SchemaValidationMode.Skip` does not fix anything — it only suppresses the throw. Field offsets/types are still whatever the runtime struct says; reading old bytes through a changed layout can produce wrong values. Use it only when you accept that risk (e.g. disposable dev/test databases).
- Validation is per-component: one component failing does not by itself evaluate the others, since registration calls are made one at a time by application startup code.

## 🧪 Tests

- [SchemaValidatorTests](../../../test/Typhon.Engine.Tests/Data/Schema/SchemaValidatorTests.cs) — pure diff/classification algorithm: identical/added/removed/widened/narrowed/breaking, all valid and invalid widening pairs, rename-as-informational
- [SchemaValidationIntegrationTests](../../../test/Typhon.Engine.Tests/Data/Schema/SchemaValidationIntegrationTests.cs) — reopen cycles: identical no-op, widening allowed, breaking throws `SchemaValidationException`, `SchemaValidationMode.Skip` bypass, `SchemaDowngradeException`

## 🔗 Related

- Related feature: [Compatible Schema Evolution](./compatible-evolution/README.md) (auto-migration of `Compatible`/`CompatibleWidening` changes)
- Related feature: [User-Defined Migration Functions](./migration-functions.md) (user-supplied conversion for `Breaking` changes)

<!-- Overview: claude/overview/04-data.md §4.10 Schema Evolution — Schema Validation (CompatibilityLevel table, diff mechanics) -->
<!-- Design: claude/design/Schema/02-schema-validation.md (full SchemaDiff design, change taxonomy, error format) -->
