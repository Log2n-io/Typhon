using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Implemented by resources that can provide detailed debug properties for diagnostic drill-down.
/// </summary>
/// <remarks>
/// <para>
/// This interface complements <see cref="IMetricSource"/> by providing detailed breakdown information
/// that would be too expensive or verbose for regular metric collection. While <see cref="IMetricSource.ReadMetrics"/>
/// reports aggregated metrics suitable for dashboards, <see cref="GetDebugProperties"/> provides the
/// full breakdown for debugging specific issues.
/// </para>
/// <para>
/// <b>Use cases:</b>
/// </para>
/// <list type="bullet">
/// <item><description>Per-segment capacity breakdown when <see cref="IMetricSource"/> reports only totals</description></item>
/// <item><description>Individual latch contention when owner aggregates overall contention</description></item>
/// <item><description>Internal state that's useful for debugging but not for monitoring</description></item>
/// </list>
/// <para>
/// <b>Thread safety:</b> Unlike <see cref="IMetricSource.ReadMetrics"/> which must be zero-allocation,
/// <see cref="GetDebugProperties"/> may allocate since it's called infrequently for debugging.
/// Implementations should still avoid taking locks to prevent affecting production behavior.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class ComponentTable : IResource, IMetricSource, IDebugPropertiesProvider
/// {
///     // IMetricSource reports aggregated totals
///     public void ReadMetrics(IMetricWriter writer)
///     {
///         writer.WriteCapacity(
///             totalAllocatedChunks,  // Sum of all segments
///             totalCapacity);
///     }
///
///     // IDebugPropertiesProvider shows per-segment breakdown
///     public IReadOnlyDictionary&lt;string, object&gt; GetDebugProperties()
///     {
///         return new Dictionary&lt;string, object&gt;
///         {
///             ["ComponentSegment.Allocated"] = _componentSegment.AllocatedChunkCount,
///             ["ComponentSegment.Capacity"] = _componentSegment.ChunkCapacity,
///             ["RevisionSegment.Allocated"] = _revisionSegment.AllocatedChunkCount,
///             ["RevisionSegment.Capacity"] = _revisionSegment.ChunkCapacity,
///         };
///     }
/// }
/// </code>
/// </example>
[PublicAPI]
public interface IDebugPropertiesProvider
{
    /// <summary>
    /// Gets detailed debug properties for diagnostic drill-down.
    /// </summary>
    /// <returns>
    /// A dictionary of property names to values. Values should be primitives, strings,
    /// or types that have meaningful <c>ToString()</c> implementations.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method may allocate (returns new dictionary) since it's called infrequently.
    /// Callers should cache results if needed across multiple accesses.
    /// </para>
    /// <para>
    /// Property naming convention: Use dot-notation for nested concepts (e.g.,
    /// "ComponentSegment.Allocated", "Contention.MaxWaitUs").
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, object> GetDebugProperties();
}
