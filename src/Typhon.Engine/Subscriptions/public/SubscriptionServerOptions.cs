using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration for the subscription server (TCP listener, send buffers, backpressure thresholds).
/// </summary>
[PublicAPI]
public sealed class SubscriptionServerOptions
{
    /// <summary>TCP port to listen on. Default: 9000.</summary>
    public int Port { get; set; } = 9000;

    /// <summary>Per-client send buffer capacity in bytes. Default: 262144 (256 KB).</summary>
    public int SendBufferCapacity { get; set; } = 262144;

    /// <summary>Fill percentage at which a warning is logged (0.0–1.0). Default: 0.75.</summary>
    public float BackpressureWarningThreshold { get; set; } = 0.75f;

    /// <summary>Maximum number of entities per incremental sync batch. Default: 200.</summary>
    public int SyncBatchSize { get; set; } = 200;

    /// <summary>Maximum concurrent client connections. 0 = unlimited. Default: 0.</summary>
    public int MaxClients { get; set; }

    /// <summary>Ring buffer capacity for published View delta buffers. Must be power of 2. Default: 8192.</summary>
    public int PublishedViewBufferCapacity { get; set; } = 8192;
}
