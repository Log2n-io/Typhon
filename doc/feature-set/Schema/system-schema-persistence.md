# System Schema Persistence
> A Typhon database file describes its own component/field metadata — no external schema file needed.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Schema](./README.md)

## 🎯 What it solves

Interpreting a database file requires knowing every component's name, fields, types, offsets, and indexes. Keeping that metadata in a separate sidecar file invites drift — the file gets lost, copied out of sync, or simply doesn't travel with the `.bin` when it's moved. Typhon avoids the problem by storing its own schema *inside* the database: component and field definitions are persisted as ordinary entities, in the same storage engine that holds user data. Any process that opens the file — the real application, or a throwaway inspector — recovers the full schema from the file alone.

## ⚙️ How it works (in brief)

Two system component types, `ComponentR1` and `FieldR1`, carry the metadata: one `ComponentR1` row per registered component (name, revision, storage mode, segment SPIs), with its fields nested as a `FieldR1` collection (name, FieldId, type, offset, size, index flags). On first database creation, `ComponentR1` itself is registered as a component and a `ComponentR1` row is written describing `ComponentR1` — the system table is self-referential. Segment SPIs for the system tables are stored in the root file header's bootstrap dictionary. On reopen, the engine reconnects to those segments from the bootstrap SPIs, then scans the `ComponentR1` table's allocated chunks to rebuild the in-memory map every subsequent `RegisterComponentFromAccessor<T>()` call consults to reattach to existing storage instead of allocating fresh segments. Reads and writes of these system rows go through a dedicated minimal CRUD helper — single-threaded, no MVCC revision chain, no WAL record, no conflict detection — appropriate because schema mutation only ever happens at startup, before any transaction exists.

## 💻 Usage

```csharp
using Typhon.Engine;

// First run — creates the database; registering GuildComponent persists its
// ComponentR1 + FieldR1 rows inside the file as a side effect.
var engine = services.GetRequiredService<DatabaseEngine>();
engine.RegisterComponentFromAccessor<GuildComponent>();
// ... application runs ...
engine.Dispose();

// Later run, same path, no schema file shipped alongside the data file —
// the same registration call now reattaches to the persisted segments.
var engine2 = services2.GetRequiredService<DatabaseEngine>();
engine2.RegisterComponentFromAccessor<GuildComponent>();   // reconnect, no reallocation, no data loss
```

There is no direct API for this layer — it activates automatically on `RegisterComponentFromAccessor<T>()` / `RegisterComponentByType()`, for every component, on every create or reopen.

## ⚠️ Guarantees & limits

- A `.bin` file is self-describing: full component/field/index metadata travels with the data, recoverable without application code (this is what `DatabaseSchema.Inspect` reads — see Related).
- System schema rows have no revision history and are not part of WAL crash recovery — they're written via direct page writes inside an explicit `ChangeSet`, flushed to disk synchronously at the point of registration, independent of the WAL pipeline used for user transactions.
- Reopen cost scales with the number of distinct registered component types (one chunk scanned per component), not with entity count — cheap even on large databases.
- This is internal infrastructure, not a user-facing API: there's no supported way to read or write `ComponentR1`/`FieldR1` rows directly; the only entry points are component registration (write path) and `DatabaseSchema.Inspect` (read path).
- The system schema's own layout is not subject to the user-facing schema evolution machinery (validation, compatible evolution, migrations) — it is fixed by the engine version, not by application code.

## 🧪 Tests

- [FieldIdStabilityTests](../../../test/Typhon.Engine.Tests/Data/Schema/FieldIdStabilityTests.cs) — `FieldR1_RoundTrip_SameSession` walks `ComponentR1` chunks and reads nested `FieldR1` entries directly via `SystemCrud.Read`
- [SchemaManifestTests](../../../test/Typhon.Engine.Tests/Data/Schema/SchemaManifestTests.cs) — persisted assembly manifest (`AssemblyR1`) dedup and `ComponentR1`/`ArchetypeR1.AssemblyId` linkage, core-vs-user-assembly exclusion

## 🔗 Related

- Related feature: [Component & Field Declaration](./component-field-declaration.md) (the attribute-driven layer that produces what gets persisted here)
- Related feature: [Offline Schema Inspection & Dry-Run Validation](./schema-inspection-dryrun.md) (`DatabaseSchema.Inspect` reads the data this feature persists)
- Source: `src/Typhon.Engine/Schema/internals/SystemCrud.cs`, `src/Typhon.Engine/Ecs/public/DatabaseEngine.cs` (`CreateSystemSchemaR1`, `LoadSystemSchemaR1`, `SaveInSystemSchema`)

<!-- Deep dive: claude/design/Schema/README.md — Current State -->
<!-- Overview: claude/overview/04-data.md §4.1 DatabaseEngine (schema persistence flow), §4.8 Schema System -->
