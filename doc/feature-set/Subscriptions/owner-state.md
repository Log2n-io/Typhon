---
uid: feature-subscriptions-owner-state
title: 'Owner State (SELF)'
description: 'Fields only the session controlling an entity receives — exact health, a wallet, a quest marker — plus the sequence number of its last applied command, in every frame''s SELF block.'
---

# Owner State (`SELF`)
> What only the entity's owner may see, and the acknowledgement a predicting client reconciles against.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A player must see its own exact health, inventory count or objective marker — and nobody else should. A client that predicts its own
movement must know which of its commands the server has applied. Both are per-session facts about one entity.

## ⚙️ How it works (in brief)

- **Owner fields** are declared in the projection's owner section (`.Owner(o => o.Field(…))` or `[Owner]`), in their own change groups.
- **Control**: `ctx.Subscriptions.Session(id).Control(entity)` — staged, applied next tick. Many sessions may control one entity.
- **`SELF` block** in each frame to a controlling session: the entity's netId, its owner groups that changed since the session's last frame,
  and `u16 lastSeq` — the highest command sequence number the tick drained for the session. Every owner group is sent after a change of
  control, a reset, or the session's first `SELF` (SUB-11).
- **No controlled entity** — a spectator, or a controlled entity that was destroyed — is sent as netId 0, once, so the client knows.
- **Skips are unions**: a session that missed frames receives every owner group that changed while it was away (no per-session value copy is
  kept — a pending mask per session, OR-ed by the projection).

## 💻 Usage

```csharp
subs.Archetype<Player>(a => a
    .Motion(Player.Bounds, m => m.Teleport(12))
    // Everyone: an 8-bit health bar.
    .Fraction(Player.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals")
    // The owner only: the exact health, and its mission state.
    .Owner(o => o
        .Field(Player.Vitals, v => v.Health, Codec.VarUInt, name: "health")
        .Field(Player.State, s => s.MissionX, Codec.F32, name: "missionX", group: "mission")));
```

## ⚠️ Guarantees & limits

- An archetype with owner fields must have a position and is kept in replication state even when no profile observes it; a controlled
  entity's `SELF` is sent even when it lies outside the session's view.
- A `Static` archetype cannot declare owner fields (refused).
- Owner fields reach only controlling sessions — a field both public and owner-only is refused by the attribute generator (TPH1108).

## 🧪 Tests

- [SelfBlockTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SelfBlockTests.cs) — owner fields reach the owner only and converge across skips; `lastSeq`; netId 0

## 🔗 Related

- [Commands & acknowledgements](commands.md) — `lastSeq` and `ACKS`
- Concept: [Replication session](xref:concept-replication-session)
