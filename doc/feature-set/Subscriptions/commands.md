---
uid: feature-subscriptions-commands
title: 'Commands & Acknowledgements'
description: 'Typed client intents checked on the transport thread (budget, rate, role, pre-check), drained into the next tick per session and in order; TryResolve scoped to what the session holds; every refusal answered.'
---

# Commands & Acknowledgements
> Intents in, validated where it is cheap, applied where the state is, and always answered.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Client input is untrusted, bursty and concurrent with the simulation. It has to reach systems typed and in order, without a network thread
touching the world; abuse has to be bounded before it costs the tick; and a predicting client has to learn which of its commands were
refused, not just that time passed.

## ⚙️ How it works (in brief)

- **Declaration.** `subs.Command<T>(c => …)` over an `unmanaged` struct: `Rate(perSecond, burst)`, `Roles(…)` (none = every role),
  `Coalesce(LatestPerSession)` for "only the newest counts" intents, `Precheck(in T → bool)` for stateless validation, `Field`/`Ignore` for
  codecs. Every public field travels; one with no default codec must be declared. Attributes on the struct can declare codecs instead
  ([Replication by attributes](replication-attributes.md)).
- **On the transport thread**, per message: the session's inbound byte budget, then per command the wire, the role, the type's rate and the
  pre-check. What passes is framed into the session's inbound ring.
- **In the tick**: the Engine-Pre drain moves every ring into typed buffers. `ctx.Subscriptions.Commands<T>()` enumerates them, per session
  and in order; `TryGetLatest(session, out cmd)` for coalesced types. A command is visible for exactly the tick it was drained into (SUB-08).
- **Entity references** are netIds. `TryResolve(session, netId, out entity)` succeeds only for an entity the session **holds** — in its view
  as last sent, or its controlled entity (SUB-26). `TryResolveAny` checks liveness only, for trusted tools.
- **Acknowledgements.** Every refused command is answered in the session's next frame's `ACKS` block — `RATE_LIMITED` (rate or budget),
  `FORBIDDEN` (role), `REJECTED` (pre-check, or your `Reject(cmd, reason)`), `REGION_INVALID`, or an application reason (128–255) — and
  `SELF.lastSeq` settles everything the tick drained (SUB-27).

## 💻 Usage

```csharp
subs.Command<Attack>(c => c.Roles(SessionRole.Player).Rate(4, burst: 4));

foreach (ref readonly var cmd in ctx.Subscriptions.Commands<Attack>())
{
    if (!ctx.Subscriptions.TryResolve(cmd.Session, cmd.Value.Target, out var target))
    {
        ctx.Subscriptions.Reject(cmd, AckReasons.Rejected);
        continue;
    }
    // validate against the world, then apply
}
```

| Setting | Default | Effect |
|---|---|---|
| `SubscriptionsOptions.IngressBytesPerSecond` | **required** with commands | Each session's inbound budget (≥ `ClientMessageBytes`); a `COMMANDS` message over it is refused whole, each command `RATE_LIMITED` |
| `SubscriptionsOptions.ClientMessageBytes` | 1 KiB | Largest client message; bigger closes with 1009 |
| `AbuseWindow` / `AbuseRefusalsPerWindow` / `AbuseWindows` | 1 s / 64 / 3 | Refused commands (budget, rate, role) past the threshold for that many adjacent windows close the session with 1008 |
| `IngressRingBytes` | 4 KiB | Per-session inbound ring; a full ring drops and counts |

## ⚠️ Guarantees & limits

- **Semantic validation is the system's** — range, cooldowns, ownership: the state it must agree with is the world's.
- **A malformed message closes the session** (1007, or 1002 for framing) and delivers none of its commands, even the valid ones before the
  fault.
- **A session places at most 8 transport-side refusals per tick** in the shared acknowledgement log; beyond, refusals settle through
  `lastSeq` and are counted, so one client cannot crowd out others' acknowledgements.
- The built-in `ClientRegion` command (a `ClientRegion` profile's footprint) is rate-limited to 5 Hz and coalesced.
- **Known gap:** a `PING` is charged against the budget but never refused, and each one wakes the session's send pump; a client flooding
  pings is not closed by the abuse rule ([#1025](https://github.com/Log2n-io/Typhon/issues/1025)).

## 🧪 Tests

- [IngressDrainTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/IngressDrainTests.cs) — order, coalescing, rate, roles
- [TryResolveTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/TryResolveTests.cs) — resolves exactly what the session holds, per observer shape
- [IngressHardeningTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/IngressHardeningTests.cs) — budget, acknowledgements, the refusal share, the 1008 close
- [ClientInputFuzzTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ClientInputFuzzTests.cs) — mutated client messages through the real path (20 k per CI run, 1 M nightly)

## 🔗 Related

- Concept: [Client command](xref:concept-client-command)
- [Owner state](owner-state.md) · [Backpressure & budgets](backpressure-budgets.md)
