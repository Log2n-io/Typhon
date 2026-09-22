---
uid: feature-spatial-spatial-field-attribute-index
title: 'Field Attribute & Schema Integration'
description: 'Declare a component field as spatially indexed, validated against schema rules the moment the component is registered.'
---

# Field Attribute & Schema Integration
> Declare a component field as spatially indexed, validated against schema rules the moment the component is registered.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](../README.md)

## 🎯 What it solves

A spatial index needs to know, before a single entity exists, which field holds an entity's bounds, which shape
and precision that field uses, whether the archetype belongs in the static or the dynamic half of a cell, and
which category bits it answers to. Wiring this up imperatively — manual index construction,
manual field-offset bookkeeping, ad-hoc checks scattered through game code — is exactly the kind of boilerplate
Typhon's schema reflection already removes for secondary indexes and foreign keys. `[SpatialIndex]` extends that
same declarative path to spatial fields, and pushes every configuration mistake to startup instead of runtime.

## ⚙️ How it works (in brief)

Decorate one field of a supported geometry type (`AABB2F`/`AABB3F`/`BSphere2F`/`BSphere3F`, plus their `f64`
equivalents) with `[SpatialIndex(cellSize)]`, optionally setting `Mode` and `Category`. At component
registration the engine checks two things — that the field's type is a supported geometry type, and that the component is
not `StorageMode.Transient` — and any violation throws immediately, before the schema is built. On
success, the attribute's values flow into the component's `DBComponentDefinition.SpatialField` and into a
`SpatialIndexState` on the component table.

**That state is metadata, and registration allocates nothing.** A component carrying `[SpatialIndex]` reserves
no `StorageSegmentKind.Spatial` segment at all, nothing about the state is persisted, and the load path builds it
exactly as the create path does. Entities are indexed by the per-cell cluster broadphase, which the grid owns
rather than the component.

## 💻 Usage

```csharp
[Component("Game.Ship", revision: 1, StorageMode = StorageMode.SingleVersion)]
public struct ShipComponent
{
    [Field] public String64 Name;

    // cellSize is a constructor arg; Mode and Category are named properties. Only Mode and Category drive behaviour.
    [Field] [SpatialIndex(Mode = SpatialMode.Dynamic, Category = 1u << 2)]
    public AABB3F Bounds;
}

dbe.RegisterComponentFromAccessor<ShipComponent>();   // throws here on any violation, not later

// Inspecting the reflected metadata:
DBComponentDefinition def = dbe.DBD.GetComponent("Game.Ship", revision: 1);
DBComponentDefinition.Field spatial = def.SpatialField;
SpatialFieldType type = spatial.SpatialFieldType;     // AABB3F
```

| `[SpatialIndex]` arg | Default | Effect |
|---|---|---|
| `cellSize` (ctor) | `0` | Carried in schema metadata and sizes nothing. The cell size that matters is the engine-wide `SpatialGridConfig.CellSize`. |
| `Mode` | `SpatialMode.Dynamic` | Selects which half of every cell this archetype's clusters occupy. `Static` is skipped by the tick fence's bound-refresh, drift and repair passes; `Dynamic` is visited by all three. See [Static / Dynamic Separation](../spatial-rtree-index/spatial-rtree-static-dynamic.md). |
| `Category` | `uint.MaxValue` | Archetype-level bitmask consumed by the cluster broadphase to skip whole clusters |

## ⚠️ Guarantees & limits

- Exactly 8 supported field types: `AABB2F`/`AABB3F`/`BSphere2F`/`BSphere3F` and their `f64` (`...D`) equivalents
  — any other field type throws `InvalidOperationException` at registration.
- One `[SpatialIndex]` field per component is what the engine models: `DBComponentDefinition.SpatialField` is a single field reference. *A second one is **not** rejected — no validation checks for it, and the last field the reflection pass visits silently wins. Treat one-per-component as a contract you keep, not one the engine enforces.*
- Not supported on `StorageMode.Transient` — registering a `[SpatialIndex]` field on a Transient component throws
  `InvalidOperationException`.
- Validation runs once, at `RegisterComponentFromAccessor`/`RegisterComponentByType` time, before any
  `UnitOfWork` exists — by the time the schema is usable, the spatial configuration is already known-good.
- `BSphere*` fields are accepted directly — the engine converts to an enclosing AABB internally for indexing; the
  component's stored field stays a sphere, unchanged.
- The attribute only declares configuration; it maintains nothing — see the sub-feature below for *when* a write
  reaches the index, and [Per-Cell Cluster Index Internals](../spatial-rtree-index/README.md) for the structure
  a dense cell promotes to.

## 🧪 Tests

- [SpatialFieldTypeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialFieldTypeTests.cs) — `[SpatialIndex]` reflection (cellSize), `FieldType.FromType` mapping for all 8 supported types, `SpatialFieldInfo.ToVariant`/`IsSphere`
- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — schema validation at registration (`Schema_TransientWithSpatialIndex_Throws`, `Schema_ValidSpatialField_CreatesSpatialIndex`, `Schema_NoSpatialField_NullSpatialIndex`)
- [EntityIndexRetirementTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityIndexRetirementTests.cs) — `ASpatialComponentAllocatesNoSpatialSegment` counts segments by kind, so the no-segment guarantee above is checked rather than asserted in prose

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs), [src/Typhon.Engine/Spatial/public/SpatialFieldInfo.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialFieldInfo.cs), [src/Typhon.Schema.Definition/Attributes.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/Attributes.cs)
- Sub-features: [Storage-Mode Compatibility (SingleVersion / Versioned)](./spatial-storage-mode-compat.md)
- Sibling: [Per-Cell Cluster Index Internals](../spatial-rtree-index/README.md) — the structure that indexes entities carrying this attribute
- Overview: [Spatial Architecture Overview](../spatial-architecture-overview.md) — the one index, and the grid it stands on

<!-- Deep dive: claude/design/Spatial/SpatialIndex/05-ecs-integration.md (attribute API, schema registration flow) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/01-architecture.md (storage-mode compatibility table) -->
