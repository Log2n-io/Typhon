# Subscription Management (SetSubscriptions)
> Atomic, idempotent, diff-based API to set a client's full subscription list each tick.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

A connected client's subscription needs change constantly — zone transitions, party joins, UI panels opening
and closing. Hand-rolling this as incremental subscribe/unsubscribe calls forces the caller to track what the
client is currently subscribed to, and risks leaked subscriptions (a forgotten unsubscribe) or a client briefly
observing a half-applied state (some Views swapped, others not). `SetSubscriptions` removes both problems: the
caller always declares the client's *target* subscription state, and the runtime guarantees it gets there as a
single atomic step, with no networking or change-tracking code to write.

## ⚙️ How it works (in brief)

`SetSubscriptions` takes a client's complete desired list of `PublishedView` handles and replaces the pending
set for that connection. On the next tick's Output phase, the runtime diffs this list against the client's
currently active subscriptions: Views present only in the new list are subscribed (incremental sync begins),
Views present only in the old list are unsubscribed, and Views present in both are left untouched — no resync,
no interruption. The resulting events and any deltas land in that tick's single `TickDeltaMessage`, so the
client never observes an intermediate state. Today, the full list is always supplied by server-side game code
(v1); a client-initiated request path is planned (v2).

## Sub-features

| Sub-feature | Status | Use it for |
|-------------|--------|-----------|
| [Server-Driven Subscriptions (v1)](./subscription-server-driven.md) | ✅ Implemented | Game systems call `SetSubscriptions` directly when game state changes (zone transition, party join) |
| [Client-Initiated Subscriptions (v2)](./subscription-client-initiated.md) | 📋 Planned | Clients request their own subscription changes, validated server-side before being applied |

## ⚠️ Guarantees & limits

- **Idempotent** — calling with an identical list is a no-op; no events are generated.
- **Atomic** — the full transition (unsubscribe + subscribe + keep) lands in a single tick's `TickDeltaMessage`; the client never sees a partially-applied set.
- **Full list, not a delta** — always pass the complete desired set. The runtime converges to exactly that state, so there is no way to leak a subscription via a missed unsubscribe call.
- **Silently ignored for dead connections** — if the client disconnected between the caller obtaining its `ClientContext` and this call, the request is dropped; no exception, no error path to handle.
- Kept Views never resync, even when other Views in the same call are added or removed.
- New Views in the set go through normal incremental sync (batched across ticks for large Views) before reaching steady-state delta flow.

## 🧪 Tests

- [SubscriptionTransitionTests](../../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionTransitionTests.cs)
  — `ComputeTransition` diff logic: empty→new, all→empty, partial overlap, identical-set no-op, and the
  `Subscribed` event's `ViewName`

## 🔗 Related

- Related feature: [Published Views](../published-views/README.md), [Published/System-Input View Separation](../published-view-isolation.md)
- Sub-features: [Server-Driven Subscriptions (v1)](./subscription-server-driven.md), [Client-Initiated Subscriptions (v2)](./subscription-client-initiated.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Subscription Management, R-Q16 -->
