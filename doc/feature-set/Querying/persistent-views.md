# Persistent Views — Incremental Refresh & Delta Tracking
> A live, indexed query result set that updates itself in microseconds instead of being re-run every tick.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Querying](./README.md)

**Assumes:** [Indexed Field Predicates (WhereField)](./fluent-query-api/wherefield-indexed-predicate.md)

## 🎯 What it solves

Game loops and UI panels routinely need to know "which entities currently match this condition" once per tick — active quests, nearby enemies, low-health units. Re-running the query every tick against the full table wastes CPU on entities that didn't change, and that cost grows with table size, not with how much actually moved. Persistent Views hold the matching entity set across ticks and update only the entities affected by commits since the last refresh, turning a per-tick cost proportional to table size into one proportional to the number of changes.

## ⚙️ How it works (in brief)

`ToView()` on an indexed-field query (`WhereField`) builds the initial entity set once via the same selectivity-driven execution plan as a one-shot query, then registers the view to receive change notifications for every field it depends on. At commit time, the field-update path that already touches old/new values for the B+Tree index also pushes a small delta record (entity, before-key, after-key) into the view's lock-free multi-producer/single-consumer ring buffer — no extra scan, no polling. `Refresh(tx)` drains that buffer up to the transaction's snapshot, re-evaluates only the changed entities' predicates, and updates the entity set plus an Added/Removed/Modified delta. If the buffer fills up between refreshes (too many changes, too long a gap), the view self-heals by falling back to one full re-query using its cached execution plan, then resumes incremental mode.

## 💻 Usage

```csharp
using var view = tx.Query<ItemArch>()
    .WhereField<ItemData>(i => i.Rarity >= 3)
    .ToView();                                  // → EcsView<ItemArch>, populated immediately

// Game loop
while (running)
{
    using var refreshTx = dbe.CreateQuickTransaction();
    view.Refresh(refreshTx);

    var delta = view.GetDelta();
    foreach (var pk in delta.Added)    SpawnVisual(pk);
    foreach (var pk in delta.Removed)  DespawnVisual(pk);
    foreach (var pk in delta.Modified) UpdateVisual(pk);
    view.ClearDelta();

    // Or iterate the current full entity set
    foreach (var id in view.GetEntityEnumerator()) { /* ... */ }
}
```

| Option | Default | Effect |
|---|---|---|
| `ToView(bufferCapacity:)` | 4096 (power of 2) | Ring buffer slot count; larger absorbs bigger inter-refresh bursts before overflowing, at ~35 bytes/slot unmanaged memory |

`EcsView<TArchetype>` picks its refresh strategy from the predicate shape at `ToView()` time:

| Mode | Created when | Refresh cost |
|---|---|---|
| Incremental | Single `WhereField` branch | O(changes since last refresh) |
| OR | `WhereField` with `\|\|` (multiple branches, max 16) | O(changes), per-entity branch bitmap |
| Pull | No `WhereField` (opaque `Where` or no predicate) | O(full result set), every call — correct, but not incremental |

## ⚠️ Guarantees & limits

