# 4 — Querying & views

Spawning and reading by id only gets you so far. Real work means asking questions of your data — *"every wounded unit on team 3"*, *"everything within 15 metres of here"*, *"what changed since last tick?"*. That's this chapter. It's the most feature-dense in the guide, because querying is where you'll spend a lot of your time.

Two shapes of question:

- **One-shot queries** — ask now, get an answer (`Execute` / `Count` / `Any` / iterate).
- **Live views** — ask once, keep a result set that stays current as data changes, and tells you the *delta* each time.

Everything starts from `tx.Query<TArchetype>()`.

---

## 1. Building a query

`Query<Unit>()` returns a builder you refine with chainable filters. Nothing runs until a terminal call (§2).

### Which entities — by component shape

> `Shield`/`Stunned`/`Weapon` below are illustrative — they aren't part of the running `doc/guide/example` model (which only has `Position`/`Bounds`/`Health`/`Velocity`/`Team`). The methods themselves are real and verified against source; this snippet just isn't one you can run as-is.

```csharp
tx.Query<Unit>()
  .With<Shield>()        // only units that also have a Shield component
  .Without<Stunned>()    // …and don't have Stunned
  .Enabled<Weapon>()     // …whose Weapon component is currently enabled
```

`With`/`Without` filter on component presence; `Enabled`/`Disabled` filter on the per-entity enable flag (the cheap component toggle from [ch.2](02-modeling.md)). `Exclude<TArch>()` drops a whole sub-archetype.

### Which entities — by field value

This is the distinction that matters most for performance:

```csharp
// Indexed-field predicate → the engine drives the scan from the index (fast, selective)
tx.Query<Unit>().WhereField<Team>(t => t.Id == 3)

// Free predicate → evaluated per candidate entity (broad scan)
tx.Query<Unit>().Where<Health>(h => h.Current < h.Max)
```

> 💡 **`Where` vs `WhereField` — pick deliberately.** `Where<T>(lambda)` takes any C# predicate and runs it against *every* entity the rest of the query admits — total freedom, linear cost. `WhereField<T>(expression)` is restricted to an **indexed** field and a comparable expression, which lets the engine narrow the candidates *through the index* instead of scanning, and is the form that backs an **incremental** live view (§3) — a free `Where` can still back a *pull* view that recomputes on refresh. Rule of thumb: filter on an indexed field with `WhereField`; use `Where` for computed or non-indexed conditions (like `Current < Max`, which compares two fields and can't be a simple index lookup). You can chain both — `WhereField` to narrow, `Where` to refine.

### Which entities — by geometry

If a component has `[SpatialIndex]` (our `Bounds`, from [ch.2](02-modeling.md)), query it spatially — these run off the spatial index (`Execute` / `Count` / `Any` all apply the predicate):

```csharp
tx.Query<Unit>().WhereNearby<Bounds>(x, y, 0f, 15f).Execute()                  // x, y, z, radius
tx.Query<Unit>().WhereInAABB<Bounds>(minX, minY, 0f, maxX, maxY, 0f).Execute() // inside a box
tx.Query<Unit>().WhereRay<Bounds>(ox, oy, 0f, dx, dy, 0f, 50f).Execute()       // origin, dir, maxDist
```

A spatial predicate composes with the field/`Where` filters above — `WhereNearby<Bounds>(x, y, 0f, 15f).WhereField<Team>(t => t.Id == 3)` returns the **intersection** (in range *and* on team 3).

> ⚠️ The spatial index is maintained at the tick fence ([ch.5](05-systems.md)); from a bare transaction, run `dbe.WriteTickFence(n)` once after spawning so the index reflects current positions before you query.

### Ordering & paging

```csharp
tx.Query<Unit>()
  .WhereField<Team>(t => t.Id == 3)
  .OrderByField<Health, int>(h => h.Current)   // or OrderByFieldDescending
  .Skip(10).Take(20)
```

---

## 2. Running a query

A query does nothing until a **terminal** call. Pick the one that matches what you need:

```csharp
HashSet<EntityId> ids = q.Execute();   // materialise all matches
int n               = q.Count();       // just how many
bool any            = q.Any();         // does at least one match?

foreach (EntityId id in q)             // iterate matches without a HashSet
{
    var hp = tx.Open(id).Read(Unit.Health);
    // …react to each match…
}
```

`Execute` is the workhorse; `Count`/`Any` short-circuit when you don't need the entities; the `foreach` form iterates its own pre-collected match list instead of building a `HashSet` — cheaper than `Execute`, but not fully allocation-free streaming.

