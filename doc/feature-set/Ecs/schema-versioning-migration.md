# Schema Versioning & Migration
> Detects struct/archetype layout drift at database open and migrates data automatically or via your own functions.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](./README.md)

## 🎯 What it solves

Component structs and archetypes evolve over an application's lifetime — fields get added, removed, widened, or
reordered, and the set of components on an archetype changes. Without versioning, a stale on-disk layout silently
misaligns with the runtime struct, corrupting data or crashing. Typhon persists both component and archetype
schema metadata and validates it on every open, so layout drift is caught loudly instead of silently — then
migrates the common cases automatically and gives you a typed escape hatch for the rest.

## ⚙️ How it works (in brief)

Every `[Component]` and `[Archetype]` declares a `Revision`; persisted field metadata (name, type, offset, stable
`FieldId`) is compared against the runtime struct at `RegisterComponentFromAccessor<T>()`. Field add/remove/reorder
and safe type widenings (`int`→`long`, `Float`→`Double`, …) are detected as **compatible** and migrated
automatically — segments are reallocated to the new stride and every entity is copied field-by-field, eagerly, at
open. Lossy or semantic changes (narrowing, signed↔unsigned, cross-type, field split/merge) are **breaking**: the
engine refuses to open until you register a `MigrationFunc<TOld, TNew>` (or a byte-level fallback) that transforms
old bytes to new; chains of single-step migrations are resolved automatically via BFS. Archetype-level changes
(component count or archetype `Revision` mismatch) are validated the same way but only as a hard fail — there is
no automatic add/remove-component-from-archetype migration yet.

## 💻 Usage

```csharp
[Component("Game.Player", revision: 2)]
public struct PlayerV2
{
    public int Health;
    [Field(PreviousName = "Mana")]
    public long Energy;          // widened + renamed — auto-migrated, no function needed
    public float HealthRatio;    // new field — zero-initialized
}

// Breaking change: needs an explicit transform (must run before RegisterComponentFromAccessor)
dbe.RegisterMigration<PlayerV1, PlayerV2>((ref PlayerV1 old, out PlayerV2 cur) =>
{
    cur = new PlayerV2
    {
        Health = old.Health,
        Energy = old.Mana,
        HealthRatio = old.HealthPercent / 100f,   // semantic conversion — why a function is required
    };
});

dbe.RegisterComponentFromAccessor<PlayerV2>();   // validates, auto-migrates compatible fields, runs registered functions
```

```
tsh> schema-validate
  [green]OK[/] Game.Inventory — identical
  [red]FAIL[/] Game.Equipment — 1 breaking field change (NO migration path!)
```

| Option | Default | Effect |
|--------|---------|--------|
| `schemaValidation: SchemaValidationMode` on `RegisterComponentFromAccessor<T>` | `Enforce` | `Skip` bypasses validation entirely — unsafe, may corrupt data if the layout actually changed |

## ⚠️ Guarantees & limits

- Persisted vs. runtime field layout (name, type, offset, stable `FieldId`) is diffed on every open; a breaking
  diff with no migration path throws `SchemaValidationException` before any user transaction runs.
- Opening a database written by a newer binary (persisted revision > runtime revision) throws
  `SchemaDowngradeException` — downgrades are never attempted.
- Compatible changes (add/remove/reorder field, safe widening) migrate automatically, eagerly, at open — no
  read-path branching afterward. MVCC revision chains are collapsed to HEAD only during migration (history is not
  preserved across a stride change).
- Breaking changes require a registered `MigrationFunc<TOld, TNew>` or byte-level function; multi-step chains
  resolve via BFS over registered (from, to) revision pairs. A migration failure on any entity aborts before the
  old segment is touched — the database is left exactly as it was.
- Archetype-level schema (component count, archetype `Revision`) is validated the same way but **only as a hard
  error** ("Run `tsh migrate`") — there is no automatic migration for adding/removing a component on an archetype
  or for changing archetype inheritance; the database must be recreated for those cases today.
- `tsh migrate` opens the database through the normal engine path (not raw/exclusive file access) and reports
  per-component OK/MIGRATED/FAIL; `schema-diff`, `schema-validate`, `schema-fields`, `schema-history`, and
  `schema-export` give pre-flight and audit visibility without mutating anything.

## 🧪 Tests

- [SchemaEvolutionTests](../../../test/Typhon.Engine.Tests/Data/Schema/SchemaEvolutionTests.cs) — compatible auto-migration: add/remove/reorder fields, safe int→long/float→double widening, surviving indexes
- [MigrationFunctionTests](../../../test/Typhon.Engine.Tests/Data/Schema/MigrationFunctionTests.cs) — breaking-change `MigrationFunc`/byte-level migration, chained multi-step BFS resolution, missing-migration and duplicate-registration failures
- [SchemaValidationIntegrationTests](../../../test/Typhon.Engine.Tests/Data/Schema/SchemaValidationIntegrationTests.cs) — `SchemaValidationException` on an unmigrated breaking change, `SchemaDowngradeException`, `Skip` validation mode
- [SchemaVersioningTests](../../../test/Typhon.Engine.Tests/Data/ECS/SchemaVersioningTests.cs) — archetype-level schema persistence and tamper detection (component-count/revision mismatch hard-fails)

## 🔗 Related

- Source: `src/Typhon.Engine/Ecs/public/DatabaseEngine.cs` (`RegisterComponentFromAccessor`, `RegisterMigration`,
  `ValidateArchetypeSchema`), `src/Typhon.Engine/Schema/internals/SchemaEvolutionEngine.cs`,
  `src/Typhon.Engine/Schema/internals/MigrationRegistry.cs`, `src/Typhon.Engine/Schema/public/SchemaDiff.cs`,
  `src/Typhon.Shell/Commands/CommandExecutor.cs` (`migrate`, `schema-*` commands)
- Related features: [Entity & Archetype Model](./entity-archetype-model.md)
- Sibling: [User-Defined Migration Functions](../Schema/migration-functions.md) — the engine-side `MigrationFunc<TOld, TNew>` mechanism breaking changes register here

<!-- Deep dive: claude/design/Schema/README.md (component-level layers — Implemented), claude/design/Schema/03-compatible-evolution.md, claude/design/Schema/04-migration-functions.md, claude/design/Schema/05-operational-tooling.md, claude/design/Ecs/03-entity-model.md §Schema Versioning & Migration (archetype-level — partial) -->
