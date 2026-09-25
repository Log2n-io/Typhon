---
uid: feature-subscriptions-index
title: 'Subscriptions'
description: 'Engine-owned replication: declared archetype state pushed to remote clients around each session, typed commands drained back into the tick.'
---

# Subscriptions

> Engine-owned replication. An application declares what each archetype exposes and who sees what, and says what it changed; the engine
> does the rest — change detection, quantized encoding, motion segments, per-session visibility, sessions, backpressure and typed inbound
> commands — in parallel on the worker pool, to native and browser clients.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** Subscriptions

## 🎯 What it solves

A game server's state has to reach thousands of clients, each seeing only the part of the world around it, at tick rate, without the
application writing change detection, encoding or networking — and without the cost growing with every client times every entity it
could see. Subscriptions runs replication as an engine track after the tick fence, encodes each changed entity once, and gives each
session only what lies around it.

## ⚙️ How it works (in brief)

**Replication is pushed and explicit.** A system that writes a replicated field calls `Replicate` for that slot; the engine pushes spawns,
destroys, `WriteSpatial` moves and migrations on its own. After the fence, each pushed entity's declared fields are quantized and compared
with what was last encoded — a push that changed nothing costs an encode and no bytes — and what changed is encoded once. The changed
entities are bucketed by cell, and each session's frame is gathered from the cells around it.

**What a session holds is geometry.** A `Sphere` session holds the entities within its radius of its viewpoint, in the cells delivered to
it so far (the enter budget delivers a new view cell by cell, nearest first); a `World` session holds everything. Nothing is stored per
(session, entity), so a session costs a few hundred bytes plus its frames. A session that falls behind is never queued: its next frame
carries the union of what it missed, replayed from an 8-tick push log, or a reset.

Frames are published after the tick's durability flush, handed to the transport by engine-owned send pumps, and decoded by the clients
against a catalog sent at connection — never against C# layouts.

**A session's own entity is private to it.** Fields declared `Owner` travel only to the session that controls the entity, in its frame's
`SELF` block, together with the sequence number of the last command the tick applied and the commands it refused (`ACKS`) — what a
predicting client needs to reconcile.

**Commands are untrusted input.** They arrive typed, per session and in order, after a per-session byte budget, a per-type rate and a role
check; every refused command is answered, and a client that keeps sending what is refused is closed. A command naming an entity resolves it
only if that session was shown it (`TryResolve`).

## 💻 Usage

What an archetype replicates is declared on the data, and a source generator (shipped with the package) turns the attributes into builder
calls:

```csharp
[Archetype(1, "Creatures"), Replicated]
public partial class Creature : Archetype<Creature>
{
    [Motion(ToleranceM = 0.05, TeleportMps = 12)]                    // position as motion segments
    public static readonly Comp<CreaturePlacement> Bounds = Register<CreaturePlacement>();
    public static readonly Comp<CreatureBrain> Ai = Register<CreatureBrain>();
    public static readonly Comp<CreatureVitals> Vitals = Register<CreatureVitals>();
}

public struct CreatureVitals
{
    [Field, Fraction(nameof(MaxHealth), Bits = 8, Name = "hp", Group = "vitals")] public int Health;   // an 8-bit bar for everyone
    [Field] public int MaxHealth;
}
// On a player's component: [Field, Owner(CodecKind.Varu, Name = "credits")] — only the player's own client sees it.
```

The rest — sessions, profiles, commands' policy — is the builder, which also replaces an archetype's attributes when a deployment needs
another projection (`subs.Archetype<Creature>(a => a.Motion(…).Field(…))`):

```csharp
var subs = runtime.Subscriptions;                                   // before runtime.Start()
subs.Sessions.Kinds("player");
subs.Archetype<Creature>();                                          // as its attributes declare
subs.Command<Attack>(c => c.Rate(4, burst: 8));

subs.Profile("player", p => p
    .Sphere(192)                                                     // Detection(PushDetection.Explicit) is the default
    .Of<Creature>());

// In a system: say what you changed.
ai[slot].Mode = AiMode.Flee;
ctx.Subscriptions.Replicate(in cluster, slot);

// Bind sessions to a profile, and place them where their player stands.
foreach (ref readonly var e in ctx.Subscriptions.SessionEvents)
{
    if (e.Kind == SessionEventKind.Opened) { ctx.Subscriptions.Session(e.Session).Profile("player"); }
}
ctx.Subscriptions.Place(session, playerPosition);

// Commands from clients arrive in the tick, per session, in order. An entity a command names resolves only if its session was shown it.
foreach (ref readonly var c in ctx.Subscriptions.Commands<Attack>())
{
    if (!ctx.Subscriptions.TryResolve(c.Session, c.Value.Target, out var target))
    {
        ctx.Subscriptions.Reject(c, AckReasons.Rejected);
        continue;
    }

    /* validate, then apply */
}
```

A command struct lives in a contracts assembly the clients share, with its codecs as attributes —
`[ReplicatedMessage] public partial struct Attack { [EntityRef] public uint Target; }` — and needs only `Typhon.Protocol`.

Clients connect over the built-in TCP transport or through ASP.NET Core (`services.AddTyphonSubscriptions(…)`,
`app.MapTyphonSubscriptions("/ws")` from `Typhon.Subscriptions.AspNetCore`), and decode with `Typhon.Client` (.NET) or the TypeScript SDK.

