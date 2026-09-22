---
uid: feature-subscriptions-index
title: 'Subscriptions'
description: 'Engine-owned replication: the engine streams declared archetype state to remote clients and drains their typed commands back into the tick. Under construction.'
---

# Subscriptions

> Engine-owned replication. An application declares what each archetype exposes and who sees what; the engine does the rest — per-client interest, change detection, quantized encoding, sessions, backpressure and typed inbound commands — in parallel on the worker pool, to native and browser clients.

> 🚧 **Under construction — no public API yet.** The foundation is in the engine and covered by tests, but nothing here is reachable from application code: there is no session, no transport and no registration surface. Track [#205](https://github.com/Log2n-io/Typhon/issues/205).

## What the design commits to

| Guarantee | Meaning |
|---|---|
| Cost follows the watched set | Per-entity work happens once per tick, for the entities some client can see — never proportional to an archetype's size. A database can hold a billion entities of a type; replication costs what clients are looking at |
| Correct on x64 **and** arm64 | Every cross-thread publication is a named release/acquire pair, not an assumption about store ordering |
| Zero steady-state allocation | Native pools, no managed allocation once running |
| No silent divergence | There is no mode in which a client's view of the world drifts from the server's without saying so |

## What exists in the engine today

| Piece | What it does |
|---|---|
| Replication state blocks | Native per-cluster storage sized by the watched set, with a directory keyed by cluster chunk id and a hook in the fence's migration step |
| The Engine-Subscriptions track | A built-in track between the tick fence and the flush. Compute runs after the fence, publish after the flush, and the two are skippable only together (rule `SUB-02`) |
| Network identities | Process-global netIds; a released one waits a tick before reuse, so no frame carries both an identity's leave and its re-enter (rule `SUB-06`) |
| Ingress rings | Per-session SPSC command rings over native memory, carved from a slab pool. A full ring drops and counts — it never blocks the transport thread or throws |
| Canonical hashing | The shared FNV-1a construction behind the catalog digest |

## Where the design lives

`claude/design/Subscriptions/` in the knowledge base: the model and public API, execution, the wire protocol, transports, the SDKs, per-entity state and the delivery plan, plus a `foundation/` folder with one document per prerequisite.
