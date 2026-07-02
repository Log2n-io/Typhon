# Per-client View Factories
> A `Func<ClientContext, ViewBase>` that builds a fresh, parameterized View for each subscriber.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

Some data is private per connection — inventory, quest progress, anything scoped to "whoever is asking." A
single shared View can't express that: every subscriber would see the same query result. Per-client View
factories let you register one logical subscription name (`"my_inventory"`) whose actual query is built lazily,
per subscriber, parameterized by that subscriber's `ClientContext` — without hand-rolling a separate
registration call (and a separate name) per connected player.

## ⚙️ How it works (in brief)

`PublishView(name, factory)` registers a `Func<ClientContext, ViewBase>` instead of a View instance. No View
is created at registration time. When a client is added to this subscription (via `SetSubscriptions`), the
runtime invokes the factory with that client's `ClientContext`, refreshes the returned View to capture its
initial entity set, and starts incremental sync from it — same as a shared View, just scoped to one
subscriber. The View is disposed automatically when the client unsubscribes or disconnects. Because each
subscriber gets its own View instance, delta computation and serialization happen once per subscriber, not
once for the whole published target — the per-subscriber cost the design doc calls out is the tradeoff for
per-client data.

## 💻 Usage

```csharp
[Component("Game.InventoryItem", 1)]
public struct InventoryItem
{
    [Index] public int OwnerId;
    public int ItemId;
    public int Quantity;
}

[Archetype(10)]
public class InventoryArch : Archetype<InventoryArch>
{
    public static readonly Comp<InventoryItem> Data = Register<InventoryItem>();
}

// Factory runs once per subscriber, on subscribe — it has no ambient transaction, so it opens its own.
var myInventory = runtime.PublishView("my_inventory", (ClientContext client) =>
{
    using var tx = runtime.CreateSideTransaction();
    int ownerId = (int)client.UserData;   // set by your own connection-handling code
    return tx.Query<InventoryArch>()
        .WhereField<InventoryItem>(i => i.OwnerId == ownerId)
        .ToView();
});

runtime.SetSubscriptions(clientContext, myInventory);
```

## ⚠️ Guarantees & limits

- **One View instance per subscriber** — `SubscriberCount` on the returned `PublishedView` counts live
  per-client Views, not a shared one; cost scales linearly with subscriber count (refresh + delta + serialize
  per client), unlike shared Views.
- **Created on subscribe, disposed on unsubscribe/disconnect** — the factory is not called at publish time,
  and nothing leaks: every per-client View is torn down when its owning subscription ends.
- The factory runs on the Output-phase thread during subscription-transition processing — keep it cheap; it is
  not parallelized across subscribers.
- `IsShared` is `false` for a factory-registered `PublishedView`; `SharedView` is `null` — there is no single
  instance to inspect via the registry.
- The factory must return a dedicated View instance per call (a fresh `ToView()` result) — it follows the same
  "must not double as a system input" rule as shared Views.

## 🔗 Related

- Sibling: [Published/System-Input View Separation](../published-view-isolation.md)
- Parent feature: [Published Views](./README.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — View Types for Subscriptions, Connection Lifecycle -->
