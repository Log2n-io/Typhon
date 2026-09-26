---
uid: feature-subscriptions-wire-protocol
title: 'Wire Protocol & Catalog'
description: 'typhon.3: a handshake that carries a canonical, self-describing catalog; tick frames of typed blocks; quantized codecs with bit-exact arithmetic; and close codes an SDK acts on.'
---

# Wire Protocol & Catalog
> A compact binary stream a client decodes from a catalog it is sent, not from the server's types.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Replication bandwidth is the product's ceiling, and clients live in other languages and ship on other schedules. The wire must be dense,
self-describing, versioned independently of the server's code, and exact across languages.

## ⚙️ How it works (in brief)

**Messages** — one WebSocket binary message, or `u32 len | message` on TCP after a `TYP3` preamble; the first byte is the type:

| Message | Direction | Carries |
|---|---|---|
| `HELLO` | client → server | version, capabilities, kind, token (≤ 8 KiB), known catalog hash, payload (≤ 256 B) — within 5 s |
| `WELCOME` | server → client | granted capabilities, session id, tick, tick period, catalog hash, the catalog (omitted when the client already has it) |
| `TICK` | server → client | tick number, flags (`VIEW_COMPLETE`, `RESET`, `OVERLOAD`, `PERIOD`), blocks |
| `COMMANDS` | client → server | client tick, then typed commands with a `u16` sequence each |
| `PING` / `PONG` | both | client clock and last applied tick (mandatory, 4 Hz) / server tick |
| `KICK` / `BYE` | server → client / client → server | a close code and reason / a clean leave |

**Blocks** inside a `TICK`, each length-prefixed so an unknown one is skipped: `ENTITIES` (one archetype's enters, updates, leaves),
`EVENTS`, `SELF`, `AGG`, `STATS`, `DEBUG`, `ACKS`, `EXT`.

**The catalog** is canonical JSON — archetypes with their position and fields (codec, group, section), commands with their rate and
roles, events, metrics, limits, the tick rate — sorted so the same declarations in any order produce the same bytes and the same hash.
Built-in commands, events and metrics sit at reserved indices, so enabling one never moves an application index.

**Codecs** — integers, `varu`/`vari`, `f16`/`f32`, quantizers over a range, normalized values, angles, packed bits (≤ 24 in a section's
pack), positions (24 bits per axis over the world bounds), velocities (displacement per tick in 1/16 position quanta), entity references
(netIds), text, blobs, lists, quaternions, tick offsets. The arithmetic is specified exactly and implemented identically in C# and
TypeScript.

**Close codes**:

| Code | Meaning | SDK reconnects |
|---|---|---|
| 1000 / 1001 | normal / server going away | app decides / yes |
| 1002 / 1007 / 1009 | protocol error / malformed payload / message too big | no |
| 1008 | policy violation: sustained refused commands | yes, backoff |
| 1011 | internal error (a flush failure closes every session) | yes |
| 1013 | try later: server full, or lagging | yes, backoff |
| 4001 / 4002 / 4003 | no acknowledgement / `HELLO` timeout / admission rejected | yes / yes / no |
| 4100–4999 | application (`Kick`, `Admission.Reject`) | app-declared |

## ⚠️ Guarantees & limits

- **Versioned**: `typhon.3` + a minor + capability bits; a client ignores unknown blocks and unknown fixed-size fields.
- **No 64-bit integer on the wire** implicitly; strings and blobs are length-capped by the catalog.
- **Records are absolute**: a frame never depends on a frame the client might have missed.
- `resumeToken` is always 0 until resume is built.

## 🧪 Tests

- [GoldenTickTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Protocol.Tests/GoldenTickTests.cs) · [GoldenRefusalTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Protocol.Tests/GoldenRefusalTests.cs) — committed golden vectors, shared with the TypeScript SDK
- [CatalogBuilderTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/CatalogBuilderTests.cs) — the emitted catalog is valid and canonical

## 🔗 Related

- Concept: [Replication catalog](xref:concept-replication-catalog)
- [Client SDKs](client-sdks.md)
