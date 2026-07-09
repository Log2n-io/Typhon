---
uid: feature-resources-resource-tree-mutation-notifications
title: 'Resource Tree Mutation Notifications'
description: 'Live add/remove events from the resource graph — no polling required.'
---

# Resource Tree Mutation Notifications
> Live add/remove events from the resource graph — no polling required.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](./README.md)

## 🎯 What it solves
Tools that display the resource tree (Workbench's live tree view, a custom monitor) need to know
the moment a node appears or disappears — a new `ComponentTable`, a dropped segment, a closed
session. Polling the tree on a timer is wasteful and laggy. The Resource Tree Registry already
self-registers/unregisters every node; this feature surfaces those topology changes as an event
so consumers can update incrementally instead of re-walking the tree.

## ⚙️ How it works (in brief)
`IResourceRegistry.NodeMutated` is a plain `event Action<ResourceMutationEventArgs>`. It fires once
per `RegisterChild`/`RemoveChild` call that actually changes membership — a duplicate add (same
`Id` already present) is a no-op and raises nothing. The event args carry only the minimal
identification needed to act (`Kind`, `NodeId`, `ParentId`, `Type`, `Timestamp`), not a graph copy.
Each subscriber runs inside its own try/catch on the raising side, so a throwing handler cannot
break delivery to the others or to the registry itself.

## 💻 Usage
```csharp
using Typhon.Engine;

IResourceRegistry registry = services.GetRequiredService<IResourceRegistry>();

registry.NodeMutated += args =>
{
    switch (args.Kind)
    {
        case ResourceMutationKind.Added:
            Console.WriteLine($"+ {args.ParentId}/{args.NodeId} ({args.Type})");
            break;
        case ResourceMutationKind.Removed:
            Console.WriteLine($"- {args.ParentId}/{args.NodeId} ({args.Type})");
            break;
    }
};

// ... later, when no longer interested:
// registry.NodeMutated -= handler;
```

Typhon Workbench's live tree view is the reference consumer: `ResourceGraphStream` subscribes once
per session, coalesces bursts into ~10 frames/second, and pushes them to the browser over SSE so
the tree updates without a refresh.

## ⚠️ Guarantees & limits
- Fires only on actual membership changes: `Added` when `RegisterChild` adds a new `Id`, `Removed`
  when `RemoveChild` finds and removes one. Re-registering an existing `Id` is a silent no-op.
- `ResourceMutationKind.Mutated` is reserved for a future state-change notification — not emitted
  today.
- Handlers must not throw: the registry invokes each subscriber in its own try/catch and swallows
  exceptions to protect the others. Don't rely on exceptions propagating out of a handler.
- Handlers must not mutate the graph (register/remove nodes) from inside the callback — re-entrant
  raises are not supported.
- Delivery is synchronous and in-process, on the calling thread (the thread that constructed or
  disposed the node) — there is no built-in batching or cross-thread marshalling; consumers that
  need rate-limiting (e.g. a UI) must coalesce themselves, as `ResourceGraphStream` does.
- Carries identifiers only (`NodeId`, `ParentId`, `Type`), not a node reference or full subtree —
  look up the actual node via `IResourceRegistry.FindByPath` if more detail is needed.

## 🧪 Tests
- [ResourceRegistryMutationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceRegistryMutationTests.cs) — Added/Removed events,
  duplicate-register no-op, throwing-subscriber isolation, unsubscribe stops delivery

## 🔗 Related
- Catalog: [Resource Tree Registry](./resource-tree-registry.md)

<!-- Design: claude/design/Resources/10-di-and-public-surface.md §3 -->
