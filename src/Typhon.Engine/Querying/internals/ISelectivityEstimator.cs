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
    long EstimateCardinality(ComponentTable table, int fieldIndex, CompareOp op, long threshold);
}
