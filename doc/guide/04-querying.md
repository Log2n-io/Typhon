---
uid: guide-querying
title: '4 — Querying & views'
description: 'Spawning and reading by id only gets you so far. Real work means asking questions of your data — "every Imperial on this planet", "everything within 15…'
---

# 4 — Querying & views

Spawning and reading by id only gets you so far. Real work means asking questions of your data — *"every Imperial on this planet"*, *"everything within 15 metres of here"*, *"what changed since last tick?"*. That's this chapter. It's the most feature-dense in the guide, because querying is where you'll spend a lot of your time.

Two shapes of question:

- **One-shot queries** — ask now, get an answer (`Execute` / `Count` / `Any` / iterate).
- **Live views** — ask once, keep a result set that stays current as data changes, and tells you the *delta* each time.

Everything starts from `tx.Query<TArchetype>()`.

---

## 1. Building a query

`Query<Character>()` returns a builder you refine with chainable filters. Nothing runs until a terminal call (§2).

### Which entities — by component shape

```csharp
tx.Query<PlayerArch>()
  .With<Membership>()      // only players who have a Membership component
  .Without<Wallet>()       // …and no Wallet at all
  .Enabled<Session>()      // …whose Session component is currently enabled (i.e. online)
```

`With`/`Without` filter on component presence; `Enabled`/`Disabled` filter on the per-entity enable flag — a cheap per-entity toggle that costs no storage and no index. It's how the sample shard models states rather than deleting data: an **offline** player is one whose `Session` is disabled, a **depleted** deposit is one whose `Deposit` is disabled (the data stays readable), a **broken** harvester is one whose `MaintenanceState` is disabled.

