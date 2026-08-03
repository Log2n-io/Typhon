// unset

namespace Typhon.Engine.Internals;

/// <summary>
/// Estimates the number of entities that satisfy a single-field predicate.
/// Used by the query planner to choose the most selective index.
/// </summary>
internal interface ISelectivityEstimator
{
    /// <summary>
    /// Estimates the cardinality (number of matching entities) for a predicate on the field at
    /// <paramref name="fieldIndex"/> using <paramref name="op"/> and <paramref name="threshold"/>
    /// (encoded via <see cref="QueryResolverHelper.EncodeThreshold"/>).
    /// </summary>
    /// <remarks>
    /// Takes the statistics array rather than the <see cref="ComponentTable"/> that owns one (#665). Both implementations only ever reached through the table
    /// for <c>table.IndexStats</c>, and a cluster-backed archetype's statistics live on its <c>ArchetypeClusterState</c> instead — so the narrower parameter
    /// is what lets one estimator serve both index homes.
    /// </remarks>
    long EstimateCardinality(IndexStatistics[] stats, int fieldIndex, CompareOp op, long threshold);
}
