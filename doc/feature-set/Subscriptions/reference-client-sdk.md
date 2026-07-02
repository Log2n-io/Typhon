# Reference C# Client SDK
> A drop-in `Typhon.Client` library that turns wire bytes into a ready-to-read local entity cache.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Every other Subscriptions feature describes what the *server* sends. Something still has to open the
socket, frame the bytes, deserialize them, and keep a local copy of each View's entities in sync as
Added/Modified/Removed deltas arrive — tedious, easy-to-get-wrong plumbing that has nothing to do with
game logic. `Typhon.Client` is the reference implementation of that consumer: connect, register interest
in a View by name, and read a live entity cache. No socket code, no MemoryPack calls, no manual delta
application in application code.

## ⚙️ How it works (in brief)

`TyphonClient.Connect` opens a TCP socket and starts a dedicated receive thread on a `TyphonConnection`.
That thread reads each length-prefixed frame, deserializes it as a `TickDeltaMessage` (MemoryPack), and
dispatches it: `SubscriptionEvent`s drive per-View state (`Subscribed`/`Unsubscribed`/`SyncComplete`/`Resync`),
and `ViewDeltaMessage`s are applied to a `ViewSubscription`'s entity cache — `Added` entities are inserted,
`Modified` entities are patched in place, `Removed` entities are deleted. The cache is a plain
`Id → CachedEntity` map; application code reads it (or reacts to the subscription's events) on its own
schedule. All callbacks fire synchronously on the receive thread. `Subscribe(viewName)` only registers
*local* interest by name — matching is against whatever the server already pushed via its own
server-driven `SetSubscriptions`; the v1 client cannot request a subscription from the server.

## 💻 Usage

```csharp
using var conn = TyphonClient.Connect("server-host", 9000);

// Required once per component type before CachedEntity.Get<T>/TryGet<T> can decode it.
conn.RegisterComponent<Position>(componentId: 1);
conn.RegisterComponent<Health>(componentId: 2);

var npcs = conn.Subscribe("world_npcs");
npcs.OnEntityAdded += entity => SpawnClientEntity(entity.Id, entity.Get<Position>(1));
npcs.OnEntityModified += (entity, changed) => PatchClientEntity(entity.Id, entity.Get<Position>(1));
npcs.OnEntityRemoved += entityId => DespawnClientEntity(entityId);
npcs.OnSyncComplete += () => MarkViewReady("world_npcs");
npcs.OnResync += () => RebuildClientEntitiesFrom(npcs.Entities);

conn.OnDisconnected += (_, ex) => LogDisconnect(ex);
```

| Option (`TyphonConnectionOptions`) | Default | Effect |
|---|---|---|
| `ReceiveBufferSize` | 65536 (64 KB) | Initial frame buffer; grows automatically for larger frames |
| `AutoReconnect` | `true` | Reconnect with exponential backoff (1s, doubling) on connection loss |
| `ReconnectMaxDelay` | 30s | Cap on the backoff delay |
| `Logger` | `null` | Optional `ILogger`; `null` disables logging at zero cost |

## ⚠️ Guarantees & limits

- **All events fire on the receive thread** — `OnConnected`, `OnDisconnected`, `OnReconnected`, `OnTick`,
  and every `ViewSubscription` event. Application code must not block in a callback; it stalls delta
  processing for that connection.
- **`Subscribe` is local-only** — it does not send a request to the server. It registers a name to match
  against Views the server already pushed (server-driven `SetSubscriptions`); client-initiated subscription
  requests are not part of v1.
- **The cache is the source of truth between ticks** — `ViewSubscription.Entities` reflects exactly the
  last applied tick; read it for rendering/logic rather than re-deriving state from individual callbacks.
- **`CachedEntity.Get<T>`/`TryGet<T>` require prior `RegisterComponent<T>`** for that component ID, and
  `T`'s size must match the cached byte length — a mismatch throws (`Get`) or returns `false` (`TryGet`).
- **v1 component encoding is full-replace** — `Modified` always carries full component bytes
  (`FieldDirtyBits = ~0UL`); per-field patching is a future wire upgrade the client already tolerates.
- **Reconnect clears all caches** — on connection loss with `AutoReconnect = true`, every
  `ViewSubscription`'s cache is cleared and `ViewId` reset; the server re-establishes subscriptions and
  resyncs after `OnReconnected` fires.
- **C#-only** — this is the one reference SDK for v1; other languages would need to reimplement framing,
  MemoryPack decoding, and the cache-apply logic described here.

## 🧪 Tests

- [DeltaProcessingTests](../../../test/Typhon.Client.Tests/DeltaProcessingTests.cs) — Added/Modified/Removed/
  SyncComplete/Resync/Disconnect dispatch over a fake server, plus lazy-subscription for server-pushed Views the
  client hasn't named locally yet
- [ViewSubscriptionTests](../../../test/Typhon.Client.Tests/ViewSubscriptionTests.cs) — the per-View entity cache
  itself: add/remove/modify, sync/resync state resets
- [CachedEntityTests](../../../test/Typhon.Client.Tests/CachedEntityTests.cs) — `Get<T>`/`TryGet<T>` decode
  guarantees: missing component, size mismatch
- [TyphonConnectionIntegrationTests](../../../test/Typhon.Client.Tests/Integration/TyphonConnectionIntegrationTests.cs)
  — `Client_Connects_SubscribesAndReceivesAddedEntity`: real socket, real server, end to end

## 🔗 Related

- Related feature: [TCP Transport & Wire Format](./wire-transport.md), [Incremental Sync](./incremental-sync.md), [Backpressure & Resync](./backpressure-resync.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Client Data Model -->
