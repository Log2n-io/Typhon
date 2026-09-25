---
uid: feature-subscriptions-client-sdks
title: 'Client SDKs'
description: 'The TypeScript SDK for browsers and the .NET client for bots, tools and tests: catalog-driven decoding into columnar stores, motion extrapolation, commands, reconnection, recording, and ahead-of-time code generation.'
---

# Client SDKs
> Two clients, one catalog: the browser's and .NET's, both decoding against what the server says rather than its C# types.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A client has to decode a compact, quantized, versioned stream, keep a mirror of what its session holds, draw movers smoothly between frames,
and send commands in the server's encoding — without being regenerated every time the server's types change.

## ⚙️ How it works (in brief)

**TypeScript** (`@typhondb/client`, `src/Typhon.Client.TypeScript`, ESM + `.d.ts`):

- `ReconnectingClient` / `Connection` — the handshake, catalog caching by hash, `PING` scheduling (`PingScheduler`), reconnection with
  backoff per close code.
- `FrameApplier` — applies each `TICK` into a `WorldStore`: per-archetype `ArchetypeStore`s of typed columns (`field(name)`), live slots,
  and per-frame entered/updated/left lists; `SelfState`, acks, events and `AggregateGrid`s beside them.
- `Clock` + `evaluateLive(store, renderTick, frac, out)` — render time and motion-segment extrapolation, including tick-period changes under
  overload.
- `CommandQueue` (one batch per rendered frame) and `RegionSender` (`ClientRegion` footprints, ≤ 5 Hz).
- `StreamRecorder` / `replayStream` — record and replay a session.
- `typhon-codegen` — compiles a specialised decoder from `/typhon/catalog.json` for the hot path.

**.NET** (`Typhon.Client`):

- `TyphonClient` over `ws://`, `wss://` or `tcp://`: connect, reconnect policy, `SendCommandAsync(name, values)`, a `WorldStore` with
  per-archetype columns and `SelfState`, a `Recorder`. Built for bots, tools, load tests and integration tests.

## 💻 Usage

```typescript
const client = new ReconnectingClient({
  url: 'wss://play.example.com/ws', kind: 'player', token, caps: Capabilities.Stats,
  handlers: {
    onWelcome: (session) => {
      applier = new FrameApplier(session.plan, { clock });
      queue = new CommandQueue({ plan: session.plan });
    },
    onTick: (message, recvMs) => applier.apply(message, recvMs),
  },
});
```

```csharp
await using var bot = new TyphonClient(new ClientOptions
{
    Endpoint = new Uri("tcp://127.0.0.1:9100"),
    Kind = "player",
});
await bot.ConnectAsync();
await bot.SendCommandAsync("MoveTo", new RecordValues
{
    ["X"] = FieldValue.Of(10.0),
    ["Z"] = FieldValue.Of(20.0),
});
```

## ⚠️ Guarantees & limits

- **Golden vectors** pin the wire: the engine, the .NET client and the TypeScript SDK decode the committed vectors identically, bit for bit.
- **Forward compatible**: a client skips blocks and fixed-size fields it does not know (AC-14).
- **Steady state**: the TypeScript SDK shows no retained heap growth over a 10-minute replay; with code generation a steady-state frame of
  10 k records decodes in ≈ 0.3 ms on the reference laptop.
- **Packaging**: neither SDK is published to a package registry yet (NuGet / npm); use them from the repository.

## 🧪 Tests

- `src/Typhon.Client.TypeScript/test` — vitest suites: framing, apply, motion, reconnect, codegen, golden streams
- [EngineStreamGoldenTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Client.Tests/EngineStreamGoldenTests.cs) — the .NET client against the engine's golden stream

## 🔗 Related

- [Wire protocol & catalog](wire-protocol.md) · [Transports & hosting](transports-hosting.md)
- Concept: [Replication catalog](xref:concept-replication-catalog)
