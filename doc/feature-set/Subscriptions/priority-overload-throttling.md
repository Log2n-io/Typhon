---
uid: feature-subscriptions-priority-overload-throttling
title: 'Subscription Priority & Overload Throttling'
description: 'Tag Views Critical/Normal/Low so the server protects player-state delivery when the tick budget is blown.'
---

# Subscription Priority & Overload Throttling
> Tag Views Critical/Normal/Low so the server protects player-state delivery when the tick budget is blown.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A loaded server can't always compute and push every subscription delta on time. Without a priority signal,
the runtime would have to throttle everything equally — including the player-state View that the client's
own UI depends on every tick. Priority lets the developer say up front which Views matter most, so when the
engine's overload detector trips, throttling falls on world-flavor data (weather, ambient NPCs) first, never
on gameplay-critical state.

## ⚙️ How it works (in brief)

Priority is set once, at `PublishView` time, and applies to all subscribers of that View — it is not
per-client. The Output phase reads the runtime's current overload level each tick and decides, per published
View, whether to skip delta computation/push for that tick. `Critical` Views are never skipped. `Normal` and
`Low` Views are skipped on a tick-number modulus that gets stricter as overload climbs — the View's ring
buffer still drains underneath (no missed-update bugs), only the push to clients is withheld, and the next
non-skipped tick simply reports more accumulated change.

## 💻 Usage

```csharp
runtime.PublishView("player_state", playerStateView, SubscriptionPriority.Critical);
runtime.PublishView("world_npcs", npcView, SubscriptionPriority.Normal);   // default if omitted
runtime.PublishView("weather", weatherView, SubscriptionPriority.Low);
```

| Overload level | Critical | Normal | Low |
|---|---|---|---|
| `Normal` | every tick | every tick | every tick |
| `SystemThrottling` (1) | every tick | every tick | every 2nd tick |
| `ScopeReduction` (2) | every tick | every 2nd tick | every 4th tick |
| `TickRateModulation` (3) | every tick | every 2nd tick | every 4th tick |
| `PlayerShedding` (4) | every tick | every 2nd tick | every 4th tick |

## ⚠️ Guarantees & limits

- `Priority` is fixed at publish time (`PublishView(..., SubscriptionPriority)`) — there is no per-client
  override and no API to change a View's priority after publishing.
- `Critical` Views are pushed every tick at every overload level, including `PlayerShedding` — this is the
  guarantee the feature exists for.
- Throttling only withholds the delta push; the View itself is still refreshed every tick (ring buffer
  drained), so no Added/Removed/Modified entries are lost — a throttled tick's worth of changes is simply
  folded into the next push.
- At `PlayerShedding`, Normal/Low Views keep throttling at the same cadence as `ScopeReduction` — they are not
  automatically paused. Shedding load further (e.g. dropping a client's non-critical subscriptions outright)
  is left to game code via `TyphonRuntime.OnCriticalOverload`.
- Overload level is a single, server-wide value driven by the scheduler's tick-budget detector — it is not
  computed per client or per View beyond the priority comparison above.
- Priority affects subscription push only; it has no effect on whether the View itself is queryable, indexed,
  or usable as a system input elsewhere.

## 🔗 Related

- Related feature: [Published Views](./published-views/README.md)
- Sibling: [Overload Management](../Runtime/overload-management.md) — the scheduler-side overload detector this feature's throttling levels key off

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Subscription Cost & Overload Integration -->
<!-- Deep dive: claude/overview/13-runtime.md — Overload Management -->
