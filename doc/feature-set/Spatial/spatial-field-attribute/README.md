# Field Attribute & Schema Integration
> Declare a component field as spatially indexed, validated against schema rules the moment the component is registered.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](../README.md)

## 🎯 What it solves

A spatial index needs to know, before a single entity exists, which field holds an entity's bounds, how much
slack to give that field before a tree mutation is needed, whether it belongs in the static or dynamic tree, and
whether a coarse occupancy filter is worth maintaining. Wiring this up imperatively — manual tree construction,
manual field-offset bookkeeping, ad-hoc checks scattered through game code — is exactly the kind of boilerplate
Typhon's schema reflection already removes for secondary indexes and foreign keys. `[SpatialIndex]` extends that
same declarative path to spatial fields, and pushes every configuration mistake to startup instead of runtime.

## ⚙️ How it works (in brief)

Decorate one field of a supported geometry type (`AABB2F`/`AABB3F`/`BSphere2F`/`BSphere3F`, plus their `f64`
equivalents) with `[SpatialIndex(margin, cellSize)]`, optionally setting `Mode` and `Category`. At component
registration the engine checks the field's type, the component's storage mode, and that no second
`[SpatialIndex]` field exists on the struct — any violation throws immediately, before the schema is built. On
success, the attribute's values flow into the component's `DBComponentDefinition.SpatialField` and into the
runtime state that backs the R-Tree, the back-pointer segment, and — when `cellSize > 0` — the Layer-1
occupancy hashmap.

## 💻 Usage

```csharp
[Component("Game.Ship", revision: 1, StorageMode = StorageMode.SingleVersion)]
public struct ShipComponent
{
    [Field] public String64 Name;

    // margin/cellSize are constructor args; Mode/Category are named properties.
    [Field] [SpatialIndex(margin: 5.0f, Mode = SpatialMode.Dynamic, Category = 1u << 2)]
    public AABB3F Bounds;
}

dbe.RegisterComponentFromAccessor<ShipComponent>();   // throws here on any violation, not later

// Inspecting the reflected metadata:
DBComponentDefinition def = dbe.DBD.GetComponent("Game.Ship", revision: 1);
DBComponentDefinition.Field spatial = def.SpatialField;
float margin = spatial.SpatialMargin;
SpatialFieldType type = spatial.SpatialFieldType;     // AABB3F
```

| `[SpatialIndex]` arg | Default | Effect |
|---|---|---|
| `margin` (ctor) | required | Fat-AABB enlargement in world units — see [Spatial R-Tree Index](../spatial-rtree-index/README.md) |
| `cellSize` (ctor) | `0` | `0` = no Layer-1 hashmap; `>0` enables a coarse occupancy filter at that cell size |
| `Mode` | `SpatialMode.Dynamic` | `Static` bulk-loads once and is skipped by per-tick/commit maintenance; `Dynamic` gets fat-AABB updates |
| `Category` | `uint.MaxValue` | Archetype-level bitmask consumed by the cluster broadphase to skip whole clusters |

## ⚠️ Guarantees & limits

- Exactly 8 supported field types: `AABB2F`/`AABB3F`/`BSphere2F`/`BSphere3F` and their `f64` (`...D`) equivalents
  — any other field type throws `InvalidOperationException` at registration.
- At most one `[SpatialIndex]` field per component — a second one throws at registration, not at first use.
- `margin` must be ≥ 0 — a negative value throws at registration.
- Not supported on `StorageMode.Transient` — registering a `[SpatialIndex]` field on a Transient component throws
  `InvalidOperationException`.
- Validation runs once, at `RegisterComponentFromAccessor`/`RegisterComponentByType` time, before any
  `UnitOfWork` exists — by the time the schema is usable, the spatial configuration is already known-good.
- `BSphere*` fields are accepted directly — the engine converts to an enclosing AABB internally for indexing; the
  component's stored field stays a sphere, unchanged.
- The attribute only declares configuration; it does not itself maintain the tree — see the sub-feature below
  for *when* the tree picks up a write, and [Spatial R-Tree Index](../spatial-rtree-index/README.md) for
  the index structure it configures.

## 🧪 Tests

- [SpatialFieldTypeTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialFieldTypeTests.cs) — `[SpatialIndex]` reflection (margin/cellSize), `FieldType.FromType` mapping for all 8 supported types, `SpatialFieldInfo.ToVariant`/`IsSphere`
- [SpatialEcsIntegrationTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — schema validation at registration (`Schema_TransientWithSpatialIndex_Throws`, `Schema_ValidSpatialField_CreatesSpatialIndex`, `Schema_NoSpatialField_NullSpatialIndex`, `Schema_CellSizeZero_NoHashmap`)

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs](../../../../src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs), [src/Typhon.Engine/Spatial/public/SpatialFieldInfo.cs](../../../../src/Typhon.Engine/Spatial/public/SpatialFieldInfo.cs), [src/Typhon.Schema.Definition/Attributes.cs](../../../../src/Typhon.Schema.Definition/Attributes.cs)
- Sub-features: [Storage-Mode Compatibility (SingleVersion / Versioned)](./spatial-storage-mode-compat.md)
- Sibling: [Spatial R-Tree Index](../spatial-rtree-index/README.md) — the index structure this attribute configures
- Overview: [Spatial Architecture Overview](../spatial-architecture-overview.md) — how this fits with the separate spatial grid

<!-- Deep dive: claude/design/Spatial/SpatialIndex/05-ecs-integration.md (attribute API, schema registration flow) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/01-architecture.md (storage-mode compatibility table) -->
