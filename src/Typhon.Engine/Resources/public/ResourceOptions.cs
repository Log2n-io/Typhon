using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Controls when page CRC verification occurs.
/// </summary>
[PublicAPI]
public enum PageChecksumVerification
{
    /// <summary>Verify page CRC on every load from disk. Detects corruption on first access and throws (no repair — FPI was retired; recovery heals via the rebuild net).</summary>
    OnLoad,

    /// <summary>Only verify page CRC during crash recovery. Normal operation skips CRC checks for lower overhead.</summary>
    RecoveryOnly,

    /// <summary>Crash-recovery suspect mode: compute the CRC and, on mismatch, RECORD the page as suspect (never throw, never FPI-repair) so the post-apply
    /// resolution can heal it (derived → rebuilt; orphaned primary → in-window-replaced) or fail the open loudly (RB-04) if it holds live primary data. The
    /// engine sets this on the crash path and restores the configured mode once recovery completes.</summary>
    RecoverySuspect,
}

/// <summary>
/// Runtime knobs for the database engine's resource subsystems (transaction chain, WAL ring buffer, checkpoint cadence,
/// page-CRC policy). Set at startup via <see cref="DatabaseEngineOptions.Resources"/>, immutable thereafter.
/// </summary>
/// <remarks>
/// Every property here is <b>wired</b> — it drives real engine behavior and is range-validated at DI resolution by
/// <c>DatabaseEngineOptionsValidator</c>. (A prior aspirational memory-budget surface — page-cache pages, WAL segment
/// sizing, a shadow-buffer budget and a never-called <c>Validate()</c> — was removed in #148 as vestigial: it governed no
/// allocations. The real cache size lives on <see cref="PagedMMFOptions.DatabaseCacheSize"/>; real WAL segment sizing on
/// <see cref="WalWriterOptions"/>.)
/// </remarks>
[PublicAPI]
public class ResourceOptions
{
    // ═══════════════════════════════════════════════════════════════
    // TRANSACTIONS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum concurrent active transactions. Beyond this, CreateTransaction throws <see cref="ResourceExhaustedException"/>.
    /// </summary>
    public int MaxActiveTransactions { get; set; } = 1000;

    // ═══════════════════════════════════════════════════════════════
    // WAL & DURABILITY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Size of the WAL ring buffer in bytes. When full, commit threads block until the WAL writer drains it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <b>total</b> pinned allocation: <c>WalCommitBuffer</c> ping-pongs two halves of
    /// <c>WalRingBufferSizeBytes / 2</c> each, so the default reserves 64 MB up front and each half is 32 MB. Lower it for
    /// memory-constrained or low-write deployments — the engine is correct at any size, it just swaps buffers more often.
    /// </para>
    /// <para>
    /// The default is sized for <b>tail latency, not throughput</b>. Measured on a 20 001-entity cluster archetype at 120 Hz
    /// (#559): the median tick is flat across 8/16/32/64 MB at ~17.6 ms, but the worst tick falls from ~29 ms to ~18 ms at 64 MB.
    /// A ring that fills makes producers block on the buffer swap, which shows up as an occasional tick blowing several times its
    /// budget — the failure mode a real-time engine cares about most.
    /// </para>
    /// </remarks>
    public int WalRingBufferSizeBytes { get; set; } = 64 * 1024 * 1024;  // 64 MB total pinned, 2 x 32 MB halves — see remarks

    // ═══════════════════════════════════════════════════════════════
    // CHECKPOINT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Controls when page CRC verification occurs.
    /// <see cref="PageChecksumVerification.OnLoad"/> verifies on every page load (higher safety, slight overhead).
    /// <see cref="PageChecksumVerification.RecoveryOnly"/> only during crash recovery (lower overhead).
    /// </summary>
    public PageChecksumVerification PageChecksumVerification { get; set; } = PageChecksumVerification.OnLoad;

    /// <summary>
    /// Checkpoint interval when idle (milliseconds).
    /// </summary>
    public int CheckpointIntervalMs { get; set; } = 30000;  // 30 seconds

    /// <summary>
    /// Bounded budget (milliseconds) for the checkpoint cycle's WAL durability barrier waits (CK-02). On timeout the
    /// cycle raises a transient <see cref="WalBackPressureTimeoutException"/>, which the failure classification (CK-06)
    /// treats as <see cref="DurabilityHealth.Degraded"/> + retry-next-cycle — never a permanent stall.
    /// </summary>
    public int CheckpointBarrierTimeoutMs { get; set; } = 30000;  // 30 seconds
}
