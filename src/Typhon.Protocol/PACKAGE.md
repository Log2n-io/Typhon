# Typhon.Protocol

**The wire protocol of [Typhon](https://typhondb.io) Subscriptions, and the attributes that declare what replicates.**

Typhon Subscriptions replicate engine state to remote clients — game clients, browsers, bots — and drain their typed commands back into the
server's tick. This package holds what a server and its clients must agree on, and nothing else: the `typhon.3` framing and codecs, the
catalog model, and the replication attributes with their source generator.

> ⚠️ **Alpha.** APIs and the wire format may still change between releases.

## When to reference it

- **A contracts assembly** shared by a server, its .NET clients and its bots: commands and events as `[ReplicatedMessage]` structs. It
  references only this package — no engine.
- You rarely reference it directly otherwise: the `Typhon` engine package and the `Typhon.Client` SDK both depend on it.

```csharp
using Typhon.Protocol;

[ReplicatedMessage]
public partial struct Attack
{
    [EntityRef] public uint Target;                  // the netId the client knows
    [Codec(CodecKind.U16)] public int Damage;
    [Quant(-8192, 8192, 24)] public double X;        // quantized on the wire
}
```

The bundled generator implements `IReplicatedMessage` for every `[ReplicatedMessage]` type, so the server and the client describe the
same fields in the same order.

Targets `net10.0` and `netstandard2.1`: a contracts assembly shared with a Unity client targets `netstandard2.1` and is built with the .NET SDK
(the generated code is C# 8).

## Learn more

- [Subscriptions guide](https://doc.typhondb.io/latest/guides/07-subscriptions.html)
- [Wire protocol & catalog](https://doc.typhondb.io/latest/guides/feature-catalog/Subscriptions/wire-protocol.html)
- Packages: [`Typhon`](https://www.nuget.org/packages/Typhon) (the engine, the server side) ·
  [`Typhon.Client`](https://www.nuget.org/packages/Typhon.Client) (the .NET client) ·
  [`Typhon.Subscriptions.AspNetCore`](https://www.nuget.org/packages/Typhon.Subscriptions.AspNetCore) (WebSocket hosting)
