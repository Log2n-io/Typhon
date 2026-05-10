using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Controls when page CRC verification occurs.
/// </summary>
[PublicAPI]
public enum PageChecksumVerification
{
    /// <summary>Verify page CRC on every load from disk. Detects corruption on first access and triggers FPI repair.</summary>
    OnLoad,

    /// <summary>Only verify page CRC during crash recovery. Normal operation skips CRC checks for lower overhead.</summary>
    RecoveryOnly,
}

/// <summary>
/// Configuration options for resource budgets and limits.
/// Set at startup, immutable thereafter.
/// </summary>
/// <remarks>
/// <para>
/// Budget limits are part of <see cref="DatabaseEngineOptions"/>, passed to <c>DatabaseEngine</c> at construction.
/// Components receive only their specific limits via constructor injection — they don't have access to
/// the full ResourceOptions object.
/// </para>
/// <para>
/// Call <see cref="Validate"/> at startup to verify that fixed allocations fit within the total memory budget.
/// Growable resources (transactions, indexes, query buffers) have runtime caps that prevent unbounded growth.
/// </para>
/// <example>
/// <code>
/// var options = new DatabaseEngineOptions
/// {
///     Resources = new ResourceOptions
///     {
///         PageCachePages = 262144,           // 2 GB
///         MaxActiveTransactions = 1000,
///         WalRingBufferSizeBytes = 8 &lt;&lt; 20,  // 8 MB
///     }
/// };
/// options.Resources.Validate();
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public class ResourceOptions
{
    // ═══════════════════════════════════════════════════════════════
    // PAGE CACHE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Number of pages in the page cache.
    /// Each page is 8 KB, so 256 pages = 2 MB cache.
    /// </summary>
    public int PageCachePages { get; set; } = 256;

    /// <summary>
    /// Maximum pages the cache can grow to.
    /// Used if dynamic cache sizing is enabled (future).
    /// </summary>
    public int MaxPageCachePages { get; set; } = 16384;  // 128 MB

    // ═══════════════════════════════════════════════════════════════
    // TRANSACTIONS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum concurrent active transactions.
    /// Beyond this, CreateTransaction throws <see cref="ResourceExhaustedException"/>.
    /// </summary>
    public int MaxActiveTransactions { get; set; } = 1000;

    /// <summary>
    /// Number of Transaction objects kept in pool for reuse.
    /// When pool is empty, new objects are allocated (Degrade policy).
    /// </summary>
    public int TransactionPoolSize { get; set; } = 16;

    // ═══════════════════════════════════════════════════════════════
    // WAL & DURABILITY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Size of the WAL ring buffer in bytes.
    /// When full, commit threads block until WAL writer drains it.
    /// </summary>
    public int WalRingBufferSizeBytes { get; set; } = 8 * 1024 * 1024;  // 8 MB

    /// <summary>
    /// Back-pressure threshold as fraction of ring buffer capacity.
    /// At this level, commits start blocking.
    /// </summary>
    public double WalBackPressureThreshold { get; set; } = 0.8;  // 80%

    /// <summary>
    /// Maximum size of a single WAL segment file in bytes.
    /// </summary>
    public long WalMaxSegmentSizeBytes { get; set; } = 64L << 20;  // 64 MB

    /// <summary>
    /// Maximum number of WAL segment files.
    /// When all are full, checkpoint is forced.
    /// </summary>
    public int WalMaxSegments { get; set; } = 4;

    // ═══════════════════════════════════════════════════════════════
    // CHECKPOINT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum dirty pages before forcing a checkpoint.
    /// </summary>
    public int CheckpointMaxDirtyPages { get; set; } = 10000;

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

    // ═══════════════════════════════════════════════════════════════
    // BACKUP
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Size of the CoW shadow buffer in pages.
    /// Writers block when all slots are occupied.
    /// Each page is 8 KB, so 512 pages = 4 MB.
    /// </summary>
    public int ShadowBufferPages { get; set; } = 512;  // 4 MB

    // ═══════════════════════════════════════════════════════════════
    // OVERALL
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Total memory budget for the engine in bytes.
    /// Used for validation at startup (all fixed allocations must fit).
    /// </summary>
    public long TotalMemoryBudgetBytes { get; set; } = 4L << 30;  // 4 GB

    /// <summary>
    /// Page size in bytes. This is a constant (8 KB) but exposed for calculations.
    /// </summary>
    public const int PageSizeBytes = 8192;

    /// <summary>
    /// Validates that fixed allocations fit within the total memory budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only validates <b>fixed allocations</b> (allocated immediately at startup):
    /// </para>
    /// <list type="bullet">
    /// <item><description>Page cache pages × 8 KB</description></item>
    /// <item><description>WAL ring buffer</description></item>
    /// <item><description>WAL segments × max size</description></item>
    /// <item><description>Shadow buffer pages × 8 KB</description></item>
    /// </list>
    /// <para>
    /// Growable resources (active transactions, index nodes, query buffers) have <b>runtime caps</b>
    /// that prevent unbounded growth — they don't need upfront validation.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured fixed allocations exceed the total memory budget.
    /// </exception>
    public void Validate()
    {
        var pageCacheBytes = (long)PageCachePages * PageSizeBytes;
        var walBytes = WalRingBufferSizeBytes + ((long)WalMaxSegments * WalMaxSegmentSizeBytes);
        var shadowBytes = (long)ShadowBufferPages * PageSizeBytes;

        var totalRequired = pageCacheBytes + walBytes + shadowBytes;

        if (totalRequired > TotalMemoryBudgetBytes)
        {
            throw new InvalidOperationException(
                $"Resource configuration requires {totalRequired / 1_000_000} MB " +
                $"but budget is only {TotalMemoryBudgetBytes / 1_000_000} MB. " +
                $"Breakdown: PageCache={pageCacheBytes / 1_000_000} MB, " +
                $"WAL={walBytes / 1_000_000} MB, " +
                $"ShadowBuffer={shadowBytes / 1_000_000} MB");
        }
    }

    /// <summary>
    /// Calculates the total fixed memory allocation in bytes.
    /// </summary>
    /// <returns>Total bytes that will be allocated at startup.</returns>
    public long CalculateFixedAllocationBytes()
    {
        var pageCacheBytes = (long)PageCachePages * PageSizeBytes;
        var walBytes = WalRingBufferSizeBytes + ((long)WalMaxSegments * WalMaxSegmentSizeBytes);
        var shadowBytes = (long)ShadowBufferPages * PageSizeBytes;

        return pageCacheBytes + walBytes + shadowBytes;
    }

    /// <summary>
    /// Gets the remaining memory budget after fixed allocations.
    /// </summary>
    /// <returns>Bytes available for growable resources (transactions, indexes, queries).</returns>
    public long CalculateAvailableBudgetBytes() => TotalMemoryBudgetBytes - CalculateFixedAllocationBytes();
}
