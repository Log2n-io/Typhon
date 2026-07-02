# Resource Exhaustion Handling
> Every bounded resource declares how it fails — fail fast, wait, evict, or degrade — instead of hanging or throwing a generic error.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)
**Assumes:** [Exhaustion Policy & ResourceExhaustedException](../Resources/exhaustion-policy-handling.md)

## 🎯 What it solves

Every embedded engine has bounded resources: a transaction pool, a page cache, a chunk segment's capacity. When one fills up, the caller needs to know two things immediately — *that* it happened, and whether retrying makes sense. Before this feature, that information was inconsistent: some limits (`MaxActiveTransactions`) were tracked in metrics but never enforced, a full page cache with all pages pinned could spin forever with no bound in Release builds, and a full chunk segment threw a generic `InvalidOperationException` indistinguishable from a programming bug. None of these surfaced a uniform, catchable signal.

## ⚙️ How it works (in brief)

Each bounded resource is tagged with an `ExhaustionPolicy` — `FailFast`, `Wait`, `Evict`, or `Degrade` — describing how it behaves at capacity. The policy is fixed per resource (not configurable) and reflects its semantics: a client-facing limit like the transaction pool fails fast; a cache evicts, then waits, before failing; a durability buffer waits up to its deadline. `FailFast` resources throw `ResourceExhaustedException` directly — a `TyphonException` with `IsTransient = true`, since exhaustion is expected to self-heal as load drains. `Wait` resources instead throw a `TyphonTimeoutException` subclass (`PageCacheBackpressureTimeoutException`, `WalBackPressureTimeoutException`) if the bounded wait itself expires — see [Timeout Exceptions & Deadlines](./timeout-exceptions-deadlines.md) — both signal the same underlying exhaustion, through whichever typed exception fits how the resource waits. `ExhaustionPolicy` is also a settable diagnostic tag on `ResourceNode` in the [resource tree](../Resources/resource-tree-registry.md), so tooling can show *why* a node behaves the way it does — though today only a handful of resources set a non-default value; most enforce their policy via a dedicated throw site without yet tagging their node.

## 💻 Usage

```csharp
var options = new DatabaseEngineOptions
{
    Resources = new ResourceOptions
    {
        MaxActiveTransactions = 200,   // FailFast beyond this
    }
};

using var dbe = new DatabaseEngine(options);

try
{
    using var tx = dbe.CreateQuickTransaction();
    // ... use tx
    tx.Commit();
}
catch (ResourceExhaustedException ex)
{
    // ex.ResourcePath   → "Data/TransactionChain/CreateTransaction"
    // ex.ResourceType   → ResourceType.Service
    // ex.CurrentUsage / ex.Limit / ex.Utilization
    if (ex.IsTransient)
    {
        // back off and retry later — the pool drains as transactions commit/rollback
    }
}
```

| Policy | Behavior at capacity | Example resource |
|---|---|---|
| `FailFast` | Throw `ResourceExhaustedException` immediately | Transaction pool (`MaxActiveTransactions`) |
| `Wait` | Block until freed, bounded by the operation's deadline, then throw | Page cache eviction wait |
| `Evict` | Remove a least-used entry and retry | Page cache, chunk accessor cache |
| `Degrade` | Continue with reduced performance, no exception | Transaction object pool (allocates new), chunk segment auto-grow |

## ⚠️ Guarantees & limits

- **No unbounded hangs.** The page-cache eviction wait is bounded by the caller's deadline (see [Timeout Exceptions & Deadlines](./timeout-exceptions-deadlines.md)) in every build configuration — Release builds no longer spin forever when all pages are pinned.
- **Typed, never generic.** Pool limits and chunk-segment-full surface as `ResourceExhaustedException`; a page-cache or WAL wait that expires surfaces as `PageCacheBackpressureTimeoutException`/`WalBackPressureTimeoutException` instead — never `InvalidOperationException`/`OutOfMemoryException`. `catch (ResourceExhaustedException)` covers the `FailFast` resources; `catch (TyphonException)` covers all of them uniformly.
- **`IsTransient = true` is a hint, not a promise.** Exhaustion is expected to clear as concurrent work drains the resource, but the engine never retries on the caller's behalf — see [IsTransient](./transience-hint.md).
- **`ExhaustionPolicy` is diagnostic metadata, not a runtime dispatcher.** Each resource hardcodes its own escalation logic (evict-then-wait, fail-fast, etc.); the enum on `ResourceNode` documents that behavior for introspection and tooling, it doesn't drive it.
- **Self-healing policies (`Evict`, `Degrade`) can still end in `FailFast`.** E.g. the page cache evicts, then waits, then throws if the deadline still expires with no page free — `Wait`/`Evict` reduce the chance of exhaustion, they don't eliminate it.
- **Limits are fixed at startup.** `ResourceOptions` (e.g. `MaxActiveTransactions`) is set once at `DatabaseEngine` construction and immutable thereafter — no hot-resize.

## 🧪 Tests

- [ExhaustionPolicyTests](../../../test/Typhon.Engine.Tests/Errors/ExhaustionPolicyTests.cs) — `FailFast` enforced at real boundaries (transaction pool over `MaxActiveTransactions`, `ChunkBasedSegment.AllocateChunk(s)`) and recovery once a slot frees up; `ResourceNode.ExhaustionPolicy` metadata tagging per policy value.
- [ResourceOptionsTests](../../../test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — `ExhaustionPolicy` enum completeness, `ResourceExhaustedException` construction/message/`Utilization` (incl. zero-limit edge case).

## 🔗 Related
- Related feature: [IsTransient retry hint](./transience-hint.md)
- Sibling: [Timeout Exceptions & Deadline Propagation](./timeout-exceptions-deadlines.md) — `Wait` resources throw the `TyphonTimeoutException` subclasses this feature describes

<!-- Deep dive: claude/design/Errors/03-exhaustion-policy.md -->
<!-- Overview: claude/overview/10-errors.md, claude/overview/08-resources.md §8.7 -->
