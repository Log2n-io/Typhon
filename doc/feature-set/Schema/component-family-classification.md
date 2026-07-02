# Component Family Classification
> Groups components into semantic families for stable, readable Workbench visualizations.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Schema](./README.md)

## 🎯 What it solves

The Workbench Data Flow Timeline and Access Matrix show every component a system touches, but a flat list of dozens of component names doesn't read as a picture — there's no visual grouping to tell "movement data" apart from "combat data" apart from "network sync data" at a glance. Component Family Classification assigns each component to one of a small set of well-known families so the Workbench can group and order rows consistently, without requiring the schema author to maintain a separate taxonomy file.

## ⚙️ How it works (in brief)

Each component resolves to a family name through two steps, in order: an explicit `[ComponentFamily("Name")]` attribute on the struct wins if present; otherwise a server-side heuristic substring-matches the component's name against fixed token lists (e.g. `Position`/`Velocity`/`Transform` → `Spatial`, `Health`/`Damage`/`Shield` → `Combat`). Unmatched names fall back to `Misc`. The heuristic is the safety net for components you didn't explicitly tag, and the only path available when classifying components from a recorded trace (no live `Type` to read attributes from). The Workbench renders families in a fixed canonical order so the layout is stable across sessions, independent of declaration order or alphabetical sort.

## 💻 Usage

```csharp
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

// Explicit — wins over the name heuristic regardless of the component's name.
[Component("Game.Squad", revision: 1)]
[ComponentFamily("AI")]
[StructLayout(LayoutKind.Sequential)]
public struct SquadCommandComponent
{
    public int TargetEntityId;
}

// No attribute — classified by the name heuristic from "Position" -> Spatial.
[Component("Game.Position", revision: 1)]
[StructLayout(LayoutKind.Sequential)]
public struct PositionComponent
{
    public float X;
    public float Y;
    public float Z;
}
```

There is no API call to make as the application developer — registering the component (`RegisterComponentFromAccessor<T>()`) is enough. Classification happens server-side when a Workbench session attaches.

| Family | Heuristic tokens (substring match) |
|---|---|
| Spatial | Position, Velocity, Bounds, Rotation, Transform, Scale, Pose |
| Combat | Health, Damage, Armor, Shield, Hp, Hit |
| AI | Behaviour, Behavior, Target, Pathfind, NavMesh, Decision |
| Inventory | Inventory, Equipment, Equipped, Ammo, Item |
| Rendering | Sprite, Animation, Mesh, Material, Tint, Render |
| Networking | Network, Replication, Sync, Snapshot |
| Input | Input, Command, Action |
| Misc | (default fallback — no match) |

## ⚠️ Guarantees & limits

- `[ComponentFamily]` always overrides the heuristic, regardless of how the component is named.
- The heuristic always returns a family — there's no "unclassified" state; unmatched names land in `Misc`.
- Row order in the Workbench is fixed: Spatial → Combat → AI → Inventory → Rendering → Networking → Input → Misc, identical across sessions.
- Token matching is ordinal substring matching against fixed, non-configurable lists — there is no way to add custom families or tokens without an attribute on the component itself.
- Trace (recorded) sessions can only use the name heuristic — `[ComponentFamily]` requires reflecting the live CLR `Type`, which a trace replay doesn't have.
- This is a presentation/grouping aid only — family has no effect on storage, indexing, validation, or schema evolution.
- This is a Workbench-only concern.

## 🧪 Tests

- [ComponentFamilyResolverTests](../../../test/Typhon.Engine.Tests/Data/Schema/ComponentFamilyResolverTests.cs) — attribute-wins-over-heuristic, per-token heuristic classification (`TestCase` per family), `Misc` fallback, canonical family order

## 🔗 Related

- Sibling: [Workbench Per-Session Schema Loading & ALC Reload](workbench-schema-loading.md) — classification runs server-side each time a Workbench session attaches or reloads
- Source: `src/Typhon.Engine/Schema/internals/ComponentFamilyResolver.cs`, `src/Typhon.Schema.Definition/Attributes.cs`

<!-- Deep dive: claude/design/Schema/06-workbench-schema-loading.md §5 (resolution order, canonical family order, visibility note) -->
