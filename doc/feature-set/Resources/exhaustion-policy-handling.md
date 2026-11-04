---
uid: feature-resources-exhaustion-policy-handling
title: 'Exhaustion Policy & ResourceExhaustedException'
description: 'A typed exception for bounded resources hitting their limit, plus policy metadata that documents — but does not yet drive — the response.'
---

# Exhaustion Policy & ResourceExhaustedException
> A typed exception for bounded resources hitting their limit, plus policy metadata that documents — but does not yet drive — the response.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Resources](./README.md)

## 🎯 What it solves

Bounded resources (transaction slots, epoch registry slots, growable segments) eventually run out.
Applications need a single, typed way to detect "this resource is full" and decide whether to
retry, back off, or surface an error — instead of catching generic exceptions or timeouts and
guessing at the cause. `ResourceExhaustedException` is that signal: it names the resource, its
current usage, and its limit, so callers can react programmatically rather than by parsing a
message string.

## ⚙️ How it works (in brief)

Each `ResourceNode` records an `ExhaustionPolicy` (`None`, `FailFast`, `Wait`, `Evict`, `Degrade`)
at construction, describing how that component is *supposed* to behave when full. Today this is
**diagnostic metadata only** — nothing reads it to dispatch behavior. The actual FailFast paths are
hand-coded independently at each call site: transaction creation past `MaxActiveTransactions`
(`TransactionChain.CreateTransaction`), UoW ID allocation (`UowRegistry.AllocateUowId`), segment growth
(`ChunkBasedSegment.AllocateChunk`/`AllocateChunks`), and the epoch thread registry all throw `ResourceExhaustedException` directly when they hit capacity. Wait/Evict/Degrade
components (page cache, WAL staging buffers, transaction pooling) implement the described behavior
in their own code, not through the enum. Treat `ExhaustionPolicy` as documentation you can read off
a node, not a switch you can hook.

## 💻 Usage

```csharp
using Typhon.Engine;

try
{
    using var tx = dbe.CreateQuickTransaction(); // FailFast: throws past Resources.MaxActiveTransactions
}
catch (ResourceExhaustedException ex)
{
    // ex.ResourcePath   -> "Data/TransactionChain/CreateTransaction"
    // ex.CurrentUsage / ex.Limit / ex.Utilization
    if (ex.IsTransient)
    {
        // Safe to retry after a short backoff — slots free up as other transactions finish.
    }
}

// CreateUnitOfWork waits for a free UoW registry slot instead of failing immediately, and throws
// ResourceExhaustedException ("Execution/UowRegistry/AllocateUowId") only once its deadline expires:
using var uow = dbe.CreateUnitOfWork(timeout: TimeSpan.FromMilliseconds(50));

// ExhaustionPolicy is inspectable but purely informational:
var node = (ResourceNode)registry.FindByPath("Root/Durability/StagingBufferPool");
Console.WriteLine(node.ExhaustionPolicy); // ExhaustionPolicy.Wait — describes intent, not enforced by this property
```

## ⚠️ Guarantees & limits

- `ResourceExhaustedException.IsTransient` is always `true` — the resource may self-heal (a slot
  frees up, a pool drains); callers should treat it as retryable, not fatal.
- Confirmed FailFast throw sites today (all engine-internal; you meet them through public calls such as
  `CreateQuickTransaction` / `CreateUnitOfWork`): `TransactionChain.CreateTransaction` (past
  `MaxActiveTransactions`), `UowRegistry.AllocateUowId` (with and without a deadline),
  `ChunkBasedSegment.AllocateChunk`/`AllocateChunks`/`EnsureCapacity` (segment at max capacity),
  `PagedHashMap` bucket split, the revision-chain length cap, and the epoch thread registry
  (`ThrowHelper.ThrowEpochRegistryExhausted`).
- `ExhaustionPolicy` on `ResourceNode` is **not** enforced anywhere — setting it, or reading it back,
  has no runtime effect. Don't build logic that branches on a node's `ExhaustionPolicy`; it will not
  match actual behavior for every component and may not be updated when a component's real strategy
  changes.
- `ResourceType`, `CurrentUsage`, and `Limit` are populated by the call site, not derived — a bug at
  the throw site (e.g. stale usage count) is not caught by the exception type itself.
- Not all Wait/Evict/Degrade components surface `ResourceExhaustedException` on their slow path —
  some report exhaustion as a timeout or a boolean return instead; check the specific component's
  API before assuming a uniform exception contract engine-wide.

## 🧪 Tests
- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — `ExhaustionPolicy` enum values,
  `ResourceExhaustedException` construction/utilization/message formatting
- [ExhaustionPolicyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/ExhaustionPolicyTests.cs) — real FailFast enforcement at
  `TransactionChain.CreateTransaction` (past `MaxActiveTransactions`) and the `ChunkBasedSegment.AllocateChunk`/`AllocateChunks` throw paths, `ResourceNode.ExhaustionPolicy` metadata

## 🔗 Related

- Sibling: [Resource Budget Configuration](./resource-budgets-options.md) — the limits whose exhaustion this feature signals.
- Sibling: [Resource Exhaustion Handling](../Errors/resource-exhaustion-handling.md) — the application-facing policy enforcement built on this signal (cross-category).

<!-- Deep dive: claude/design/Resources/07-budgets-exhaustion.md -->
<!-- Design: claude/design/Errors/03-exhaustion-policy.md -->
<!-- Overview: claude/overview/08-resources.md §on ExhaustionPolicy (D12) -->
