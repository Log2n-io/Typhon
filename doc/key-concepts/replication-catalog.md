---
uid: concept-replication-catalog
title: 'Replication catalog'
description: 'The self-describing contract a server sends each client at connection — archetypes, fields, codecs, commands, events, metrics and limits — so clients decode without the server''s C# types.'
---

# Replication catalog

> **In one line:** the contract a server sends every client at connection — what each archetype, command, event and metric looks like on
> the wire — so a client decodes frames without knowing the server's C# types.

At `Start`, the engine compiles every [projection](xref:concept-projection), [command](xref:concept-client-command),
[event](xref:concept-replication-event) and metric into one **canonical JSON document**: names sorted, indices assigned, codecs spelled
out, the session limits and the tick rate included. It travels in the `WELCOME` message; a client that presents the catalog's hash in its
`HELLO` skips the download on reconnect. `MapTyphonCatalog("/typhon/catalog.json")` serves the same bytes over HTTP for build-time code
generation.

**Canonical means declaration-order-independent.** Two servers that declare the same things in a different order produce the same catalog
and the same hash; a renamed C# type with the same wire name is not a wire break. The catalog is also the single source for both SDKs: the
.NET client and the TypeScript SDK build their decode plans from it at run time, and the TypeScript code generator can compile a
specialised decoder from it ahead of time.

**Codecs are part of the contract.** A field's codec — integer widths, `varu`/`vari`, half floats, quantizers over a range, normalized
values, angles, packed bits, positions, velocities, entity references, text and blobs — is spelled in the catalog, and the same quantization
arithmetic is implemented bit-exactly in the engine, the .NET client and the TypeScript SDK, pinned by shared golden vectors.

## How it relates

- **[Projection](xref:concept-projection)** — each projection becomes a catalog archetype.
- **[Replication session](xref:concept-replication-session)** — a session negotiates against the catalog in its handshake.

## In the API

- [`TyphonRuntime.SubscriptionsCatalogJson`](xref:Typhon.Engine.TyphonRuntime.SubscriptionsCatalogJson) — the bytes `WELCOME` carries.
- [`Typhon.Protocol.CatalogSerializer`](xref:Typhon.Protocol.CatalogSerializer) — canonicalization and the hash.
- [`CodecKind`](xref:Typhon.Protocol.CodecKind) — the codec vocabulary.

## Learn & use

- **Feature detail:** [Wire protocol & catalog](xref:feature-subscriptions-wire-protocol) · [Client SDKs](xref:feature-subscriptions-client-sdks)
