---
uid: feature-schema-component-field-declaration
title: 'Component & Field Schema Declaration'
description: 'Declare a struct, decorate its fields, and Typhon turns it into a fully-indexed, persisted component type.'
---

# Component & Field Schema Declaration
> Declare a struct, decorate its fields, and Typhon turns it into a fully-indexed, persisted component type.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Schema](./README.md)

## 🎯 What it solves

Every piece of data Typhon stores — components, their fields, indexes, foreign keys — needs metadata describing its shape, identity, and constraints: a stable name and revision, field types and offsets, which fields are indexed and how, which storage discipline applies. Hand-maintaining that metadata separately from the struct definition invites drift between what the code declares and what the engine persists. Attribute-driven declaration keeps the single source of truth in the C# struct itself: the engine reflects it once, at registration time, into the metadata it needs for storage, indexing, and validation.

## ⚙️ How it works (in brief)

A component is a `struct` (blittable, fixed layout) decorated with `[Component(name, revision)]`. Each storable field is plain public field, optionally decorated with `[Field]` (explicit id/name/rename), `[Index]` (secondary B+Tree index), `[ForeignKey]` (referential link to another component), or `[SpatialIndex]` (R-Tree). At registration (`RegisterComponentFromAccessor<T>()` / `RegisterComponentByType()`), Typhon reflects the struct's fields, computes per-field storage offsets and sizes, and builds a `DBComponentDefinition` — the runtime schema object that drives storage layout, index creation, and (on reopen) validation against what's already persisted. Field identity is name-based, not declaration-order-based: an unannotated field gets an auto-assigned `FieldId` on first creation, then that id is matched back by name (or `PreviousName`) on every subsequent reopen, so reordering or inserting fields never reshuffles index associations.

## 💻 Usage

```csharp
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

[Component("Game.Guild", revision: 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct GuildComponent
{
    [Index(AllowMultiple = true)]
    public int Level;

    [Index]
    public int MemberCap;
}

[Component("Game.Player", revision: 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerComponent
{
    [Field(Name = "Health", PreviousName = "Hitpoints")]
    public float Health;

    [Index(AllowMultiple = true), ForeignKey(typeof(GuildComponent))]
    public long GuildId;

    [Index]
    public int AccountId;
}

// At startup, before any UnitOfWork is created:
dbe.RegisterComponentFromAccessor<GuildComponent>();
dbe.RegisterComponentFromAccessor<PlayerComponent>();

// Inspecting the reflected schema:
DBComponentDefinition def = dbe.DBD.GetComponent("Game.Player", revision: 1);
int healthOffset = def.FieldsByName["Health"].OffsetInComponentStorage;
```

| Attribute / Option | Default | Effect |
|---|---|---|
| `[Component(name, revision)]` | — (required) | Persistent component identity; `revision` is a manual version bump, independent of automatic field evolution |
| `ComponentAttribute.StorageMode` | `StorageMode.Versioned` | `Versioned` (full MVCC) / `SingleVersion` (in-place, tick-fence durable) / `Transient` (heap-only, no persistence) |
| `ComponentAttribute.DefaultDiscipline` | `DurabilityDiscipline.TickFence` | `SingleVersion`-only; `Commit` makes writes to this component commit-durable |
| `[Field(FieldId, Name, PreviousName)]` | name = C# member name, id = auto-assigned | Overrides field name/id; `PreviousName` preserves identity across a rename |
| `[Index(AllowMultiple)]` | `AllowMultiple = false` | Adds a secondary B+Tree index on the field; `AllowMultiple` allows non-unique values |
| `[ForeignKey(targetType)]` | — | Declares a referential link (field must be `long`); enables cascade-delete via `IndexAttribute.OnParentDelete` |
| `[SpatialIndex(margin, cellSize)]` | — | At most one per component; registers the field with the spatial R-Tree |
| `schemaValidation` (`RegisterComponentFromAccessor<T>`) | `SchemaValidationMode.Enforce` | Throws on breaking changes detected at reopen; `Skip` bypasses validation (unsafe) |

## ⚠️ Guarantees & limits

- `FieldId` is stable across reopens via name matching (or `PreviousName` for renames) — not C# declaration order — so secondary index identity survives field reordering or insertion.
- Field lookup by id is a dense-array O(1) dereference (`definition[fieldId]`), not a dictionary — chosen for hot-path performance over flexibility.
- Components must be blittable `unmanaged` structs; only public instance fields are reflected (static fields are ignored).
- At most one `[SpatialIndex]` field per component; unsupported on `StorageMode.Transient`.
- `[ForeignKey]` requires the field type to be `long`.
- Field names must be a single alphabetic word, UTF-8-encoded, ≤ 63 bytes.
- Registration is startup-only — `RegisterComponentFromAccessor<T>()` / `RegisterComponentByType()` must run before any `UnitOfWork` is created.
- This is the declaration layer only: schema validation on reopen, compatible evolution (add/remove fields), and migration functions for breaking changes are separate features (see Related).

## 🔗 Related

- Sibling: [FieldId Stability & Rename Tracking](fieldid-stability.md) — assigns and preserves the stable ids for the fields declared here
- Sibling: [Schema Validation (SchemaDiff)](schema-validation.md) — diffs this declared struct against the persisted layout on every reopen
- Source: `src/Typhon.Schema.Definition/Attributes.cs`, `src/Typhon.Engine/Schema/public/DBComponentDefinition.cs`, `src/Typhon.Engine/Schema/public/DatabaseDefinitions.cs`, `src/Typhon.Engine/Schema/public/DBObjectDefinition.cs`

<!-- Deep dive: claude/design/Schema/README.md (layered schema versioning architecture, decision log D1–D8) -->
<!-- Deep dive: claude/design/Schema/01-field-id-stability.md (FieldId resolution algorithm, `PreviousName`) -->
<!-- Overview: claude/overview/04-data.md §4.8 (Schema System), §4.1 (component registration flow) -->
