# Per-Tick Delta Computation & Encoding
> Each tick, the engine figures out exactly what changed in a published View and encodes only that.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

A client cache only needs three things per tick: what entered, what left, and what changed on entities that
stayed. Computing that diff against the previous tick — across every component, including changes that never
touch an indexed field — is exactly the kind of bookkeeping a game developer should never write by hand.
`Per-Tick Delta Computation & Encoding` is the step that turns a published View's raw state into that
Added/Removed/Modified triple and puts only the changed bytes on the wire, with zero query code from the
developer.

## ⚙️ How it works (in brief)

After `WriteTickFence`, the Output phase refreshes every published View and reads two sources of change: the
View's own ring buffer (entities whose `[Index]`-marked fields changed) and `PreviousTickDirtyBitmap` (every
SV/Transient component chunk written this tick, indexed or not). The union becomes the View's Modified set —
this is what makes "any field changed" detection work, not just indexed ones. For each Modified entity, only
the components whose chunk was actually dirty are read and encoded; untouched components on the same entity
are omitted entirely. Added entities get a full component snapshot; Removed entities are just an ID. The
result is handed to the wire-transport layer for serialization (see [TCP Transport & Wire
Format](../wire-transport.md)) — this feature stops at "what bytes represent this change," not how they
travel.

## Sub-features

| Sub-feature | Status | Use it for |
|-------------|--------|-----------|
| [Component-Level Dirty Encoding (v1)](./delta-encoding-component-dirty.md) | ✅ Implemented | Default behavior today — every Modified entity sends full bytes for each dirty component |
| [Per-Field Dirty Encoding (v1.1)](./delta-encoding-per-field-dirty.md) | 📋 Planned | Future bandwidth optimization — send only the fields that actually changed within a dirty component |

## ⚠️ Guarantees & limits

- **Modified detection covers all field changes, not just indexed ones** — the `PreviousTickDirtyBitmap`
  supplement exists specifically because the View ring buffer only fires for `[Index]`-marked fields.
- **Per-component filtering, not per-entity** — a Modified entity only carries the components whose chunk was
  dirty this tick; components that didn't change are never serialized even if the entity itself did change.
- **Delta computation happens once per View, not once per client** — every subscriber to a shared View reuses
  the same Added/Removed/Modified result; cost scales with View size and change volume, not subscriber count.
- **Versioned components are read unconditionally** for Modified entities — they have no `DirtyBitmap`, so the
  encoder includes them whenever the entity is in the Modified set rather than risk omitting a real change.
- This feature only determines *what* changed and produces the wire structs — see [TCP Transport & Wire
  Format](../wire-transport.md) for serialization, framing, and delivery.

## 🧪 Tests

- [ViewDeltaTests](../../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/ViewDeltaTests.cs) —
  `DeltaBuilder_SharedView_ProducesCorrectDelta`/`DeltaBuilder_SecondCall_OnlyNewEntities`: Added/Modified/Removed
  computed once per View, not once per client
- [SubscriptionStressTests](../../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionStressTests.cs)
  — `HighEntityChurn_SpawnDestroy_DeltasConsistent`: sustained spawn/destroy churn under an active subscription

## 🔗 Related

- Related feature: [Shared Views](../published-views/shared-views.md), [TCP Transport & Wire Format](../wire-transport.md)
- Sub-features: [Component-Level Dirty Encoding (v1)](./delta-encoding-component-dirty.md), [Per-Field Dirty Encoding (v1.1)](./delta-encoding-per-field-dirty.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Modified Entity Detection, Message Format -->
<!-- Deep dive: claude/overview/13-runtime.md — Subscription Server -->
