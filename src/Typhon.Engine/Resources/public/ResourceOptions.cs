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
    /// <remarks>
    /// This is a <b>durability</b> knob — it bounds how much WAL a crash has to replay. It is deliberately NOT the knob
    /// that keeps the page cache alive; that is <see cref="CheckpointDirtyPageThresholdPercent"/>, because cache
    /// survival depends on how fast pages are dirtied, not on the clock.
    /// </remarks>
    public int CheckpointIntervalMs { get; set; } = 30000;  // 30 seconds

    /// <summary>
    /// Run a checkpoint as soon as this percentage of the page cache owes a writeback, without waiting for
    /// <see cref="CheckpointIntervalMs"/>. <c>0</c> disables the trigger, leaving the timer and explicit forces as the
    /// only causes — which is the pre-#830 behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page cannot be evicted until a checkpoint has written it (PS-10), so the cache's reclaim rate IS the checkpoint
    /// rate and the peak writeback debt is "everything dirtied inside one interval". On a workload that dirties more
    /// than the cache holds in <see cref="CheckpointIntervalMs"/>, the cache saturates and the next page allocation has
    /// nothing to evict — the engine dies on <c>PageCacheBackpressureTimeout</c> with a cache that is ~100 % dirty.
    /// Measured on the SpaceBattle demo (256 MiB cache, ~25 000 entities at 60 Hz): the 30 s default died at ~58 000
    /// ticks with 32 758 of 32 768 pages owed, while a 1 s cadence ran to 104 506 ticks with debt never above 15 %.
    /// </para>
    /// <para>
    /// The default of 25 % leaves three quarters of the cache as headroom for the cycle to complete while it runs. The
    /// clock cannot serve this purpose: the safe interval is a function of cache size and write rate, and nothing in the
    /// engine derives one from the other — the user only finds out they configured it wrong when the engine stops.
    /// </para>
    /// <para>
    /// Raising the frequency is not the trade against throughput it looks like. On the measurement above the simulation
    /// got <i>faster</i> (14–28 ms per tick → 9–15 ms) and the WAL stopped sawtoothing to 6 GB, because back-pressure
    /// stalls and giant flush bursts both disappeared. Caveat: that is one workload on one machine.
    /// </para>
    /// </remarks>
    public int CheckpointDirtyPageThresholdPercent { get; set; } = 25;

    /// <summary>
    /// Bounded budget (milliseconds) for the checkpoint cycle's WAL durability barrier waits (CK-02). On timeout the
    /// cycle raises a transient <see cref="WalBackPressureTimeoutException"/>, which the failure classification (CK-06)
    /// treats as <see cref="DurabilityHealth.Degraded"/> + retry-next-cycle — never a permanent stall.
    /// </summary>
    public int CheckpointBarrierTimeoutMs { get; set; } = 30000;  // 30 seconds
}
