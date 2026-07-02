# Offline Schema Inspection & Dry-Run Validation
> Read a database's persisted schema, or simulate a code upgrade against it, without opening it for real.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Schema](./README.md)

## 🎯 What it solves

Two questions come up before you ever touch a production database file: "what schema does this database actually have?" and "will my new build's component structs and migrations succeed against it, or fail?". Answering either by opening the database with your application normally means accepting whatever side effects registration and auto-migration trigger — including a hard failure that leaves you debugging a broken deployment. `DatabaseSchema.Inspect` and `DatabaseSchema.ValidateEvolution` answer both questions from outside the application, without registering any components for real and without writing a single byte to the file.

## ⚙️ How it works (in brief)

Both calls spin up a throwaway, isolated engine instance against the target file path, read what they need from the persisted system tables, and dispose it — no shared state with any live `DatabaseEngine` your app may have open elsewhere. `Inspect` reads persisted component/field/index metadata and per-component entity counts directly, with no component registration at all. `ValidateEvolution` takes a configuration callback (the same shape as your real startup registration code) but captures the registrations into a recorder instead of applying them: it computes a `SchemaDiff` per component against the persisted layout, checks whether breaking changes have a registered migration chain, and reports the outcome — all without allocating storage or migrating any entity.

## 💻 Usage

```csharp
// What's actually in this file?
var report = DatabaseSchema.Inspect("prod/game.typhon");

Console.WriteLine($"{report.DatabaseName} (format R{report.DatabaseFormatRevision})");
foreach (var component in report.Components)
{
    Console.WriteLine($"  {component.Name} R{component.Revision} — {component.EntityCount} entities");
    foreach (var field in component.Fields)
        Console.WriteLine($"    {field.Name} ({field.Type}) FieldId={field.FieldId} Offset={field.Offset}");
}

// Will my next release's schema open cleanly against it?
var result = DatabaseSchema.ValidateEvolution("prod/game.typhon", registrar =>
{
    registrar.RegisterComponent<PlayerV2>();
    registrar.RegisterMigration<PlayerV1, PlayerV2>(MigratePlayer);
});

if (!result.IsValid)
{
    foreach (var error in result.Errors)
        Console.WriteLine($"BLOCKED: {error}");
    // fail the deploy / CI step here
}
```

## ⚠️ Guarantees & limits

- Neither call mutates the database file — `Inspect` never registers a component, `ValidateEvolution` never allocates segments, migrates entities, or writes a schema history entry.
- Safe to run against a database another process has open; each call uses its own short-lived engine and service provider, fully disposed before returning.
- `ValidateEvolution` only evaluates the components you register in the callback — it does not flag persisted components your configuration omits.
- `EvolutionValidationResult.IsValid` is `false` only when a component has breaking changes with no registered migration path; non-breaking (`Compatible`/`CompatibleWidening`) changes are reported as `NeedsMigration` but don't fail validation, matching what auto-migration would actually do on real registration.
- A dry run is a prediction, not a guarantee — it doesn't account for runtime conditions at real-registration time (disk space exhaustion mid-migration, a concurrently-open writer, etc.).

## 🧪 Tests

- [OperationalToolingTests](../../../test/Typhon.Engine.Tests/Data/Schema/OperationalToolingTests.cs) — `Inspect_ReturnsComponentsAndFields` (offline read of persisted components/fields), `ValidateEvolution_CompatibleChange_IsValid` (dry-run reports `NeedsMigration`/`HasMigrationPath` without mutating)

## 🔗 Related

- Related feature: [Schema Validation on Reopen](./schema-validation.md) (the live-path equivalent these calls simulate)

<!-- Design: claude/design/Schema/05-operational-tooling.md §2 Schema Inspection API -->
<!-- Design: claude/design/Schema/05-operational-tooling.md §3 Dry-Run Validation -->
<!-- Overview: claude/overview/04-data.md — DatabaseSchema.Inspect / ValidateEvolution API summary -->
