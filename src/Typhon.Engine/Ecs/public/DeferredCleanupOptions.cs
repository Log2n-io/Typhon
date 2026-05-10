using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for the deferred cleanup subsystem that manages MVCC revision cleanup
/// when long-running transactions block immediate cleanup.
/// </summary>
/// <remarks>
/// <para>
/// When a non-tail transaction commits, entities it modified are enqueued for deferred cleanup.
/// These options control queue monitoring thresholds, batch sizes, and lazy cleanup behavior.
/// </para>
/// </remarks>
[PublicAPI]
public class DeferredCleanupOptions
{
    /// <summary>
    /// Queue size threshold that triggers a warning log. Indicates a long-running transaction
    /// is blocking cleanup for many entities.
    /// </summary>
    public int HighWaterMark { get; set; } = 100_000;

    /// <summary>
    /// Queue size threshold that triggers a critical warning log. Indicates severe accumulation
    /// that may impact performance.
    /// </summary>
    public int CriticalThreshold { get; set; } = 1_000_000;

    /// <summary>
    /// Maximum number of entities to clean up in a single deferred cleanup pass.
    /// </summary>
    public int MaxCleanupBatchSize { get; set; } = 1000;
}
