---
uid: guide-subscriptions
title: '7 — Serving remote clients'
description: 'Your world runs on a tick. Now let players see it and act on it: declare what clients see, who sees what, and which commands they may send — the engine does change detection, encoding, networking and backpressure.'
---

# 7 — Serving remote clients

Everything so far happens inside one process. A game server has to show its world to people: a browser, a game client, a bot swarm. In
most stacks that is a second engine you write around the database — change tracking, serialization, interest management, sockets,
backpressure — and it ends up costing more than the simulation. In Typhon it is part of the engine: **Subscriptions**.

This chapter serves the shard from chapters 1–6 to remote clients. Each player controls a character, sees the characters near it move,
sees its own exact health (others see only a bar), sends `MoveTo` and `Attack` commands, and hears about hits. You write four things:

1. **What clients see** — attributes on the data.
2. **Who sees what** — profiles.
3. **Who may connect, and what they control** — sessions.
4. **What they may send** — commands (and what you tell them: events).

The engine does the rest, on the worker pool, after each tick: it finds what changed, encodes each change once, gives every session the
changes around it, catches up a session that fell behind, and never lets a slow client hold up the tick.

> 🧭 **Prerequisites.** The runtime ([ch.5](05-systems.md)), and a spatial grid ([ch.2 §5](02-modeling.md#5-spatial--querying-by-geometry)):
> replication is spatial, so a replicated archetype needs a `[SpatialIndex]` position.

---

## 0. The shape of it

```
tick N    systems run ── write components ── call ctx.Subscriptions.Replicate(…)
                                             after a write clients should see
          tick fence ─── records this tick's pushes
                         (spawns, destroys, moves, your Replicate marks)
          replication ── project pushed entities (compare quantized bytes, encode once)
                         index the changes by cell
                         gather each session's frame from the cells around it
          durability ─── the tick's writes flushed
          publish ────── frames released to engine-owned send pumps
                         → TCP / WebSocket → clients
tick N+1  commands the clients sent meanwhile are drained, typed,
          into ctx.Subscriptions.Commands<T>()
```

Two things to hold on to:

- **Replication is pushed, and explicit.** The engine sends an entity when it is *told* it changed: by you, with `Replicate`, after a
  write a client should see — and by itself for spawns, destroys, `WriteSpatial` moves and migrations. A write you do not push is not
  sent. That is the contract that makes the cost follow what changed, never the size of the world.
- **What a session holds is geometry.** Nothing is kept per (client, entity). A player's session holds the entities within its radius; a
  client that falls behind is skipped, never queued, and its next frame carries the union of what it missed ([§9](#9-budgets-backpressure-and-limits) says how, in brief).

---

## 1. Declaring what clients see

A **projection** is what of an archetype travels: its position, the fields, how each is quantized, and who may see it. The quickest way to
declare it is on the data itself. Mark the archetype `[Replicated]`, its position component `[Motion]`, and the fields that travel:

```csharp
using Typhon.Protocol;   // the replication attributes and CodecKind live here

[Archetype, Replicated]                          // opt in: this archetype is sent to clients
public sealed partial class Character : Archetype<Character>
{
    public static readonly Comp<Transform> Transform = Register<Transform>();

    [Motion(ToleranceM = 0.05, TeleportMps = 20)] // the position, from Bounds' [SpatialIndex] box
    public static readonly Comp<Bounds>    Bounds    = Register<Bounds>();

    public static readonly Comp<Ham>       Ham       = Register<Ham>();
    public static readonly Comp<Faction>   Faction   = Register<Faction>();
    public static readonly Comp<Wallet>    Wallet    = Register<Wallet>();
    public static readonly Comp<Intent>    Intent    = Register<Intent>();
}

[Component("Shard.Ham", 1, StorageMode = StorageMode.SingleVersion)]
public struct Ham
{
    [Fraction(nameof(MaxHealth), Name = "hp", Group = "vitals")] // everyone: an 8-bit health bar
    [Owner(CodecKind.Varu, Name = "health")]                     // its owner: the exact number
    public int Health;

    [Owner(CodecKind.Varu, Name = "action")] public int Action;
    [Owner(CodecKind.Varu, Name = "mind")]   public int Mind;
    public int MaxHealth, MaxAction, MaxMind;
}

[Component("Shard.Faction", 1, StorageMode = StorageMode.SingleVersion)]
public struct Faction
{
    [Index(AllowMultiple = true)]
    [OnEnter(CodecKind.U8, Name = "faction")]    // sent once, when a character comes into view
    public int Value;
}
```

That is the whole projection. What each attribute means:

| Attribute | On | Sends |
|---|---|---|
| `[Replicated]` | the archetype | opts it in — without it, no attribute of its components replicates it |
| `[Motion(…)]` / `[Position]` | a `Comp<T>` whose component holds the `[SpatialIndex]` field | a mover's position as motion segments / a fixed thing's position once |
| `[Replicate(kind?)]` | a component field | the field, with every update that changes it |
| `[OnEnter(kind?)]` | a component field | the field once, in the enter record |
| `[Fraction(nameof(max))]` | a numeric field | the value as a fraction of another field (8 bits by default) |
| `[Heading]` | an angle field | the angle, only when it turned past a tolerance |
| `[Owner(kind?)]` | a component field | the field **only to the session that controls the entity** |

- **Codecs.** With no `CodecKind` a field travels under its type's default (`byte` → `U8`, `int` → `I32`, `float` → `F32`, an enum →
  packed bits wide enough for its names, `EntityId` → an entity reference). Name one to quantize: `CodecKind.Quant` with `Min`, `Max` and
  `Bits`, `F16`, `Unorm`, `Varu`… A `long` or `double` has **no** default — the generator asks you to choose, because no 64-bit value
  reaches the wire without a decision (`Saturate = true` clamps it on purpose).
- **Change groups.** A record carries only the groups that changed, so a health change does not resend a faction. Fields with no `Group`
  share the default one.
- **Motion segments** make movement cheap: a client receives a start point, a velocity and a start tick, and extrapolates. The engine sends
  a new segment only when the extrapolation would drift past `ToleranceM`, on a teleport (a jump faster than `TeleportMps`), or as a
  heartbeat.
- **Owner fields** are how a client sees its own exact state without broadcasting it — its HP, its wallet, its quest marker.

> 🔧 **The attributes are compiled, not reflected.** A source generator — shipped with the `Typhon` package — turns them into ordinary builder
> calls. It checks at compile time what it can: the class must be `partial`, a `long` needs a codec, `[Fraction]`'s maximum must exist, a
> field cannot be both `[Replicate]` and `[Owner]` (that would send the "private" value to everyone). The attributes are **not** part of the
> component's storage identity: changing a codec is never a schema migration.

### The builder, when the attributes are not enough

Everything above is a builder call underneath, and you can write the builder call yourself. It **replaces** the archetype's attributes
entirely — useful when one deployment needs a different projection, or when a component is shared by two archetypes that should send it
differently:

```csharp
subs.Archetype<Character>(a => a
    .Motion(Character.Bounds, m => m.Tolerance(0.05).Teleport(20))
    .Fraction(Character.Ham, h => h.Health, h => h.MaxHealth, bits: 8, name: "hp", group: "vitals")
    .OnEnter(Character.Faction, f => f.Value, Codec.U8, name: "faction")
    .Owner(o => o.Field(Character.Ham, h => h.Health, Codec.VarUInt, name: "health")));
```

`subs.Static<T>(a => …)` declares an archetype whose state never changes — sent once on enter, never compared again, so a world of
scenery costs nothing per tick.

---

## 2. Who sees what: profiles

A **profile** is a named kind of view. It holds one **observer** over a set of archetypes:

```csharp
var subs = runtime.Subscriptions;                  // declare everything before runtime.Start()

subs.Archetype<Character>();                        // the projection, as its attributes declare it

// Players: everything within 150 m of the character they control.
// 165 m to leave, so an entity pacing on the boundary does not flap.
subs.Profile("player", p => p
    .Sphere(150, leave: 165)
    .AroundControlled()
    .Of<Character>());

// A spectator tool: the whole world, delivered progressively.
subs.Profile("viewer", p => p
    .World()
    .Of<Character>());
```

| Observer | Holds | Centre |
|---|---|---|
| `World()` | every entity of its archetypes | — |
| `Sphere(r, leave:, max:)` | entities within `r` | `AroundControlled()`, `Bind(entity)`, `At(point)`, or `ctx.Subscriptions.Place(session, point)` every tick |
| `ClientRegion(maxEdgeM)` | entities inside a convex footprint the client sends | the client's region, capped by `.Near(budget)` |

Beside its observer a profile may declare one `Aggregate(tileM, rateHz)`: per-tile counts of each archetype, refreshed at a bounded rate —
a minimap or a far tier without a single entity record. Other refinements, all optional: `.Bands(b => b.Every(2, beyond: 0.5))` sends
far entities' updates less often; `.Every(2)` serves the whole profile one tick in two; `Sphere(…, max: 1500)` lets a system widen one
session's radius at run time with `SetRadius` (a player boarding an aircraft).

**The replication cell is required.** A session's view is delivered and swept in cubes of `SubscriptionsOptions.ReplicationCellM`. Choose
it from your radii: about a third of the largest Sphere radius is a good start, which gives each session an 11 × 11 window of cells.
`Start` refuses a runtime without it, or one whose window would be wider than 15 cells (13 in a 3D world), and logs the window it
derived.

**How the engine decides who receives what.** Every tick, the entities that changed are sorted into the replication cells they are in,
once, for everyone. Each session then reads only the cells around it and keeps just its centre, its radius and one bit per nearby cell:
has this cell been sent to me yet? An entity is held when it is within the radius and its cell has been sent. No list of what each client
holds is kept, so a session's cost follows what changed near it, never the size of the world or how much it sees. That is why 1 000
players over 270 k entities cost the demo about 2 ms a tick. The full algorithm, with diagrams, is in
[Technical overview ch.15 § 3](../in-depth-overview/15-subscriptions.md#3-the-cell-algorithm-who-receives-what).

---

## 3. Who may connect: sessions

A client opens a **session** by sending `HELLO` with a **kind** and a token. Declare the kinds you serve and decide, in an admission hook,
who gets in and with what role:

```csharp
subs.Sessions.Kinds("player", "viewer");
subs.Sessions.Admit = static (in AdmissionRequest request) =>
    request.Kind == "player"
        ? (IsValid(request.Token)
            ? Admission.Accept(SessionRole.Player)
            : Admission.Reject(4101, "bad token"))
        : Admission.Accept(SessionRole.Spectator);
```

The hook runs on the connection's thread, before the session exists — keep it quick and thread-safe. A `Spectator` may watch; a `Player`
may control an entity and send player commands. `Accept` can also carry per-session `SessionLimits` and application data; `Reject` takes a
close code in the application range 4100–4999 (an undeclared kind is refused by the engine itself, with 4003).

**A session receives nothing until it is bound to a profile.** Admitted sessions appear to systems as `SessionEvents` in the next tick;
the `Opened` event is where you bind them — and, for a player, spawn its character and hand over **control**:

```csharp
internal sealed class SessionSystem : CallbackSystem
{
    // session → the character it controls, shared with CommandSystem
    private readonly Dictionary<SessionId, EntityId> _characters;
    public SessionSystem(Dictionary<SessionId, EntityId> characters) => _characters = characters;

    protected override void Configure(SystemBuilder b) => b
        .Name("Sessions")
        .Phase(Phase.Input)
        .Writes<Transform>().Writes<Bounds>().Writes<Ham>()
        .Writes<Faction>().Writes<Wallet>().Writes<Intent>();

    protected override void Execute(TickContext ctx)
    {
        var subs = ctx.Subscriptions;
        foreach (ref readonly var e in subs.SessionEvents)
        {
            switch (e.Kind)
            {
                case SessionEventKind.Opened when e.SessionKind == "player":
                    var character = SpawnCharacter(ctx.Transaction); // ch.5's spawn → EntityId
                    _characters[e.Session] = character;
                    subs.Session(e.Session).Profile("player").Control(character);
                    break;

                case SessionEventKind.Opened:
                    subs.Session(e.Session).Profile("viewer");
                    break;

                case SessionEventKind.Closed when _characters.Remove(e.Session, out var gone):
                    ctx.Transaction.Destroy(gone); // or keep it, re-bind it when the player returns
                    break;
            }
        }
    }
}
```

- **Requests are staged.** `Session(id).Profile(…)`, `.Control(…)`, `.SetBudget(bytesPerSecond)` and `.Kick(code, reason)` are applied
  at the start of the next tick, so any system on any worker may make them. `Place(session, point)` is the exception: it applies now,
  because it is this tick's viewpoint.
- **Control** is what `AroundControlled()` centres on, and what owner fields and `SELF` are about: the controlling session receives the
  entity's owner fields and the sequence number of its last applied command in every frame.
- **Handle `Closed`.** A session that opens and closes inside one tick produces both events, in order. Whatever you built on `Opened`,
  undo on `Closed`.

---

## 4. Telling the engine what changed

Spawns, destroys, migrations and `WriteSpatial` moves are pushed for you. A **content** write a client should see is yours to push, right
after it:

```csharp
// In a system that walks clusters (ch.5 §5):
foreach (var cluster in clusters)
{
    var ham = cluster.GetSpan(Character.Ham);
    var occupied = cluster.OccupancyBits;
    while (occupied != 0)
    {
        var slot = BitOperations.TrailingZeroCount(occupied);
        occupied &= occupied - 1;
        if (ham[slot].Health < ham[slot].MaxHealth)
        {
            ham[slot].Health++;
            ctx.Subscriptions.Replicate(in cluster, slot);     // one atomic OR; duplicates are free
        }
    }
}

// Or for an entity reached by id — a command's target:
var target = ctx.Transaction.OpenMut(id);
target.Write(Character.Ham).Health -= damage;
ctx.Subscriptions.Replicate(in target);
```

`Replicate` is a mark, not a send: after the fence the entity is re-encoded and compared with what was last sent, so a push that changed
nothing visible costs an encode and no bytes. A slot mask form (`Replicate(in cluster, ulong slots)`) marks several at once.

> ⚠️ **A write you do not push is not sent.** The client keeps the old value until the entity changes again. During development, set the
> environment variable `TYPHON_PUSH_VALIDATE=N`: every tick the engine projects N whole clusters per archetype, counts every changed entity
> you did not push — and sends it, so the validator heals what it finds while it reports it.

---

## 5. Commands: what clients may send

A **command** is a typed intent: "move there", "attack that". Declare its type in a small **contracts** assembly that references only
`Typhon.Protocol` — .NET clients and bots then share the exact struct:

```csharp
// Shard.Contracts — references Typhon.Protocol only.
[ReplicatedMessage]
public partial struct MoveTo
{
    [Quant(0, 1000, 16)] public float X;
    [Quant(0, 1000, 16)] public float Y;
}

[ReplicatedMessage]
public partial struct Attack
{
    [EntityRef] public uint Target;       // the netId the client was sent, not a server EntityId
}
```

The **policy** is declared on the server:

```csharp
subs.Command<MoveTo>(c => c
    .Roles(SessionRole.Player)
    .Coalesce(CommandCoalesce.LatestPerSession)   // only a session's newest move per tick survives
    .Rate(10, burst: 20));

subs.Command<Attack>(c => c
    .Roles(SessionRole.Player)
    .Rate(4, burst: 4));
```

and every command a client sends is checked **before any tick sees it**: against the wire, the session's inbound byte budget, the type's
rate, the session's role and an optional stateless `.Precheck(…)`. What passes is drained into the next tick, per session, in order:

```csharp
internal sealed class CommandSystem : CallbackSystem
{
    private readonly Dictionary<SessionId, EntityId> _characters; // the map SessionSystem fills
    public CommandSystem(Dictionary<SessionId, EntityId> characters) => _characters = characters;

    // …Configure: Phase.Input, After("Sessions"), Writes<Intent>().Writes<Ham>()

    protected override void Execute(TickContext ctx)
    {
        var subs = ctx.Subscriptions;

        foreach (ref readonly var move in subs.Commands<MoveTo>())
        {
            if (!_characters.TryGetValue(move.Session, out var me)) continue;
            ctx.Transaction.OpenMut(me).Write(Character.Intent).Target =
                new Point2F { X = move.Value.X, Y = move.Value.Y };
        }

        foreach (ref readonly var attack in subs.Commands<Attack>())
        {
            // Only an entity this session was actually shown resolves:
            // a client cannot target what it never saw.
            if (!subs.TryResolve(attack.Session, attack.Value.Target, out var target)
                || !InRange(attack.Session, target))
            {
                subs.Reject(attack, AckReasons.Rejected);
                continue;
            }

            var victim = ctx.Transaction.OpenMut(target);
            victim.Write(Character.Ham).Health -= 10;
            subs.Replicate(in victim);
        }
    }
}
```

- **Semantic validation is yours** — range, cooldowns, ownership — because the state it must agree with is yours. The engine's checks
  are the ones that need no world state.
- **Every refused command is answered.** A command refused by rate or budget, by role, by the pre-check or by your `Reject` comes back to
  its client as an `ACK` with a reason in the session's next frame, and the frame's `SELF.lastSeq` tells it which commands the tick
  drained. A predicting client settles every command it sent.
- **The inbound budget is required** when you declare commands: `SubscriptionsOptions.IngressBytesPerSecond`, each session's bytes per
  second, at least `ClientMessageBytes`. Size it from your commands' size and rate — a player sending 20 small commands a second needs a
  few KiB/s. A client that keeps sending what is refused is closed with `1008`.

---

## 6. Events: telling clients what happened

State says what *is*; an **event** says what *happened* — a hit, an explosion, a notice. Declare how it is routed, then emit it from any
system:

```csharp
[ReplicatedMessage]
public partial struct Hit
{
    public EntityId Attacker;              // travels as the netId each client knows
    public EntityId Target;
    [Codec(CodecKind.U16)] public int Damage;
}

// Routed to the sessions that hold either one:
subs.Event<Hit>(e => e.RouteToKnown(h => h.Target, h => h.Attacker));

// in CommandSystem, after applying an attack:
subs.Emit(new Hit { Attacker = _characters[attack.Session], Target = target, Damage = 10 });
```

Routes: `RouteNear(point, radius)`, `RouteToKnown(entities)`, `RouteToOwner(entity)`, `RouteToSession()` with `EmitTo(session, …)`, and
`Broadcast()`. An event is encoded once and the same bytes reach every session it matches.

> 💡 **Events are best effort.** A session more than eight ticks behind loses the older ones, and is told how many (`EventsLost`). Anything that must never be lost is
> state: a death is a mode change the client learns from its next record, the hit is the flourish.

---

## 7. Opening the doors: transports

Replication needs a way in. Start the runtime, then attach one or both transports.

**WebSocket, through ASP.NET Core** (`Typhon.Subscriptions.AspNetCore`) — for browsers:

```csharp
runtime.Start();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(runtime);
// An empty origin list refuses to start: be explicit.
builder.Services.AddTyphonSubscriptions(o => o.AllowOrigin("https://play.example.com"));

var app = builder.Build();
app.UseWebSockets();
app.MapTyphonSubscriptions("/ws");             // the replication endpoint (subprotocol typhon.3)
app.MapTyphonCatalog("/typhon/catalog.json");  // the catalog, for code generation and tools
await app.RunAsync();
```

**TCP, built into the engine** — for native clients, bots, server-to-server links, and anything that does not host HTTP:

```csharp
runtime.Start();
// Binds loopback unless you set Address.
var tcp = new TcpSubscriptionTransport(new TcpSubscriptionOptions { Port = 9100 });
runtime.StartSubscriptionTransport(tcp);
// … on shutdown:
await tcp.StopAsync();
runtime.Shutdown();
```

Neither transport authenticates anyone: the `HELLO` token reaches your admission hook unexamined, and that is where you check it. Put TLS
in front of the WebSocket endpoint (ASP.NET Core does it), or set `ServerCertificate` on the TCP options.

---

## 8. Clients

Clients never see your C# types. At connection they receive the **catalog** — every archetype, field, codec, command and event, in
canonical JSON — and decode against it. Two SDKs ship with the engine:

- **TypeScript** (`@typhondb/client`, `src/Typhon.Client.TypeScript`) — for browsers. `ReconnectingClient` handles the handshake,
  catalog caching and reconnection; `FrameApplier` applies each frame into a `WorldStore` of per-archetype columns; `Clock` and
  `evaluateLive` extrapolate motion segments to render time; `CommandQueue` batches commands per frame. `npx typhon-codegen` compiles a
  specialised decoder from `/typhon/catalog.json` for the hot path.
- **.NET** (`Typhon.Client`) — for bots, tools and tests. `TyphonClient` connects over `ws://`, `wss://` or `tcp://`, keeps a
  `WorldStore`, and sends commands by catalog name:

```csharp
await using var client = new TyphonClient(new ClientOptions
{
    Endpoint = new Uri("ws://localhost:5000/ws"),
    Kind = "player",
    Token = token,
});
await client.ConnectAsync();
await client.SendCommandAsync("MoveTo", new RecordValues
{
    ["X"] = FieldValue.Of(420.0),
    ["Y"] = FieldValue.Of(310.0),
});
```

The SWG Tatooine demo (`demo/SwgTatooine`, `demo/SwgTatooine.Client`, `demo/SwgTatooine.Bots`) is a complete, measured example of all of
the above: 270 k entities, 1 000 player sessions at 50 Hz, a browser client, and bot swarms. Serving those 1 000 sessions costs the
replication track **about 2 ms per tick** (1.5–1.95 ms measured) on a Ryzen 9 7950X (16 cores): a tenth of the 20 ms tick budget.

---

## 9. Budgets, backpressure and limits

The defaults serve a few thousand sessions; the knobs are on `RuntimeOptions.Subscriptions`:

```csharp
new RuntimeOptions
{
    Subscriptions = new SubscriptionsOptions
    {
        ReplicationCellM = 50,              // required
        IngressBytesPerSecond = 8 * 1024,   // required once commands are declared
        MaxSessions = 2048,
        EnterBudgetPerFrame = 500,          // a new view fills cell by cell, nearest first
        CloseStalledAfter = TimeSpan.FromSeconds(3),
    },
}
```

- **A slow client is skipped, never queued.** Its next frame carries everything it missed (replayed from an 8-tick log, or a reset past
  it). A client that stays behind is served every other tick, then every fourth, then closed with `1013`. A client that stops
  acknowledging frames is closed with `4001`.
- **A per-session outbound budget** (`Session(id).SetBudget(bytesPerSecond)`) lowers that session's detail level — less frequent far
  updates, a smaller enter budget — before it ever drops state.
- **Memory is bounded and pooled**: ≈ 96 B of replication state per entity of an observed archetype, a few hundred bytes per session plus
  its frames, all native, all under configured budgets. Steady-state replication allocates no managed memory.

What is **not built yet** (refused at `Start` rather than silently ignored): several entity observers in one profile, per-session observers
(`Observe`), shared View sources, resumable sessions, and reliable events. See the [feature catalog](xref:feature-subscriptions-index) for
the full status.

---

## 🧭 What's next

- **[Chapter 6 — Operating & going deeper](06-operating.md)** covers observing a running server; replication reports its per-stage cost
  in the runtime's telemetry and in each session's optional `STATS` block.
- **Look it up:** the [Subscriptions feature catalog](xref:feature-subscriptions-index) — one page per capability, with guarantees and limits.
- **Go deep:** [Technical overview 15 — Subscriptions](xref:overview-subscriptions) — the pipeline, the per-entity state, the frame path.

## 🧩 Key concepts & types

**Concepts:** [Subscription](../key-concepts/subscription.md) · [Projection](../key-concepts/projection.md) ·
[Replication profile](../key-concepts/replication-profile.md) · [Replication session](../key-concepts/replication-session.md) ·
[Client command](../key-concepts/client-command.md) · [Replication event](../key-concepts/replication-event.md) ·
[Replication catalog](../key-concepts/replication-catalog.md).

**Exact calls:** `TyphonRuntime.Subscriptions` (`SubscriptionsRegistry`: `Archetype<T>()` / `Archetype<T>(…)` / `Static<T>(…)` / `Profile` /
`Command<T>` / `Event<T>` / `Sessions.Kinds` / `Sessions.Admit`) · `[Replicated]` / `[Motion]` / `[Position]` / `[Replicate]` / `[OnEnter]` /
`[Owner]` / `[Fraction]` / `[Heading]` / `[ReplicatedMessage]` / `[Codec]` / `[Quant]` / `[EntityRef]` · `ProfileBuilder` (`World` / `Sphere` /
`ClientRegion` / `Aggregate` / `Every`) · `ObserverBuilder` (`Of` / `AroundControlled` / `Bind` / `At` / `Near` / `Bands`) · `ctx.Subscriptions`
(`SessionEvents` / `Session(…).Profile/Control/SetBudget/Kick` / `Place` / `SetRadius` / `Replicate` / `Commands<T>` / `TryResolve` / `Reject` /
`Emit` / `EmitTo`) · `SubscriptionsOptions` (`ReplicationCellM` / `IngressBytesPerSecond` / `MaxSessions` / `EnterBudgetPerFrame` /
`CloseStalledAfter`) · `TcpSubscriptionTransport` / `runtime.StartSubscriptionTransport` · `AddTyphonSubscriptions` / `MapTyphonSubscriptions` /
`MapTyphonCatalog`.