| Option | Default | Effect |
|--------|---------|--------|
| `SubscriptionsOptions.MaxSessions` | 8 192 | Session table size (hard max 65 535) |
| `SubscriptionsOptions.IngressBytesPerSecond` | **required** with commands | Each session's inbound byte budget, at least `ClientMessageBytes`; a `COMMANDS` message over it is refused whole and acknowledged |
| `SubscriptionsOptions.AbuseWindow` / `AbuseRefusalsPerWindow` / `AbuseWindows` | 1 s / 64 / 3 | Refused commands past the threshold for that many adjacent windows close the session with 1008 |
| `SubscriptionsOptions.EnterBudgetPerFrame` | 500 | Enter records per frame; a new view fills cell by cell under it |
| `SubscriptionsOptions.CloseStalledAfter` | 3 s | A session denied frames this long is closed with 1013; it is degraded a rate class first |
| `SubscriptionsOptions.StatePoolBudgetBytes` | 256 MiB | Ceiling for per-entity replication state (every entity of an observed archetype) |
| `SubscriptionsOptions.FramePoolBudgetBytes` | 256 MiB | Ceiling for frames in flight; exhaustion skips sessions, never allocates |
| `ProfileBuilder.Every(n)` | 1 | Serve the profile's sessions one tick in n (1, 2 or 4); missed ticks are replayed |
| `ProfileBuilder.Detection(PushDetection.Automatic)` | `Explicit` | Engine compares every live entity each tick — experimental, needs `AllowAutomaticPushDetection` |

## ⚠️ Guarantees & limits

- **A write you do not push is not sent.** Clients keep the old value until the entity changes again or is pushed. Spawns, destroys,
  `WriteSpatial` and migrations are pushed for you; content writes are yours. `TYPHON_PUSH_VALIDATE=N` samples whole clusters each tick and
  counts (and heals) unpushed changes, for use in development.
- **Cost follows what changed:** per-tick work is proportional to the entities pushed, never to an archetype's size. An archetype no
  profile observes costs nothing; an observed one keeps ≈ 96 B of state per live entity.
- **Records are absolute and skips are unions:** a session that misses frames converges on its next one, with no retransmission.
- **Correct on x64 and arm64:** every cross-thread hand-off is a named release/acquire pair.
- **Zero steady-state managed allocation** in the replication path.
- **Built today:** one entity observer per profile — `World`, `Sphere` or `ClientRegion` (a client-sent convex footprint) — plus at most one
  `Aggregate` of per-tile counts; flat and volumetric worlds (a world one spatial cell deep is served in the plane, a deeper one in 3D — 2D
  archetypes then live on the plane z = 0); Sphere profiles of any radius, with a leave band (`Sphere(192, leave: 208)`), a run-time range
  (`Sphere(192, max: 1500)` + `SetRadius`) and distance LOD bands; up to 255 replicated archetypes, any number of them observed by one
  profile. The replication cell side is declared (`SubscriptionsOptions.ReplicationCellM`). A Sphere is centred on the viewpoint `Place`
  gives, a fixed point (`At`), one entity (`Bind`) or the session's controlled entity (`AroundControlled`), read after the tick's fence.
  Headings, events routed near an entity or to the sessions that hold one, owner fields in `SELF`, command acknowledgement, and replication
  declared by attributes. **Refused until built:** several entity observers in one profile, per-session `Observe`, and shared sources.
- **Every refused command is answered** — over the budget or the rate (`RATE_LIMITED`), the wrong role (`FORBIDDEN`), a failed pre-check
  or an application `Reject` (`REJECTED`) — and settles the client's `lastSeq`. A client that keeps sending refused commands is closed with
  1008, which both SDKs treat as reconnect-with-backoff.
- **`TryResolve` answers only for entities the session holds** — in its view as last sent, or the one it controls. `TryResolveAny` checks
  liveness only, for trusted tools.
- **Attributes are not schema:** replication attributes never change a component's storage identity, so changing a codec is never a
  migration.
- **A session never placed holds nothing** — not the area around the origin.

## 🧪 Tests

- [PushOracleTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushOracleTests.cs) — decoded clients compared with the server under seeded churn, skips of 0–90 %, walking and teleporting sessions, both detection modes, the forgotten-push mutant
- [FrameAssemblerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/FrameAssemblerTests.cs) — skipped sessions caught up from the log; identical bytes for sessions in the same state
- [SphereObserverTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SphereObserverTests.cs) — a session holds exactly its disc
- [PushOracle3DTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushOracle3DTests.cs) — the oracle in a volumetric world: 3D movers and 2D walkers, climbing and teleporting sessions, a radius that changes without a reset
- [SelfBlockTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SelfBlockTests.cs) — owner fields reach their owner only and converge across skips; `lastSeq` and refusals reach the client
- [TryResolveTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/TryResolveTests.cs) — `TryResolve` accepts exactly what each session's frames built
- [ClientInputFuzzTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ClientInputFuzzTests.cs) — mutated client messages through the real connection, ingress and pumps (1 M nightly)
- [IngressHardeningTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/IngressHardeningTests.cs) — the inbound budget, refusal acknowledgements and the 1008 close
- [ReplicationAttributeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ReplicationAttributeTests.cs) — attributes compile to the builder's catalog to the byte
- Correctness rules: [`rules/subscriptions.md`](https://github.com/Log2n-io/Typhon/blob/main/rules/subscriptions.md) (SUB-01 … SUB-27)

## 🔗 Related

- Related feature: [Overload Management](../Runtime/overload-management.md) — a lagging session is degraded one rate class at a time before it is closed
- Related feature: [Persistent Views](../Querying/persistent-views.md) — shared View sources are a later phase
- Concept: [Subscription](xref:concept-subscription)

<!-- Deep dive: claude/design/Subscriptions/README.md -->
<!-- Deep dive: claude/design/Subscriptions/02-execution.md — the push pipeline -->
<!-- Deep dive: claude/adr/067-push-replication.md -->
