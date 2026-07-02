# FieldId Stability & Rename Tracking
> Field identity survives reordering, insertion, removal, and renames — your indexes never silently point at the wrong field.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Schema](./README.md)

## 🎯 What it solves

Typhon identifies a component field internally by a small integer (`FieldId`), and secondary indexes are keyed on that integer, not on the field's name. If FieldIds were assigned purely by declaration order, inserting a field in the middle of a struct — or removing one — would silently shift every later field's id. An index built for `Mana` could end up reading `Shield` after a routine schema edit, with no error raised. FieldId stability removes this trap: a field's id, once assigned, never changes for the life of the database, regardless of how the struct is reshaped across versions.

## ⚙️ How it works (in brief)

The first time a component is created, FieldIds are assigned sequentially and persisted alongside each field's name. On every later reopen, Typhon matches runtime struct fields against the persisted set **by name**: a name match reuses the old id, a brand-new field gets the next free id above the current maximum, and a field no longer present is dropped (its id is never reused). Renames are detected via `PreviousName` rather than name matching. The struct's declaration order only matters for the very first creation — after that, reordering fields freely is safe.

## 💻 Usage

```csharp
// V1
[Component("Game.Player", 1)]
struct Player
{
    public int Health;     // FieldId 0
    public float Speed;    // FieldId 1
}

// V2 — Health renamed, a field inserted, declaration order changed
[Component("Game.Player", 1)]
struct Player
{
    [Field(PreviousName = "Health")]
    public int Hitpoints;  // matched by PreviousName -> keeps FieldId 0
    public int Shield;     // new field -> gets FieldId 2 (next free, not 1)
    public float Speed;    // matched by name -> keeps FieldId 1
}

dbe.RegisterComponentFromAccessor<Player>();
```

`PreviousName` is also available on `[Component(...)]` for renaming the component itself; it resolves the same way against the persisted component name.

| Mechanism | When to use it |
|-----------|-----------------|
| Plain field rename (no attribute) | Never — treated as remove-old + add-new, breaks the old index |
| `[Field(PreviousName = "Old")]` | Renaming a field while preserving its id, data, and index |
| `[Field(FieldId = N)]` | Explicit override (cross-assembly schemas, manual control) — wins over name matching |
| `[Component(PreviousName = "Old.Name")]` | Renaming a component while preserving all of its field ids |

## ⚠️ Guarantees & limits

- Adding, removing, or reordering fields never changes the FieldId of an unaffected field.
- A rename only preserves identity if `PreviousName` is declared; an undeclared rename is indistinguishable from drop+add and loses the old field's index/data linkage.
- `PreviousName` need only reference the immediately preceding name — once reopened with the new name, the persisted record updates and subsequent opens match on it directly. Multiple renames are PreviousName chains across versions, not a list of all historical names.
- Explicit `[Field(FieldId = N)]` always wins, but conflicts with an already-assigned id belonging to a different field fail loudly at registration rather than silently colliding.
- Two fields claiming the same `PreviousName` in one version is rejected at registration.
- FieldIds are never recycled — removed fields permanently retire their id. Accumulated additions over a component's lifetime are bounded by `short.MaxValue` (32,767); exceeding it fails registration with an explicit overflow error.
- Pre-existing databases need no migration: since original auto-assignment was deterministic, name-based matching reconstructs the same ids on first reopen under this scheme.

## 🧪 Tests

- [FieldIdResolverTests](../../../test/Typhon.Engine.Tests/Data/Schema/FieldIdResolverTests.cs) — pure resolver algorithm: name matching, `PreviousName` renames (incl. circular swaps), explicit `FieldId` conflicts, overflow, gap-skipping for new ids
- [FieldIdStabilityTests](../../../test/Typhon.Engine.Tests/Data/Schema/FieldIdStabilityTests.cs) — end-to-end reopen cycles proving ids/index survive add/remove/rename

## 🔗 Related

- Sibling: [Component & Field Schema Declaration](component-field-declaration.md) — declares the fields whose ids this feature keeps stable across reopens
- Sibling: [Schema Versioning & Migration](../Ecs/schema-versioning-migration.md) — the Ecs-side view of the same FieldId-based diff/migration flow

<!-- Deep dive: claude/design/Schema/01-field-id-stability.md -->
