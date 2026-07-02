# Published/System-Input View Separation
> A View used to drive client subscriptions can never double as a system's query input, or vice versa.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Subscriptions stream **incremental** deltas: a published View must accumulate every change made during a tick so the next Output-phase flush can compute exactly what changed since the last flush. If the same View instance were also consumed by a game system mid-tick (as a query input), that system would drain the View's pending change entries for its own use — leaving subscriptions with a gap, and the system with entries it didn't ask for. The two roles need two independent change streams, and Typhon enforces that at the API boundary instead of leaving it as a footgun.

## ⚙️ How it works (in brief)

Every `ViewBase` tracks whether it has been published (`PublishView`) or wired as a system's query input (`QuerySystem(..., input: ...)`). Whichever of these happens second checks the View's state and throws `InvalidOperationException` if the other role is already set. There is no way to "downgrade" a View back to neutral — once a View takes on a role, it keeps it for its lifetime. The fix is always the same: create a second View over the same query, one per role.

## 💻 Usage

```csharp
// Two separate View instances over the same query — one per role.
var npcViewForAI = tx.Query<NPC>().Where(n => n.Health > 0).ToView();
var npcViewForSubs = tx.Query<NPC>().Where(n => n.Health > 0).ToView();

dag.QuerySystem("AI", AiTick, input: () => npcViewForAI);
runtime.PublishView("world_npcs", npcViewForSubs);   // OK — distinct instance

// Reusing the system's input View for publishing throws InvalidOperationException:
// runtime.PublishView("world_npcs", npcViewForAI);
//   "Cannot publish View (ViewId=...) — it is already used as a system input.
//    Published Views must be separate instances. Create a new View with the same query for subscriptions."

// The reverse order throws too, when the DAG resolves:
// runtime.PublishView("world_npcs", npcView);
// dag.QuerySystem("AI", AiTick, input: () => npcView);
//   "Input View (ViewId=...) is already published for subscriptions.
//    Published Views must be separate instances from system input Views."
```

## ⚠️ Guarantees & limits

- **Caught at setup, not at runtime corruption** — the violation throws `InvalidOperationException` as soon as the second role is assigned (publish call or DAG build), before any tick runs with the misconfigured View.
- **One role per View, for its whole lifetime** — a View cannot be reassigned from published to system-input or back; create a new instance per role instead.
- **No extra cost for the common case** — the check is two boolean reads at registration time; it does not affect per-tick delta computation.
- Applies to both shared and per-client published Views — the per-client factory path always creates fresh `ViewBase` instances per subscriber, so this conflict only arises with shared Views and explicit system-input wiring.

## 🧪 Tests

- [ViewSeparationTests](../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/ViewSeparationTests.cs) —
  `UseAsSystemInput_ThenPublish_Throws`/`PublishView_SameInstanceTwice_Throws` cover both violation directions;
  `SeparateViewInstances_SameQuery_NoConflict` shows the correct fix (two instances)

## 🔗 Related

- Related feature: [Server-Driven Subscriptions (v1)](./subscription-management/subscription-server-driven.md), [Per-client View Factories](./published-views/per-client-views.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Published View Constraints, R-Q19 -->