- **Indexed fields only.** Every field in the view's predicate must be `[Index]`-annotated — that's what gives commit-time change capture for free. Non-indexed fields are rejected at `ToView()` time; use `.Where(lambda)` one-shot queries for those instead.
- **No held transaction.** A view tracks a TSN watermark, not an open transaction — it never blocks MVCC cleanup, however long it lives.
- **Refresh is explicit and consumer-controlled.** Views never update themselves; call `Refresh(tx)` whenever the caller wants to observe the latest committed state up to `tx`'s snapshot.
- **Delta is net change since `ClearDelta()`**, not a raw log — an entity that enters and leaves between two `ClearDelta()` calls produces no event; one that leaves and re-enters reports as Modified.
- **`ViewDelta`/`GetDelta()` is zero-allocation** and references the view's internal state directly; it is only valid until the next `ClearDelta()`.
- **Overflow is graceful, not fatal.** A full ring buffer sets `HasOverflow`; the next `Refresh()` rebuilds the entity set from the cached execution plan and computes Added/Removed from the diff — but per-field Modified granularity for that cycle is lost.
- **AND, OR, and plain-scan (pull) predicates are all supported** by the same `EcsView<TArchetype>` type — the refresh mode is selected automatically from the predicate shape; OR predicates are capped at 16 DNF branches.
- **Not ordered.** A view is an unordered live set; `OrderByField`/`Skip`/`Take` are rejected on `ToView()`. Re-run `ExecuteOrdered()` per cycle if you need a sorted/paged snapshot.
- **Must be disposed.** `view.Dispose()` deregisters it from change notifications and frees its unmanaged ring buffer; un-drained entries are discarded.
- **Cost profile:** commit-time notification is sub-microsecond per (changed field, watching view); draining ~100-200 changes on refresh is single-digit microseconds.
- **SingleVersion/Transient views are not fully validated yet.** Views over these storage modes are designed to observe state only as of the last tick boundary (after `WriteTickFence` drains ring buffers), but this wiring is still partial — treat tick-boundary consistency for SV/Transient views as unvalidated until it's closed out. Views over `Versioned` components are fully validated. This is the reason this feature is marked Partial rather than Implemented.

## 🧪 Tests

- [EcsIncrementalViewTests](../../../test/Typhon.Engine.Tests/Data/ECS/EcsIncrementalViewTests.cs) — incremental refresh (field crosses in/out), `GetDelta` Added/Removed/Modified, `CompactDelta` net-change collapsing, overflow recovery via full re-query, Pull-mode fallback
- [EcsOrViewTests](../../../test/Typhon.Engine.Tests/Data/ECS/EcsOrViewTests.cs) — OR mode per-entity branch bitmap, entity stays in view while any branch matches
- [EcsViewTests](../../../test/Typhon.Engine.Tests/Data/ECS/EcsViewTests.cs) — base `ToView`/`Refresh` mechanics, `Contains`, delta clearing, dispose semantics
- [ViewChangeCaptureTests](../../../test/Typhon.Engine.Tests/Data/Query/ViewChangeCaptureTests.cs) — commit-time ring-buffer delta entries (before/after keys, multi-field changes, disposed-view skip)
- [ViewRegistryTests](../../../test/Typhon.Engine.Tests/Data/Query/ViewRegistryTests.cs) — view registration/deregistration bookkeeping behind commit-time change notification
- [EcsViewMultiFieldTests](../../../test/Typhon.Engine.Tests/Data/ECS/EcsViewMultiFieldTests.cs) — two ANDed `WhereField` predicates on one view: per-field crossing only re-evaluates that predicate

## 🔗 Related

- Related feature: [Fluent Query API & Predicate Parsing](./fluent-query-api/README.md)
- Also documented as: [Reactive Views (EcsView)](../Ecs/reactive-views.md) in the Ecs category — same feature, since `ToView()` is called on an `EcsQuery`; this page is canonical.
- Sibling: [Published Views](../Subscriptions/published-views/README.md) — registers a View as a subscribable target for connected clients

<!-- Deep dive: claude/overview/05-query.md §5.6 Persistent Views, §5.7 Delta Tracking, §5.8 View Registry -->
<!-- Deep dive: claude/design/Querying/ViewSystem/README.md — full design series (overview, internals, view types, concurrency) -->
<!-- Deep dive: claude/design/Querying/ViewSystem/03-internals.md — ring buffer layout, change capture, refresh/overflow algorithms -->
<!-- Deep dive: claude/design/Querying/ViewSystem/07-concurrency.md — MPSC correctness, disposal safety -->
<!-- Deep dive: claude/design/Ecs/09-implementation-plan.md §Umbrella 4 — SV/Transient tick-boundary wiring, the gap behind the Partial status -->
<!-- ADR: claude/adr/042-view-system-architecture.md — TSN-anchored design rationale (see 2026-05-11 supersession note for current type names) -->