`Exclude<TArch>()` drops a whole sub-archetype — useful with the archetype hierarchy from [ch.2 §4](02-modeling.md#4-relationships--when-one-archetype-isnt-enough):

```csharp
tx.Query<StructureArch>()          // polymorphic: every Harvester AND every Factory
  .Exclude<FactoryArch>()          // …minus the factories
```

> 💡 **Polymorphic by default.** `Query<StructureArch>()` matches the archetype **and its whole subtree**. When you want exactly one shape and none of its descendants, use `tx.QueryExact<HarvesterArch>()`. This is the payoff of declaring a base archetype: one query for the family, one for a leaf.

### Which entities — by field value

This is the distinction that matters most for performance:

```csharp
// Indexed-field predicate → the engine drives the scan from the index (fast, selective)
tx.Query<Character>().WhereField<Faction>(f => f.Value == Factions.Imperial)

// Free predicate → evaluated per candidate entity (broad scan)
tx.Query<Character>().Where<Ham>(h => h.Health < h.MaxHealth)
```

> 💡 **`Where` vs `WhereField` — pick deliberately.** `Where<T>(lambda)` takes any C# predicate and runs it against *every* entity the rest of the query admits — total freedom, linear cost. `WhereField<T>(expression)` is restricted to an **indexed** field and a comparable expression, which lets the engine narrow the candidates *through the index* instead of scanning, and is the form that backs an **incremental** live view (§3) — a free `Where` can still back a *pull* view that recomputes on refresh. Rule of thumb: filter on an indexed field with `WhereField`; use `Where` for computed or non-indexed conditions (like `Health < MaxHealth`, which compares two fields and can't be a simple index lookup). You can chain both — `WhereField` to narrow, `Where` to refine.

### Which entities — by geometry

If a component has `[SpatialIndex]` (our `Bounds`, from [ch.2](02-modeling.md)), query it spatially — these run off the spatial index (`Execute` / `Count` / `Any` all apply the predicate):

```csharp
tx.Query<Character>().WhereNearby<Bounds>(x, y, 0f, 15f).Execute()                  // x, y, z, radius
tx.Query<Character>().WhereInAABB<Bounds>(minX, minY, 0f, maxX, maxY, 0f).Execute() // inside a box
tx.Query<Character>().WhereRay<Bounds>(ox, oy, 0f, dx, dy, 0f, 50f).Execute()       // origin, dir, maxDist
```

A spatial predicate composes with the field/`Where` filters above — `WhereNearby<Bounds>(x, y, 0f, 15f).WhereField<Faction>(f => f.Value == Factions.Imperial)` returns the **intersection** (in range *and* Imperial).

> ⚠️ The spatial index is maintained at the tick fence ([ch.5](05-systems.md)); from a bare transaction, run `dbe.WriteTickFence(n)` once after spawning so the index reflects current positions before you query.

### Ordering & paging

```csharp
tx.Query<PlayerArch>()
  .WhereField<Player>(p => p.Level >= 10)
  .OrderByFieldDescending<Player, int>(p => p.Level)   // or OrderByField for ascending
  .Skip(10).Take(20)
  .ExecuteOrdered();                                   // ordered results need their own terminal
```

> ⚠️ **Three constraints that will bite you otherwise.** `OrderByField` requires (1) a **`WhereField` first** — that's what tells the engine which component table to order within; (2) an **indexed** field to order by — ordering is served by walking the index in key order, so an unindexed field throws; and (3) the **`ExecuteOrdered()`** terminal, which returns an ordered `List<EntityId>`. Plain `Execute()` returns an unordered set and *throws* if you attached an `OrderBy` — deliberately, rather than silently discarding your ordering. `Skip`/`Take` likewise require an `OrderBy` (paging an unordered set is meaningless).

---

## 2. Running a query

A query does nothing until a **terminal** call. Pick the one that matches what you need:

```csharp
HashSet<EntityId> ids = q.Execute();        // materialise all matches (unordered)
List<EntityId> ordered = q.ExecuteOrdered();// ordered matches (requires OrderByField)
int n                 = q.Count();          // just how many
bool any              = q.Any();            // does at least one match?

foreach (EntityId id in q)                  // iterate matches without a HashSet
{
    var ham = tx.Open(id).Read(Character.Ham);
    // …react to each match…
}
```

`Execute` is the workhorse; `Count`/`Any` short-circuit when you don't need the entities; the `foreach` form iterates its own pre-collected match list instead of building a `HashSet` — cheaper than `Execute`, but not fully allocation-free streaming.

> 💡 **Know which scan you triggered.** `Execute` picks one of three paths automatically: a **targeted** scan when you used `WhereField` (index-driven), a **spatial** scan when you used a spatial predicate (spatial-index-driven), or a **broad** scan otherwise (walk the archetype, apply any `Where` predicate per entity). The takeaway for cost: a `WhereField`/spatial query stays cheap as the archetype grows; a pure-`Where` query is linear in archetype size. Both are correct — choose with your data sizes in mind.

The running example's headline query — *Imperials who are hurt* — combines the tools:

```csharp
var woundedImperials = tx.Query<Character>()
                         .WhereField<Faction>(f => f.Value == Factions.Imperial)  // index-narrowed
                         .Where<Ham>(h => h.Health < h.MaxHealth)                 // refined per entity
                         .Execute();
```

---

## 3. Live views — results that stay current

A one-shot query is a snapshot answer. A **view** is a result set that you keep and refresh, and that reports what changed each time — exactly what a reactive system or a UI needs.

```csharp
using var hurt = tx.Query<Character>()
                   .Where<Ham>(h => h.Health < h.MaxHealth / 2)   // two fields → a pull view
                   .ToView();

// later, each tick:
hurt.Refresh(tx);                       // bring the view up to date
foreach (long pk in hurt.GetDelta().Added)
    RaiseAlarm(pk);                     // characters that just dropped below half health
foreach (long pk in hurt.GetDelta().Removed)
    ClearAlarm(pk);                     // …or healed back above / left the set
hurt.ClearDelta();                      // reset the delta for the next cycle
```

A view gives you:

- **Membership & iteration** — `view.Contains(id)`, and `foreach` over its current entities.
- **`Refresh(tx)`** — re-evaluate against the latest data.
- **A delta** — `view.GetDelta()` returns `Added` / `Removed` / `Modified` (entity keys), and `ClearDelta()` resets it for the next round.

> 💡 **Two flavours of view.** A view built on an indexed `WhereField` predicate (field vs. a constant, e.g. `WhereField<Faction>(f => f.Value == Factions.Imperial)`) updates **incrementally**: the engine watches the index and moves only the entities that actually crossed the boundary — never re-running the whole query. A view built on a free `Where` (like our "below half health", which compares two fields and so can't be an index lookup) is a *pull* view: recomputed on `Refresh`. Both report the same `Added` / `Removed` / `Modified` delta — the difference is cost, not capability. Either way, a reactive UI or a streaming server is built on views, not on polling `Execute`.

> 📌 **A view is also how you feed a system.** Every `QuerySystem` in [ch.5](05-systems.md) takes a long-lived view as its input set — you build it once at startup and the runtime hands your system its entities every tick. Views aren't only for UI; they're the standing subscription the tick loop runs on.

---

## 4. Subscriptions — pushing views to clients

When the consumer of a view is remote (a connected client, another process), **subscriptions** publish a view and stream its deltas out. You register a `PublishedView`; the engine pushes Added/Removed/Modified to subscribers as the view refreshes, with per-subscription priority. It's the same view + delta machinery from §3, wired to a transport — so a client can mirror "the characters near my camera" without re-querying. The surface lives in `Subscriptions/` (`PublishedView`, `PublishedViewRegistry`); reach for it when you're building a server, not a single-process sim.

---

## 5. Reading in parallel

Everything above runs on one transaction (one thread). When a query system needs to fan a read-only pass across many worker threads at a single consistent snapshot, that's the **`PointInTimeAccessor`** — one frozen TSN, one accessor per worker, zero per-entity locking. It's the read engine behind parallel systems, so it's covered with the runtime in [ch.5](05-systems.md). For now: know that "query a million entities across all cores at one snapshot" is a first-class, supported pattern.

> 💡 **The name is about *parallelism*, not time travel.** `PointInTimeAccessor` freezes one *current* snapshot so every worker reads the same consistent moment — it does **not** read historical/past versions (Typhon has no user-facing as-of-past-version read API). The only "as-of" you get is snapshot isolation: a consistent view fixed at your transaction's start. See the cheat sheet's [naming traps](isolation-durability-cheatsheet.md#8-naming-traps).

> A note on planning: the engine keeps lightweight **statistics** about component data and uses them when choosing how to run targeted scans. Like indexes, it's bookkeeping you benefit from but never maintain.

---

## 🧭 What's next

You can now find data (one-shot) and observe it (live views). The last big piece is *running logic over it continuously*:

- **[Chapter 5 — Systems & the tick loop](05-systems.md):** systems, the scheduler, parallel reads with `PointInTimeAccessor`, and how one UoW per tick drives the whole thing.
- **[Chapter 6 — Operating & going deeper](06-operating.md):** observability, resource budgets, error handling, and the map into the in-depth reference.

## 🧩 Key concepts & types

**Concepts:** [Query](../key-concepts/query.md) · [View](../key-concepts/view.md) · [Subscription](../key-concepts/subscription.md) · [Snapshot isolation](../key-concepts/snapshot-isolation.md) · [PointInTimeAccessor](../key-concepts/point-in-time-accessor.md).

**Exact calls:** `tx.Query<TArch>()` / `tx.QueryExact<TArch>()` → `EcsQuery` · `With` / `Without` / `Exclude` / `Enabled` / `Disabled` · `Where` (broad) vs `WhereField` (indexed) · `WhereNearby` / `WhereInAABB` / `WhereRay` · `OrderByField` / `OrderByFieldDescending` / `Skip` / `Take` · `Execute` / `ExecuteOrdered` / `Count` / `Any` / `foreach` · `ToView` → `EcsView` (`Contains` / `Refresh` / `GetDelta` / `ClearDelta`) · `PublishedView` (subscriptions).
