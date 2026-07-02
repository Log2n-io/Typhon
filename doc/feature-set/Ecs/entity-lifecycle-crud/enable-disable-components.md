# Enable/Disable Components
> O(1) bit-flip to toggle a component's participation without freeing its data, copying it, or migrating the entity.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Some component data needs to temporarily "not count" — a stunned unit's AI shouldn't tick, a downed unit's health
regen shouldn't apply — without permanently removing the component. Removing and re-adding a component isn't even
possible post-spawn (an archetype's component set is fixed for the entity's life), and reallocating storage just to
toggle participation would be wasteful when the data needs to come right back. Enable/Disable gives every component
slot a cheap two-state toggle that `Query`, iteration, and optional reads all respect, with the underlying storage
untouched.

## ⚙️ How it works (in brief)

Each entity carries one `EnabledBits` bitmask on its `EntityRecord` — one bit per archetype component slot.
`Enable<T>(Comp<T>)`/`Disable<T>(Comp<T>)` on a writable `EntityRef` flip that bit locally and stage the change via
`StageEnableDisable` for commit; a read-only `EntityAccessor`/`PointInTimeAccessor` worker throws, since only a full
`Transaction` supports staging structural changes. If the entity lives in cluster (batched SoA) storage, the
cluster's own enabled-bit vector is updated immediately too, so bulk cluster iteration sees the change without
waiting for commit. Because `EnabledBits` is entity-level metadata independent of each component's own
`StorageMode`, it carries its own MVCC snapshot isolation through an engine-wide exception dictionary
(`EnabledBitsOverrides`): a fast path (`_overrideCount == 0`, a single volatile-int read) skips it entirely when no
concurrent transaction is mid-toggle; when one is, older transactions still resolve the pre-change bits via a
per-entity `EnabledBitsHistory`. The query engine's `.Enabled<T>()`/`.Disabled<T>()` constraints and reactive views
integrate for free — a per-component `hasEnabledAwareViews` flag keeps view notification at zero cost unless a view
actually filters on enabled state.

## 💻 Usage

```csharp
using var tx = dbe.CreateQuickTransaction();
var id = tx.Spawn<Unit>(Unit.Pos.Set(new Position { X = 0, Y = 0, Z = 0 }));
// Velocity omitted at Spawn — starts disabled, its chunk is zero-initialized
tx.Commit();

using var wtx = dbe.CreateQuickTransaction();
EntityRef e = wtx.OpenMut(id);
bool moving = e.IsEnabled(Unit.Vel);   // false — never set at Spawn
e.Enable(Unit.Vel);                     // O(1) bit flip — data zero-initialized, now live
e.Disable(Unit.Pos);                    // O(1) bit flip — data preserved, not freed
wtx.Commit();

// Safe optional read — false if disabled or absent, no exception
using var rtx = dbe.CreateQuickTransaction();
if (rtx.Open(id).TryRead<Velocity>(out var vel)) { /* vel is a copy, not a ref */ }

// Query-side: only entities with Velocity currently enabled
HashSet<EntityId> moving2 = rtx.Query<Unit>().Enabled<Velocity>().Execute();
```

## ⚠️ Guarantees & limits

- Always O(1) — a single bit flip on the `EntityRef`'s cached bits; no chunk allocation, no data copy.
- Disabling never frees or reallocates a component's chunk — data is preserved and immediately available on
  re-enable; chunks are freed only when the entity itself is destroyed.
- Two-state only (enabled/disabled) — no partial or graded state.
- `Enable`/`Disable` require the `Comp<T>` handle overload — there is no bare-type-parameter form.
- Requires a full `Transaction` — a read-only `EntityAccessor`/`PointInTimeAccessor` worker accessor throws
  (`StageEnableDisable` is Transaction-only).
- Carries its own MVCC snapshot isolation independent of the component's `StorageMode`: even a
  `SingleVersion`/`Transient` component's enabled bit stays snapshot-consistent for concurrent readers via
  `EnabledBitsOverrides` — zero overhead (one volatile-int check) when no transaction is mid-toggle.
- Visible within the same transaction immediately (read-your-own-writes) — `.Enabled<T>()`/`.Disabled<T>()` query
  filters see a pending, uncommitted toggle before that transaction commits.
- Cluster-stored (batched SoA) entities update the cluster's own enabled-bit vector immediately on toggle, not just
  at commit, so bulk cluster iteration reflects it right away.
- `TryRead<T>` returns a copy, not a ref (an `out` parameter can't be `ref readonly`) — for zero-copy access, check
  `IsEnabled` first, then call `Read` directly.

## 🧪 Tests

- [EnableDisableTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EnableDisableTests.cs) — enabled-bits-after-spawn (full/partial), Enable/Disable bit-flip and data preservation across re-enable, commit persistence, MVCC (older transaction still sees pre-disable bits), multiple toggles in one transaction (last state wins), disable-then-destroy, all-disabled-but-entity-still-alive, same-transaction query visibility of a pending toggle

## 🔗 Related

- Sibling: [Query System (EcsQuery)](../query-system.md) — `.Enabled<T>()`/`.Disabled<T>()` query constraints
- Sibling: [Persistent Views](../../Querying/persistent-views.md) — enable/disable toggles drive view boundary-crossing notifications
- Parent feature: [Entity Lifecycle & CRUD API](./README.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md §Disable / Enable, §MVCC Isolation, §View Notification -->
