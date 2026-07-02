# Subscription Server Configuration
> One options object tunes the TCP listener's port, capacity, and backpressure behavior.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Every other subscription feature — client connections, backpressure/resync, incremental sync — has a knob that
needs a sane default for development and a tunable value for production (different port, more headroom for a
laptop demo vs. a 500-player server, tighter batch sizes to protect the tick budget). Rather than scattering
these across constructor parameters or environment variables, Typhon collects them into a single options
object that's set once, at runtime startup.

## ⚙️ How it works (in brief)

`SubscriptionServerOptions` is a plain settings object assigned to `RuntimeOptions.SubscriptionServer` before
the runtime starts. There is nothing to call at runtime — the listener, send buffers, and Output-phase batching
all read these values once at startup and apply them for the life of the process. Leaving it unset
(`null`) or constructing it with no initializers falls back to development-friendly defaults.

## 💻 Usage

```csharp
var runtimeOptions = new RuntimeOptions
{
    SubscriptionServer = new SubscriptionServerOptions
    {
        Port = 9000,
        MaxClients = 500,
        SendBufferCapacity = 262_144,
        BackpressureWarningThreshold = 0.75f,
        SyncBatchSize = 200,
        PublishedViewBufferCapacity = 8192,
    },
};

// runtimeOptions flows into the runtime/hosting setup that starts the listener
// and Output-phase subscription pipeline.
```

| Option | Default | Effect |
|--------|---------|--------|
| `Port` | 9000 | TCP listen port (dual-stack IPv6/IPv4) |
| `MaxClients` | 0 (unlimited) | Connections beyond this are refused at accept time |
| `SendBufferCapacity` | 262144 (256 KB) | Per-client send buffer; full buffer drops a tick and triggers resync |
| `BackpressureWarningThreshold` | 0.75 | Send-buffer fill ratio (0.0–1.0) that logs a warning |
| `SyncBatchSize` | 200 | Max entities per incremental-sync batch per client per View per tick |
| `PublishedViewBufferCapacity` | 8192 | Capacity (must be a power of 2) of a published View's delta ring buffer |

## ⚠️ Guarantees & limits

- **Set once, at startup** — there is no API to change these values on a running server; reconfiguring means
  restarting the runtime with new options.
- **`null` is a valid default** — omitting `RuntimeOptions.SubscriptionServer` runs the subscription server with
  every option at its built-in default rather than failing startup.
- **`MaxClients = 0` means unlimited** — there is no implicit cap; size it explicitly for production deployments.
- **`PublishedViewBufferCapacity` must be a power of 2** — this matches the ring-buffer requirement used
  throughout Typhon's View infrastructure (see [Querying](../Querying/README.md)).
- This object only configures the server side — client-side connection settings (reconnect, timeouts) belong to
  the client SDK, not here.

## 🧪 Tests

- [SubscriptionStressTests](../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionStressTests.cs) —
  `Backpressure_SlowClient_TriggersResync` sets a non-default `SendBufferCapacity` and shows it changing overflow
  behavior — the one place a config knob is verified to take effect end-to-end

## 🔗 Related

- Related feature: [Client Connections & Lifecycle](./client-connections.md), [Backpressure & Resync Recovery](./backpressure-resync.md), [Incremental Sync](./incremental-sync.md), [TCP Transport & Wire Format](./wire-transport.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md -->