> 💡 **Know which scan you triggered.** `Execute` picks one of three paths automatically: a **targeted** scan when you used `WhereField` (index-driven), a **spatial** scan when you used a spatial predicate (spatial-index-driven), or a **broad** scan otherwise (walk the archetype, apply any `Where` predicate per entity). The takeaway for cost: a `WhereField`/spatial query stays cheap as the archetype grows; a pure-`Where` query is linear in archetype size. Both are correct — choose with your data sizes in mind.

The full running-example query — *wounded units on team 3, nearest first* — combines the tools:

```csharp
var wounded = tx.Query<Unit>()
                .WhereField<Team>(t => t.Id == 3)        // index-narrowed
                .Where<Health>(h => h.Current < h.Max)   // refined per entity
                .Execute();
```

---

## 3. Live views — results that stay current

A one-shot query is a snapshot answer. A **view** is a result set that you keep and refresh, and that reports what changed each time — exactly what a reactive system or a UI needs.

```csharp
using var lowHp = tx.Query<Unit>()
                    .Where<Health>(h => h.Current < h.Max / 2)   // two fields → a pull view
                    .ToView();

// later, each tick:
lowHp.Refresh(tx);                       // bring the view up to date
foreach (long pk in lowHp.GetDelta().Added)
    Alert(pk);                            // units that just dropped below half HP
foreach (long pk in lowHp.GetDelta().Removed)
    ClearAlert(pk);                       // …or just recovered / left the set
lowHp.ClearDelta();                       // reset the delta for the next cycle
```

A view gives you:

- **Membership & iteration** — `view.Contains(id)`, and `foreach` over its current entities.
- **`Refresh(tx)`** — re-evaluate against the latest data.
- **A delta** — `view.GetDelta()` returns `Added` / `Removed` / `Modified` (entity keys), and `ClearDelta()` resets it for the next round.

> 💡 **Two flavours of view.** A view built on an indexed `WhereField` predicate (field vs. a constant, e.g. `WhereField<Team>(t => t.Id == 3)`) updates **incrementally**: the engine watches the index and moves only the entities that actually crossed the boundary — never re-running the whole query. A view built on a free `Where` (like our "below half health", which compares two fields and so can't be an index lookup) is a *pull* view: recomputed on `Refresh`. Both report the same `Added` / `Removed` / `Modified` delta — the difference is cost, not capability. Either way, a reactive UI or a streaming server is built on views, not on polling `Execute`.

---

## 4. Subscriptions — pushing views to clients

When the consumer of a view is remote (a connected client, another process), **subscriptions** publish a view and stream its deltas out. You register a `PublishedView`; the engine pushes Added/Removed/Modified to subscribers as the view refreshes, with per-subscription priority. It's the same view + delta machinery from §3, wired to a transport — so a client can mirror "the units near my camera" without re-querying. The surface lives in `Subscriptions/` (`PublishedView`, `PublishedViewRegistry`); reach for it when you're building a server, not a single-process sim.

---

## 5. Reading in parallel

Everything above runs on one transaction (one thread). When a query system needs to fan a read-only pass across many worker threads at a single consistent snapshot, that's the **`PointInTimeAccessor`** — one frozen TSN, one accessor per worker, zero per-entity locking. It's the read engine behind parallel systems, so it's covered with the runtime in [ch.5](05-systems.md). For now: know that "query a million entities across all cores at one snapshot" is a first-class, supported pattern.

> A note on planning: the engine keeps lightweight **statistics** about component data and uses them when choosing how to run targeted scans. Like indexes, it's bookkeeping you benefit from but never maintain.

---

## 🧭 What's next

You can now find data (one-shot) and observe it (live views). The last big piece is *running logic over it continuously*:

- **[Chapter 5 — Systems & the tick loop](05-systems.md):** systems, the scheduler, parallel reads with `PointInTimeAccessor`, and how one UoW per tick drives the whole thing.
- **[Chapter 6 — Operating & going deeper](06-operating.md):** observability, resource budgets, error handling, and the map into the in-depth reference.

## 🧩 The types you'll touch

`tx.Query<TArch>()` → `EcsQuery` · `With` / `Without` / `Exclude` / `Enabled` / `Disabled` · `Where` (broad) vs `WhereField` (indexed) · `WhereNearby` / `WhereInAABB` / `WhereRay` · `OrderByField` / `Skip` / `Take` · `Execute` / `Count` / `Any` / `foreach` · `ToView` → `EcsView` (`Contains` / `Refresh` / `GetDelta` / `ClearDelta`) · `PublishedView` (subscriptions).
