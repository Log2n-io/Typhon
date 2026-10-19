---
uid: feature-subscriptions-client-sdks
title: 'Client SDKs'
description: 'The TypeScript SDK for browsers, the .NET client for bots, tools and tests, and the C SDK for native clients and FFI bindings: catalog-driven decoding into columnar stores, motion extrapolation, commands, reconnection, recording, and ahead-of-time code generation. Published on npm, NuGet and GitHub Releases.'
---

# Client SDKs
> Three clients, one catalog: the browser's, .NET's and native code's, all decoding against what the server says rather than its C# types.

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
- `GroupSubscriptions` — `subscribe(set, key)` / `unsubscribe(set, key)` for an explicit group set, each a promise of its answer: `pump(queue)`
  before each flush, `observe(applier)` after each frame, `reset()` on close ([groups](./groups.md)).
- `StreamRecorder` / `replayStream` — record and replay a session.
- `typhon-codegen` — compiles a specialised decoder from `/typhon/catalog.json` for the hot path.

**.NET** (`Typhon.Client`):

- `TyphonClient` over `ws://`, `wss://` or `tcp://`: connect, reconnect policy, `SendCommandAsync(name, values)`, a `WorldStore` with
  per-archetype columns and `SelfState`, a `Recorder`. Built for bots, tools, load tests and integration tests.
- `SubscribeAsync(set, key)` / `UnsubscribeAsync(set, key)` — a key of an explicit group set, settling with the server's answer
  (`SubscribeResult`); paced by the command's rate, sent again when the server still refuses it for the rate, dropped when the session ends.
- `FrameApplied` — raised on the receive thread after each `TICK` is applied whole (events and leaves included) and before the next: the window
  to copy the frame's change lists and columns out to a game or render thread, since the next frame clears and rewrites them.

**C** (`src/Typhon.Client.Native`, C++20 inside, a C ABI outside, no third-party dependency):

- `typhon_client_create` / `_start` / `_poll` / `_stop` over TCP — a columnar store read through `typhon_archetype_view_get` and
  `typhon_field_column`, motion through `typhon_motion_evaluate_live`, commands through `typhon_command_enqueue` / `typhon_commands_flush`,
  groups through `typhon_group_subscribe` and `typhon_groups_get`, events through `typhon_event_*`. Caller-owned buffers and opaque handles, so
  any language with an FFI binds the one header (`typhon/client.h`). TLS and WebSocket are not built (#1045).

## 📦 Install

| SDK | Where | |
|---|---|---|
| .NET | NuGet | `dotnet add package Typhon.Client --prerelease` — and `Typhon.Protocol` alone for a contracts assembly shared with the server |
| TypeScript | npm | `npm install @typhondb/client@alpha` (ESM, Node ≥ 22 or a browser) |
| C | [GitHub Releases](https://github.com/Log2n-io/Typhon/releases) | `typhon-client-native-<version>-<platform>` for linux-x64, windows-x64, macos-arm64: headers, a static and a shared library, and a CMake package — `find_package(TyphonClient)`, then link `Typhon::typhon_client` (static) or `Typhon::typhon_client_shared` |

Every package carries the server's version (one version for the engine and its clients); the alpha line is prerelease-only.

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

- **Golden vectors** pin the wire: the engine, the .NET client, the TypeScript SDK and the C SDK decode the committed vectors identically, bit for bit.
- **Forward compatible**: a client skips blocks and fixed-size fields it does not know (AC-14).
- **Steady state**: the TypeScript SDK shows no retained heap growth over a 10-minute replay; with code generation a steady-state frame of
  10 k records decodes in ≈ 0.3 ms on the reference laptop.
- **Packaging**: each release's smoke tests install the packages from the release itself — a server on `Typhon` + `Typhon.Subscriptions.AspNetCore`,
  the .NET client over TCP, the TypeScript tarball over WebSocket, and the C package through `find_package`, static and shared.
- **Targets**: `Typhon.Protocol` and `Typhon.Client` target `net10.0` and `netstandard2.1` — Unity 2021.2 and later. The `netstandard2.1` builds
  run the same tests, every golden vector among them, on CoreCLR; a run on Mono or IL2CPP is not yet proven, and WebGL has no sockets. A
  contracts assembly for Unity targets `netstandard2.1` and is built with the .NET SDK (its generator's output is C# 8); `ClientOptions` is
  settable, since Unity's compiler has no `init` setters. The engine and `Typhon.Subscriptions.AspNetCore` are `net10.0` only.

## 🧪 Tests

- `src/Typhon.Client.TypeScript/test` — vitest suites: framing, apply, motion, reconnect, codegen, golden streams
- [EngineStreamGoldenTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Client.Tests/EngineStreamGoldenTests.cs) — the .NET client against the engine's golden stream

## 🔗 Related

- [Wire protocol & catalog](wire-protocol.md) · [Transports & hosting](transports-hosting.md)
- Concept: [Replication catalog](xref:concept-replication-catalog)
