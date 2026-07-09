---
uid: feature-subscriptions-subscription-management-subscription-client-initiated
title: 'Client-Initiated Subscriptions (v2)'
description: 'Clients request their own subscription changes; the server validates before applying them.'
---

# Client-Initiated Subscriptions (v2)
> Clients request their own subscription changes; the server validates before applying them.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

Server-driven subscriptions (v1) require game server code to be the one deciding every subscription change.
Some interaction patterns are more naturally client-initiated — a player opens a UI panel that needs a View
it isn't already subscribed to, or browses a list before deciding what to commit to. Forcing every such case
through a server-side system adds plumbing the game code shouldn't need to write. Client-initiated
subscriptions let the client ask directly, while keeping the server as the final authority over what it is
allowed to see.

## ⚙️ How it works (in brief)

The plan is a server-side `OnClientSubscriptionRequest` callback: the client sends a subscription request
(by View name), the callback validates it against game/permission rules, and only on acceptance does the
runtime fold the change into that client's subscription set using the same diff-and-apply machinery as
`SetSubscriptions`. Rejected requests never reach the active set. This is additive to v1 — server-driven
calls keep working unchanged, and a client request is just another way to populate the same target list.

## 💻 Usage

```csharp
// Illustrative only — design complete, not implemented yet.
// runtime.OnClientSubscriptionRequest((client, requestedViewName) =>
// {
//     // Server-side validation — e.g. zone/permission check
//     return PlayerCanSee(client, requestedViewName);
// });
```

## ⚠️ Guarantees & limits

- **Not implemented.** No code under `src/Typhon.Engine/Subscriptions` defines `OnClientSubscriptionRequest`
  today; v1 is server-driven only (see [Server-Driven Subscriptions](./subscription-server-driven.md)).
- **Server validation is the point.** Unlike v1, the runtime will not blindly trust a `PublishedView` request — the callback decides whether a connection may subscribe to a given name.
- **Expected to reuse v1's transition machinery.** Once a request is accepted, it is expected to fold into the same atomic diff-based transition `SetSubscriptions` already provides — no second delta-delivery path.

## 🔗 Related

- Parent feature: [Subscription Management (SetSubscriptions)](./README.md)
- Sibling: [Server-Driven Subscriptions (v1)](./subscription-server-driven.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Client-Initiated (v2), Scope (v1 / Demo) -->
