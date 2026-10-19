# Typhon.Client

**The .NET client SDK for [Typhon](https://typhondb.io) Subscriptions.**

Connects to a Typhon server, receives its replicated world into a columnar store, and sends commands back. Everything is decoded from the
catalog the server sends at connection — not from C# layouts — so a client keeps working while the server's schema evolves.

> ⚠️ **Alpha.** APIs and the wire format may still change between releases.

## What it does

- **Connect** over TCP or WebSocket, with reconnection and session resume.
- **Store**: per-archetype columns of the entities the server says this session holds; enters, updates and leaves applied per frame.
- **Motion**: positions extrapolated from the server's motion segments between frames.
- **Commands**: typed, rate-paced, acknowledged; `SubscribeAsync` / `UnsubscribeAsync` for the server's explicit group sets.
- **Groups and events**: `IsGroupComplete(set, key)`, and the frame's events, best effort or reliable.

Targets `net10.0` and `netstandard2.1` (Unity 2021.2+; tested on CoreCLR, a Mono/IL2CPP run not yet proven). `FrameApplied` is raised on the
receive thread: copy what a frame changed to your game thread there.

## Learn more

- [Client SDKs](https://doc.typhondb.io/latest/guides/feature-catalog/Subscriptions/client-sdks.html) — this SDK, the TypeScript one and the C one
- [Subscriptions guide](https://doc.typhondb.io/latest/guides/07-subscriptions.html)
- Packages: [`Typhon.Protocol`](https://www.nuget.org/packages/Typhon.Protocol) (the wire, and the attributes for a shared contracts assembly) ·
  [`Typhon`](https://www.nuget.org/packages/Typhon) (the engine, the server side)
