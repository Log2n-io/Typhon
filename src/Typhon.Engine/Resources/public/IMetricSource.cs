using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Implemented by resources that expose measurable metrics.
/// </summary>
/// <remarks>
/// <para>
/// Not all <see cref="IResource"/> nodes implement <see cref="IMetricSource"/> — only those
/// with meaningful state to measure. The graph uses runtime <c>is</c> checks during tree
/// traversal to discover metric sources.
/// </para>
/// <para>
/// This separation follows the Interface Segregation Principle: pure grouping nodes (like "Root"
/// or "Storage") exist only to organize the tree and have no counters of their own. Their metrics
/// are aggregates of their children, computed by the snapshot system.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="ReadMetrics"/> is called from the snapshot thread while
/// other threads may update the same fields. This is acceptable because reads of primitive types
/// are atomic on x64, and approximate values are acceptable for diagnostics.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class PageCache : IResource, IMetricSource
/// {
///     private long _cacheHits;
///     private long _cacheMisses;
///     private long _peakBytes;
///     private long _currentBytes;
///
///     public void ReadMetrics(IMetricWriter writer)
///     {
///         writer.WriteMemory(_currentBytes, _peakBytes);
///         writer.WriteThroughput("CacheHits", _cacheHits);
///         writer.WriteThroughput("CacheMisses", _cacheMisses);
///     }
///
///     public void ResetPeaks()
///     {
///         _peakBytes = _currentBytes;
///     }
/// }
/// </code>
/// </example>
[PublicAPI]
public interface IMetricSource
{
    /// <summary>
    /// Writes current metric values to the provided writer.
    /// </summary>
    /// <param name="writer">The writer to report metrics to. Call one or more Write* methods.</param>
    /// <remarks>
    /// <para>
    /// Called during snapshot collection (every 1-5 seconds). Implementations should:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Read fields and write to writer quickly (target &lt; 100ns)</description></item>
    /// <item><description>Not allocate (no new objects, no LINQ, no string formatting)</description></item>
    /// <item><description>Not acquire locks (read fields directly, accept slight inconsistency)</description></item>
    /// <item><description>Not call other IMetricSource.ReadMetrics() — the graph handles recursion</description></item>
    /// </list>
    /// <para>
    /// Only report metric kinds that are relevant to this resource. For example, a TransactionPool
    /// might only report Capacity and Throughput, not Memory or DiskIO.
    /// </para>
    /// </remarks>
    void ReadMetrics(IMetricWriter writer);

    /// <summary>
    /// Resets all high-water mark metrics (PeakBytes, MaxWaitUs, MaxUs, etc.) to current values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by ResourceGraph.ResetPeaks() to enable windowed peak measurements. After displaying
    /// or exporting peak values, this method resets them so subsequent peaks represent new maxima
    /// since the last reset.
    /// </para>
    /// <para>
    /// If this node has no high-water marks, implement as an empty method. Plain writes are
    /// sufficient — snapshots are taken every 1-5 seconds, so eventual visibility is acceptable.
    /// </para>
    /// </remarks>
    void ResetPeaks();
}
