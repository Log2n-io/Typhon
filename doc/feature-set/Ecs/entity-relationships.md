---
uid: feature-ecs-entity-relationships
title: 'Entity Relationships'
description: 'Typed EntityLink references plus declarative cascade delete and reactive FK joins.'
---

# Entity Relationships
> Typed `EntityLink<T>` references plus declarative cascade delete and reactive FK joins.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](./README.md)

## 🎯 What it solves

Entities rarely stand alone — bags own items, characters own equipment, guilds own members. An ECS needs a
relationship model with the same guarantees as the rest of the database: type-checked endpoints (not raw
integer IDs), indexed reverse lookups, predictable cleanup when a parent goes away, and reactive joins for views
that must stay live as relationships change. `EntityLink<T>` plus the indexing/cascade/navigation machinery
built on top of it covers 1:1, 1:N, and N:M relationships, and hierarchical trees, without ad-hoc bookkeeping.

## ⚙️ How it works (in brief)

`EntityLink<TArchetype>` is an 8-byte, archetype-checked wrapper around `EntityId` — `EntityLink<Building>`
accepts `Building` or any descendant archetype, `EntityLink.Null` means "no reference." Marking the field
`[Index]` gives an indexed FK: a child stores a link to its parent (1:N, "FK on child"), or a parent stores a
`ComponentCollection<EntityLink<T>>` of children (1:N, "collection on parent"); the two compose for bidirectional
access. `[Index(OnParentDelete = CascadeAction.Delete)]` on an FK field makes `tx.Destroy()` on the parent
recursively destroy every child still pointing to it — validated cycle- and diamond-free at registration.
Reactive FK joins (`NavigateField`/`NavigationView`, see [Query System](./query-system.md)) walk a plain
`long`-typed indexed field rather than `EntityLink<T>` directly — model that field as `[Index] public long
OwnerId;` alongside (or instead of) a typed `EntityLink<T>` field if a relationship needs one.

## 💻 Usage

```csharp
public struct ItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<Bag> Owner;
    public int Damage;
}

public struct BagData
{
    public int Capacity;
}

// FK on child — O(1) reverse lookup, no contention on the parent
EntityId itemId = tx.Spawn<Item>(Item.Data.Set(new ItemData { Owner = bagId, Damage = 15 }));

foreach (EntityRef item in tx.Query<Item>().Where(i => i.Owner == bagId))
{
    ref readonly ItemData data = ref item.Read<ItemData>();
}

// Safe follow of a possibly-stale link
if (tx.TryOpen(item.Read<ItemData>().Owner, out EntityRef bag))
{
    // alive — use it
}

// Cascade delete — destroying the bag destroys every Item whose Owner points to it
tx.Destroy(bagId);
```

| Relationship shape | Mechanism | Reverse lookup | Reactive join (`NavigateField`) |
|---------------------|-----------|-----------------|-----------------|
| 1:1 | `EntityLink<T>` field, optionally `[Index]` | Only if indexed | Needs a separate `long` `[Index]` field |
| 1:N (FK on child) | `[Index(AllowMultiple = true)]` `EntityLink<T>` | O(1) field read | Needs a separate `long` `[Index]` field |
| 1:N (collection on parent) | `ComponentCollection<EntityLink<T>>` | Not indexed | Not applicable |
| N:M | Junction archetype with two indexed `EntityLink<T>` fields | Both sides indexed | Needs a separate `long` `[Index]` field |

## ⚠️ Guarantees & limits

- `EntityLink<T>` is 8 bytes, same layout as `EntityId` — zero overhead over a raw reference; construction
  asserts (debug builds) that the target archetype is in `T`'s subtree.
- A unique `[Index]` (no `AllowMultiple`) on an `EntityLink<T>` field rejects a second entity claiming the same
  target — enforced at index-update time, at commit.
- Cascade delete (`OnParentDelete = CascadeAction.Delete`) only follows indexed FK fields; the cascade graph is
  validated at registration — cycles and diamonds both fail startup with a descriptive error, so cascade is
  always a bounded tree traversal. All cascaded destroys commit atomically with the parent destroy.
- `ComponentCollection<EntityLink<T>>` relationships are not indexed — no reverse lookup, no independent query
  on members, and concurrent writers to the same parent collection retry on conflict (microsecond cost,
  in-process). Cascade for these is a parent-side iterate-and-destroy, not an index scan.
- Opening a stale (destroyed, non-cascaded) link fails the MVCC visibility check; use `tx.TryOpen` to handle it
  without an exception.
- `NavigateField`/`NavigationView` take an `Expression<Func<TSource, long>>` FK selector — a raw indexed `long`
  field, not an `EntityLink<T>` field directly (no implicit conversion exists between the two). `EntityLink<T>`
  fields work with `Where`, cascade delete, and `tx.Open`/`tx.TryOpen`, just not a reactive FK join today.

## 🧪 Tests

- [CascadeDeleteTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/CascadeDeleteTests.cs) — cascade graph validation, multi-level and mixed-ownership cascade, already-dead children, same-transaction cascade
- [EcsHardeningTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsHardeningTests.cs) — `EntityLink<T>` implicit conversion round-trip, null detection, `IsAlive`/`Open` via a link, deep cascade hierarchies
- [EcsNavigationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsNavigationTests.cs) — `NavigateField` reactive FK join, combined source/target predicates, incremental view over the join

## 🔗 Related

- Source: `src/Typhon.Engine/Ecs/public/EntityLink.cs`, `src/Typhon.Engine/Ecs/public/EcsNavigationQueryBuilder.cs`,
  `src/Typhon.Engine/Ecs/internals/ArchetypeRegistry.cs` (cascade graph validation)
- Related features: [Query System](./query-system.md), [Automatic Secondary Indexes](../Indexing/secondary-index-storage-modes/README.md),
  [ECS Query API](../Querying/README.md)

<!-- Deep dive: claude/design/Ecs/08-entity-relationships.md -->
