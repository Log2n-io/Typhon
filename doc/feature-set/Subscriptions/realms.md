---
uid: feature-subscriptions-realms
title: 'Sessions in Realms'
description: 'Replication across isolated worlds: a session is in one realm at a time, each realm is served by its own replication at its own scale, and a realm switch is one reset carrying the new realm.'
---

# Sessions in Realms
> Several worlds on one server — planets, interiors, space — each replicated at its own scale; a client is in one of them at a time.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A realm is an isolated world with its own spatial grid, cell size and dimensionality. A client must see the realm it is in and nothing of any other,
even where two realms use the same local coordinates; a realm nobody watches must cost replication nothing; and a player walking through a door
must switch worlds without a reconnect.

## ⚙️ How it works (in brief)

- **One realm per session.** The application places a session (`Place(session, realm, position)`, `Enter(session, realm)`, `Leave(session)`), or
  its profile follows an entity (`AroundControlled()`, `Bind(entity)`), whose realm after the tick's fence is the session's — a teleport switches its
  sessions in the same tick. With several realms a session nobody placed is in none.
- **The switch is one frame**: a `RESET` whose first block is `REALM` — the realm's bounds, cell, position width, kind and tag — over which every
  position that follows decodes. The committed realm (`RealmOf`) moves only when that frame is published; a skipped switch is retried.
- **Each realm with a session is served by its own replication**, built on first use from the realm's `RealmConfig.Replication` (kind, cell, tag)
  and put to sleep when its last session has left; a realm no session is in costs no replication work.
- **Kinds and variants.** `subs.RealmKinds("planet", "interior")`; a profile's `In(kind, v => …)` serves realms of that kind with other observers
  (a World in a one-room interior, a wide Sphere in space), `NotIn(kinds)` serves nothing there — one profile name for every scale.
- **Events are realm-scoped**: `RouteNear(point, realm)` and `RouteToKnown` file in the point's or entity's realm; `RouteToRealm(realm, subtree)`
  reaches a realm, or with its subtree the realms below it in the `Parent` tree.
- **Commands** are decoded over the realm the client held when it built them; a position built in a realm the session has left is refused with
  `ACK REALM_CHANGED`, and every command carries `ClientCommand.Realm`.

## 💻 Usage

```csharp
dbe.Realms.Register(new RealmId(7), new RealmConfig
{
    Grid = SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(64, 64), 16),
    WhenUnobserved = RealmUnobserved.Sleep, SleepAfterTicks = 200, UnobservedTickDivisor = 1,
    Parent = new RealmId(1),                                      // a cantina on planet 1: routing only
    Replication = new RealmReplicationConfig { Kind = "interior", CellM = 16, AppTag = 42 },
});

subs.RealmKinds("planet", "interior");
subs.Profile("player", p =>
{
    p.Sphere(192, leave: 208).AroundControlled().Of<Player>().Of<Npc>();
    p.In("interior", v => v.World().AroundControlled().Of<Player>().Of<Npc>());
});

ctx.Subscriptions.Enter(godSession, new RealmId(1));             // a camera with no entity to follow
```

## ⚠️ Guarantees & limits

- A session holds, hears and resolves only its own realm (SUB-28); a switch is one published `RESET|REALM` (SUB-29); a positioned value is encoded
  and decoded with exactly one realm's frame (SUB-30).
- An entity moving between realms leaves one and enters the other with a new network identity.
- A realm's position width is the replication block layout's (24 bits); a realm without `Replication` takes no session.
- Not built: `At(realm, position)` (a fixed anchor is realm 0), `Follow(entity)`, `SessionsIn(realm)`, the `RealmClosed` session event, per-realm
  position widths.

## 🧪 Tests

`RealmSessionTests`, `RealmEventTests`, `RealmReplicationTests` (engine); the `REALM` goldens and refusals (protocol, both SDKs).

## 🔗 Related

- [Wire protocol & catalog](wire-protocol.md) — the `REALM` block · [Profiles & observers](profiles-observers.md) · [Events](events.md)
- Design: `claude/design/Subscriptions/12-realms.md` · Decision: ADR-068
