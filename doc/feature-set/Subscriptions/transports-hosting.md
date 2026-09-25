---
uid: feature-subscriptions-transports
title: 'Transports & Hosting'
description: 'The engine''s own TCP listener for native clients and bots, the ASP.NET Core WebSocket adapter for browsers, and the catalog endpoint — thin links fed by engine-owned send pumps.'
---

# Transports & Hosting
> Two doors into the same sessions: TCP in the engine, WebSocket through ASP.NET Core.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Browsers need WebSocket, TLS and origin checks; native clients, bots and server links want a plain socket with no HTTP stack. The engine
must not depend on ASP.NET Core, and no transport may block or be called by the tick.

## ⚙️ How it works (in brief)

- **Links are thin.** A transport accepts connections and moves bytes; handshake, admission, caps and close codes are the engine's, behind
  `ISubscriptionAcceptor`. **Send pumps** owned by the engine walk only the sessions that produced a frame, hand it to the session's link
  (at most one send in flight), and chain the next; the tick never awaits a send.
- **TCP** (`TcpSubscriptionTransport`, in the engine): a `TYP2` preamble, `u32` length framing, optional TLS
  (`ServerCertificate`), `TCP_NOTSENT_LOWAT` where the OS has it. Binds **loopback** unless `Address` is set.
- **WebSocket** (`Typhon.Subscriptions.AspNetCore`): the `typhon.2` subprotocol, an allowed-origin list (empty **refuses to start**;
  `AllowAnyOrigin()` is explicit), explicit keep-alive, a bounded receive loop. No `permessage-deflate`: quantized payloads barely compress
  and per-connection zlib defeats encode-once.
- **Catalog endpoint**: `MapTyphonCatalog` serves exactly the bytes `WELCOME` carries, for code generation and tools.

## 💻 Usage

```csharp
// TCP — after runtime.Start():
var tcp = new TcpSubscriptionTransport(new TcpSubscriptionOptions { Port = 9100 });
runtime.StartSubscriptionTransport(tcp);
// … before runtime.Shutdown():
await tcp.StopAsync();

// WebSocket — ASP.NET Core:
builder.Services.AddSingleton(runtime);
builder.Services.AddTyphonSubscriptions(o => o.AllowOrigin("https://play.example.com"));
app.UseWebSockets();
app.MapTyphonSubscriptions("/ws");
app.MapTyphonCatalog("/typhon/catalog.json");
```

| Option | Default | Effect |
|---|---|---|
| `TcpSubscriptionOptions.Address` / `Port` | loopback / required | Where TCP listens (`Port = 0`: ephemeral, read `BoundEndPoint`) |
| `TcpSubscriptionOptions.ServerCertificate` | none | TLS on TCP |
| `TyphonSubscriptionsOptions.AllowedOrigins` | empty (refuses) | Browser origins allowed |
| `TyphonSubscriptionsOptions.KeepAliveInterval` / `KeepAliveTimeout` | 15 s / 15 s | WebSocket ping frames (the framework's defaults are 2 min / infinite) |

## ⚠️ Guarantees & limits

- `Typhon.Engine` references no ASP.NET Core assembly (AC-19).
- **Neither transport authenticates**: the `HELLO` token reaches the admission hook unexamined.
- The WebSocket adapter does not cap kernel send buffering (`TCP_NOTSENT_LOWAT` on the upgraded socket), so the acknowledgement-based lag
  skip is what bounds a slow browser. **WebTransport is not built.**
- A transport can be written against `ISubscriptionTransport` / `ISubscriptionLink` and started with `StartSubscriptionTransport`.

## 🧪 Tests

- [TcpTransportTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/TcpTransportTests.cs) — framing, preamble, random framing never stops the listener
- [LiveClientTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/LiveClientTests.cs) — a real client over TCP end to end

## 🔗 Related

- [Wire protocol & catalog](wire-protocol.md) · [Client SDKs](client-sdks.md)
