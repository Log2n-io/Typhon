# Subscriptions
> Server-driven, View-based client state replication: the runtime publishes query Views, diffs them against connected clients' subscription sets each tick, and pushes a single MemoryPack-encoded `TickDelta` over TCP per client per tick — turning "subscribe to a query" into automatic incremental sync without the developer writing networking, change-detection, or serialization code.

> 🔬 **Recommended:** Subscriptions doesn't have its own in-depth-overview chapter yet — read the Subscriptions section inside [in-depth-overview/09-querying.md](../../in-depth-overview/09-querying.md) (Chapter 09: Querying) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Published Views](published-views/README.md) | Register a query View as a subscribable target via `TyphonRuntime.PublishView`, as either one shared instance for all clients or a per-client factory | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Shared Views](published-views/shared-views.md) | One View instance, refreshed and diffed once per tick, fanned out to every subscriber | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Per-Client Views](published-views/per-client-views.md) | A `Func<ClientContext, ViewBase>` that builds a fresh, parameterized View for each subscriber | ✅ Implemented | 🔵 Core |
| [Subscription Management (SetSubscriptions)](subscription-management/README.md) | Atomic, idempotent, diff-based API to set a client's full subscription list each tick — the runtime computes subscribed/unsubscribed/kept Views and applies the transition within one TickDelta | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Server-Driven Subscriptions (v1)](subscription-management/subscription-server-driven.md) | Game code calls `SetSubscriptions` whenever game state changes; the runtime applies the diff-based transition on the next tick | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Client-Initiated Subscriptions (v2)](subscription-management/subscription-client-initiated.md) | Clients request their own subscription changes via an `OnClientSubscriptionRequest` callback, validated server-side before being applied | 📋 Planned | 🟣 Advanced |
| [Client Connections & Lifecycle](client-connections.md) | TCP listener thread accepts sockets and assigns each a `ConnectionId`; the public `ClientContext` is the only handle game code touches — connections, subscriber lists, and per-client Views are cleaned up automatically on disconnect | ✅ Implemented | 🔵 Core |
| [Per-Tick Delta Computation & Encoding](delta-computation/README.md) | After `WriteTickFence`, the Output phase diffs published Views (ring buffer ∪ dirty-bitmap supplement) into Added/Removed/Modified and encodes only the changed component bytes | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Component-Level Dirty Encoding (v1)](delta-computation/delta-encoding-component-dirty.md) | Modified entities send full bytes for each component whose chunk was dirty this tick; unchanged components are omitted | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Per-Field Dirty Encoding (v1.1)](delta-computation/delta-encoding-per-field-dirty.md) | Planned output-phase field diffing to shrink Modified payloads to only the bytes of fields that actually changed; not implemented yet | 📋 Planned | 🟣 Advanced |
| [Subscription Server Configuration](server-configuration.md) | Tunable knobs for the TCP subscription listener: port, max clients, per-client send buffer capacity, backpressure warning threshold, incremental sync batch size, and published-View ring buffer capacity | ✅ Implemented | 🔵 Core |
| [Reference C# Client SDK](reference-client-sdk.md) | `Typhon.Client` connects over TCP, decodes `TickDeltaMessage`s, and maintains a per-View local entity cache that application code reads directly | ✅ Implemented | 🔵 Core |
| [Published/System-Input View Separation Guard](published-view-isolation.md) | Runtime throws if a published View doubles as a system input (or vice versa), since the View's MPSC delta ring buffer can only have one consumer | ✅ Implemented | 🟣 Advanced |
| [TCP Transport & Wire Format](wire-transport.md) | One length-prefixed, MemoryPack-serialized `TickDeltaMessage` per client per tick over TCP_NODELAY; steady-state shared-only clients get a single serialize-once frame memcpy'd to every matching send buffer instead of N serializations | ✅ Implemented | 🟣 Advanced |
| [Incremental Sync](incremental-sync.md) | New subscriptions to large Views sync in tick-sized batches instead of one giant first delta | ✅ Implemented | 🟣 Advanced |
| [Backpressure & Resync Recovery](backpressure-resync.md) | A full client send buffer drops one tick's delta and triggers an automatic full-state resync — never an unbounded queue | ✅ Implemented | 🟣 Advanced |
| [Subscription Priority & Overload Throttling](priority-overload-throttling.md) | Critical/Normal/Low priority per published View; under overload Normal/Low Views are throttled while Critical Views always go out | ✅ Implemented | 🟣 Advanced |
| [Subscription Telemetry & Tracing](subscription-telemetry.md) | Per-tick `OutputPhaseMs`/`DeltasPushed`/`OverflowCount` counters plus a live per-tick Output-phase trace span; the deeper per-subscriber dispatch span subtree (kinds 235-240) ships its wire format but has no producer wired yet | 🚧 Partial | 🟣 Advanced |

## Internal Features

*No internal-only engine machinery in this category — every feature here is directly reachable and usable by application code (server-side publish/subscribe API or the client-side wire protocol/SDK).*