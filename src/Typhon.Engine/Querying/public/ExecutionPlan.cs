using System.Text;

namespace Typhon.Engine;

/// <summary>
/// Describes the primary scan stream and selectivity-ordered filter chain for a query.
/// Built by <see cref="PlanBuilder"/>, consumed by <see cref="PipelineExecutor"/>.
/// </summary>
/// <remarks>
/// The primary stream scans either the PK index (<see cref="PrimaryFieldIndex"/> == -1) or a secondary index (<see cref="PrimaryFieldIndex"/> >= 0).
/// When a secondary index is selected (unique or AllowMultiple), entity PKs are recovered via <see cref="CompRevStorageHeader.EntityPK"/>.
/// The planner selects the most selective index to minimize the number of entities scanned.
/// </remarks>
public readonly struct ExecutionPlan
{
    /// <summary>
    /// Index into <see cref="ComponentTable.IndexedFieldInfos"/> for the primary scan stream.
    /// -1 means scan the PK index (full table scan with filter evaluation).
    /// >= 0 means scan a secondary index with a narrowed key range.
    /// </summary>
    public readonly int PrimaryFieldIndex;

    /// <summary>Key type of the primary secondary index. Only valid when <see cref="PrimaryFieldIndex"/> >= 0.</summary>
    public readonly KeyType PrimaryKeyType;

    /// <summary>Lower bound (inclusive) for the primary scan range (PK-encoded or field-value-encoded).</summary>
    public readonly long PrimaryScanMin;

    /// <summary>Upper bound (inclusive) for the primary scan range (PK-encoded or field-value-encoded).</summary>
    public readonly long PrimaryScanMax;

    /// <summary>True when the primary stream should iterate in descending order.</summary>
    public readonly bool Descending;

    /// <summary>
    /// Field evaluators ordered by ascending estimated cardinality (most selective first).
    /// Evaluated via component reads with short-circuit on first failure.
    /// </summary>
    public readonly FieldEvaluator[] OrderedEvaluators;

    /// <summary>Estimated cardinality per evaluator (parallel array, same order).</summary>
    public readonly long[] EstimatedCounts;

    public ExecutionPlan(int primaryFieldIndex, KeyType primaryKeyType, long scanMin, long scanMax, bool descending, FieldEvaluator[] orderedEvaluators, 
        long[] estimatedCounts)
    {
        PrimaryFieldIndex = primaryFieldIndex;
        PrimaryKeyType = primaryKeyType;
        PrimaryScanMin = scanMin;
        PrimaryScanMax = scanMax;
        Descending = descending;
        OrderedEvaluators = orderedEvaluators;
        EstimatedCounts = estimatedCounts;
    }

    /// <summary>True when the primary stream uses a secondary index (not PK scan).</summary>
    public bool UsesSecondaryIndex => PrimaryFieldIndex >= 0;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (PrimaryFieldIndex >= 0)
        {
            sb.Append($"Index scan Field[{PrimaryFieldIndex}] [{PrimaryScanMin}..{PrimaryScanMax}]");
        }
        else
        {
            sb.Append($"PK scan [{PrimaryScanMin}..{PrimaryScanMax}]");
        }

        if (Descending)
        {
            sb.Append(" DESC");
        }

        if (OrderedEvaluators is { Length: > 0 })
        {
            sb.Append(" → Filters:");
            for (var i = 0; i < OrderedEvaluators.Length; i++)
            {
                ref readonly var eval = ref OrderedEvaluators[i];
                sb.Append($" Field[{eval.FieldIndex}] {eval.CompareOp} {eval.Threshold}");
                if (EstimatedCounts != null && i < EstimatedCounts.Length)
                {
                    sb.Append($" (est: {EstimatedCounts[i]})");
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Specifies a field to order results by. Controls the PK iteration direction (ascending/descending).
/// </summary>
internal readonly struct OrderByField
{
    /// <summary>Index into <see cref="ComponentTable.IndexedFieldInfos"/>. -1 = order by PK.</summary>
    public readonly int FieldIndex;

    /// <summary>0 = first component, 1 = second component (for two-component views).</summary>
    public readonly byte ComponentTag;

    /// <summary>True for descending iteration order.</summary>
    public readonly bool Descending;

    public OrderByField(int fieldIndex, byte componentTag = 0, bool descending = false)
    {
        FieldIndex = fieldIndex;
        ComponentTag = componentTag;
        Descending = descending;
    }
}
