using System;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Builds an <see cref="ExecutionPlan"/> from evaluators and index statistics.
/// Selects the most selective secondary index (unique or AllowMultiple) as the primary scan stream when possible, falling back to a full PK index scan otherwise.
/// </summary>
internal class PlanBuilder
{
    public static readonly PlanBuilder Instance = new();

    private PlanBuilder() { }

    /// <summary>
    /// Builds a selectivity-ordered plan. Evaluators are reordered by ascending estimated cardinality so the most selective predicate is evaluated first
    /// (short-circuit optimization). Attempts to select a unique secondary index as the primary scan stream.
    /// </summary>
    public ExecutionPlan BuildPlan(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator) =>
        BuildPlanCore(evaluators, table, estimator, descending: false, orderByFieldIndex: int.MinValue,
            queryInstanceKind: 0, queryInstanceLocalId: 0, definitionSourceFile: null, definitionSourceLine: 0, definitionSourceMethod: null,
            executionSourceFile: null, executionSourceLine: 0, executionSourceMethod: null);

    /// <summary>
    /// Builds a plan with OrderBy support. Sets the iteration direction based on <paramref name="orderBy"/>.
    /// Secondary index selection is only used when OrderBy is by the same field as the primary predicate, or when OrderBy is by PK (falls back to PK scan).
    /// </summary>
    public ExecutionPlan BuildPlan(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator, OrderByField orderBy) =>
        BuildPlanCore(evaluators, table, estimator, descending: orderBy.Descending, orderByFieldIndex: orderBy.FieldIndex,
            queryInstanceKind: 0, queryInstanceLocalId: 0, definitionSourceFile: null, definitionSourceLine: 0, definitionSourceMethod: null,
            executionSourceFile: null, executionSourceLine: 0, executionSourceMethod: null);

    /// <summary>
    /// Builds a plan with full Query-Definition-Export attribution (#342). Issuer supplies:
    /// <list type="bullet">
    /// <item>(kind, localId) identifying the View or EcsQuery instance for trace dedup.</item>
    /// <item>Definition-site source info (where the user *declared* the query).</item>
    /// <item>Execution-site source info (where the user *triggered* this execution).</item>
    /// </list>
    /// When the issuer omits identity (kind/localId == 0), the plan still builds correctly — the trace events simply
    /// carry zeros for the new fields, equivalent to legacy callers.
    /// </summary>
    public ExecutionPlan BuildPlanAttributed(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator, OrderByField? orderBy,
        byte queryInstanceKind, uint queryInstanceLocalId,
        string definitionSourceFile, int definitionSourceLine, string definitionSourceMethod,
        string executionSourceFile, int executionSourceLine, string executionSourceMethod) =>
        BuildPlanCore(evaluators, table, estimator,
            descending: orderBy?.Descending ?? false,
            orderByFieldIndex: orderBy?.FieldIndex ?? int.MinValue,
            queryInstanceKind, queryInstanceLocalId,
            definitionSourceFile, definitionSourceLine, definitionSourceMethod,
            executionSourceFile, executionSourceLine, executionSourceMethod);

    private static ExecutionPlan BuildPlanCore(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator,
        bool descending, int orderByFieldIndex,
        byte queryInstanceKind, uint queryInstanceLocalId,
        string definitionSourceFile, int definitionSourceLine, string definitionSourceMethod,
        string executionSourceFile, int executionSourceLine, string executionSourceMethod)
    {
        // ── Step 1: build the plan first so the descriptor can carry the real PrimaryIndexFieldIdx ──
        // Previously the descriptor was emitted before plan resolution with primaryIndexFieldIdx=-1, forcing the Workbench catalog to render every query as
        // "no index scan". The build is pure computation (no telemetry emission yet) so reordering doesn't affect trace event ordering — the
        // QueryDefinitionDescribe still lands BEFORE BeginQueryPlan opens its span.
        var (ordered, estimates) = OrderBySelectivity(evaluators, table, estimator);
        var plan = BuildPlanWithPrimarySelection(ordered, estimates, table, descending, orderByFieldIndex);

        // ── Step 2: emit QueryDefinitionDescribe (first time per identity) BEFORE the QueryPlan span ──
        // The dedup tracker inside EmitQueryDefinitionDescribe ensures at most one descriptor per (kind, localId) per session. Carries the resolved primary
        // index from the plan.
        if (queryInstanceLocalId != 0)
        {
            EmitDefinitionDescribe(evaluators, table, queryInstanceKind, queryInstanceLocalId,
                definitionSourceFile, definitionSourceLine, definitionSourceMethod, orderByFieldIndex, descending,
                primaryIndexFieldIdx: (short)plan.PrimaryFieldIndex);
        }

        // ── Step 3: intern execution-site strings on the producer thread, get IDs for the QueryPlanEvent ──
        var executionFileId = QuerySourceStringInterner.Intern(executionSourceFile);
        var executionMethodId = QuerySourceStringInterner.Intern(executionSourceMethod);

        // ── Step 4: open the QueryPlan span with the full attributed payload ──
        var planScope = TyphonEvent.BeginQueryPlan(
            (byte)Math.Min(evaluators.Length, byte.MaxValue), 0, long.MinValue, long.MaxValue);
        try
        {
            planScope.IndexFieldIdx = (ushort)Math.Max(0, plan.PrimaryFieldIndex);
            planScope.RangeMin = plan.PrimaryScanMin;
            planScope.RangeMax = plan.PrimaryScanMax;
            // v9 extension fields: identity + execution-site. Always set, even when zero — the optMask
            // bits ride along so the decoder knows the trace carries v9-shape records.
            planScope.QueryInstanceKind = queryInstanceKind;
            planScope.QueryInstanceLocalId = queryInstanceLocalId;
            planScope.ExecutionSourceFileId = executionFileId;
            planScope.ExecutionSourceLine = executionSourceLine;
            planScope.ExecutionSourceMethodId = executionMethodId;

            // ── Step 5: emit QueryArgs after the plan opens, when there are evaluators with thresholds ──
            // Encoded inline from the ordered evaluators' Threshold field (already 8-byte widened).
            if (ordered.Length > 0)
            {
                EmitArgs(ordered);
            }

            return plan;
        }
        finally
        {
            planScope.Dispose();
        }
    }

    /// <summary>
    /// Emit a <see cref="Typhon.Profiler.TraceEventKind.QueryDefinitionDescribe"/> event for the given identity. Internal so the View pipeline
    /// (<c>EcsView.Refresh</c>) can emit directly — Views don't run through <c>BuildPlanCore</c>, but the descriptor still needs to land in the trace.
    /// Dedup is handled by the underlying <see cref="QueryDefinitionDescribeTracker"/> — subsequent calls per (kind, localId) are no-ops.
    /// </summary>
    internal static unsafe void EmitDefinitionDescribe(FieldEvaluator[] evaluators, ComponentTable table, byte queryInstanceKind, uint queryInstanceLocalId,
        string definitionSourceFile, int definitionSourceLine, string definitionSourceMethod, int orderByFieldIndex, bool descending,
        short primaryIndexFieldIdx = -1, ushort targetComponentTypeOverride = 0)
    {
        // Intern definition-site strings on the consumer-bound interner. Empty input → id 0 (sentinel).
        var defFileId = QuerySourceStringInterner.Intern(definitionSourceFile);
        var defMethodId = QuerySourceStringInterner.Intern(definitionSourceMethod);

        // Build the evaluator-shape blob: 4 bytes per evaluator (u16 fieldIdx + u8 op + u8 reserved).
        // Stack-allocate up to 64 evaluators (256 bytes); fallback to heap above that (never expected).
        var evCount = evaluators.Length;
        Span<byte> evScratch = evCount <= 64
            ? stackalloc byte[evCount * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize]
            : new byte[evCount * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize];
        for (var i = 0; i < evCount; i++)
        {
            var off = i * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(evScratch[off..], evaluators[i].FieldIndex);
            evScratch[off + 2] = (byte)evaluators[i].CompareOp;
            evScratch[off + 3] = 0;  // reserved pad
        }

        // FieldDependencies: derived from the evaluator set (the fields this query reads). For the descriptor
        // we capture the evaluator fields as the read set — this is the structural query shape. The Workbench
        // can resolve detailed field-dependency tracking by joining with the View/EcsQuery's runtime metadata
        // if needed in later phases.
        Span<byte> depScratch = evCount <= 64
            ? stackalloc byte[evCount * QueryDefinitionDescribeEventCodec.FieldDependencyEntrySize]
            : new byte[evCount * QueryDefinitionDescribeEventCodec.FieldDependencyEntrySize];
        for (var i = 0; i < evCount; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                depScratch[(i * QueryDefinitionDescribeEventCodec.FieldDependencyEntrySize)..], evaluators[i].FieldIndex);
        }

        // SortFieldIdx / SortDescending are surfaced from the BuildPlan caller's orderByFieldIndex. int.MinValue
        // / -1 sentinels map to -1 on the wire (no sort / sort by PK). Drop noise at the wire: -1 explicitly.
        short sortFieldIdx = orderByFieldIndex == int.MinValue ? (short)-1 : (short)orderByFieldIndex;

        // TargetComponentType: prefer the explicit override (callers who know it from a type parameter
        // pass it directly), else resolve at runtime via ArchetypeRegistry. Defensive default 0 if the
        // table doesn't have a registered POCOType (test fixtures may construct synthetic tables).
        ushort targetComponentType = targetComponentTypeOverride;
        if (targetComponentType == 0 && table?.Definition?.POCOType != null)
        {
            var typeId = ArchetypeRegistry.GetComponentTypeId(table.Definition.POCOType);
            if (typeId is > 0 and <= ushort.MaxValue)
            {
                targetComponentType = (ushort)typeId;
            }
        }

        // PrimaryIndexFieldIdx is supplied by the caller (when the plan has been built) or -1 when the
        // caller can't yet know the optimizer's choice (e.g., EcsView.Refresh emitting a descriptor for
        // an EcsQuery whose plan was built before the profiler gate opened). -1 means "no primary scan
        // resolved" on the wire and the Workbench renders the catalog row accordingly.
        TyphonEvent.EmitQueryDefinitionDescribe(queryInstanceKind, queryInstanceLocalId, targetComponentType, primaryIndexFieldIdx,
            sortFieldIdx, descending ? (byte)1 : (byte)0, defFileId, definitionSourceLine, defMethodId,
            evScratch, depScratch);
    }

    private static void EmitArgs(FieldEvaluator[] ordered)
    {
        var count = ordered.Length;
        // Stack-allocate up to 64 thresholds (512 bytes); fallback above.
        Span<byte> thresholds = count <= 64
            ? stackalloc byte[count * QueryArgsEventCodec.ThresholdSize]
            : new byte[count * QueryArgsEventCodec.ThresholdSize];
        for (var i = 0; i < count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(thresholds[(i * QueryArgsEventCodec.ThresholdSize)..], ordered[i].Threshold);
        }
        TyphonEvent.EmitQueryArgs(thresholds);
    }

    private static ExecutionPlan BuildPlanWithPrimarySelection(FieldEvaluator[] orderedEvaluators, long[] estimates, ComponentTable table, bool descending,
        int orderByFieldIndex = int.MinValue)
    {
        // Try to find a secondary index for the primary stream
        var (primaryFieldIndex, primaryKeyType, scanMin, scanMax) = SelectPrimaryStream(orderedEvaluators, table, orderByFieldIndex);

        // Phase 7: Query:Plan:PrimarySelect instant — fires once per BuildPlan, after the candidate decision is made.
        // candidates = total evaluator count, winnerIdx = chosen field idx (or 0xFF if PK fallback), reason: 0 = secondary-index, 1 = PK fallback.
        TyphonEvent.EmitQueryPlanPrimarySelect(
            (byte)Math.Min(orderedEvaluators.Length, byte.MaxValue),
            (byte)(primaryFieldIndex < 0 ? 0xFF : Math.Min(primaryFieldIndex, byte.MaxValue)),
            (byte)(primaryFieldIndex < 0 ? 1 : 0));

        if (primaryFieldIndex < 0)
        {
            // Fall back to PK scan — use full long range so the plan remains valid
            // when reused after new entities are inserted (e.g., overflow recovery).
            scanMin = long.MinValue;
            scanMax = long.MaxValue;
        }

        return new ExecutionPlan(primaryFieldIndex, primaryKeyType, scanMin, scanMax, descending, orderedEvaluators, estimates);
    }

    /// <summary>
    /// Selects the most selective secondary index as the primary scan stream.
    /// Only considers operators that can narrow a range (not NE).
    /// </summary>
    /// <param name="orderedEvaluators">Evaluators sorted by ascending selectivity.</param>
    /// <param name="table">Component table with index metadata.</param>
    /// <param name="orderByFieldIndex">
    /// When set (not int.MinValue), only select a secondary index if it matches this field index.
    /// Prevents using a secondary index when OrderBy requires a different iteration order.
    /// int.MinValue = no OrderBy constraint, -1 = OrderBy PK (forces PK scan).
    /// </param>
    private static (int FieldIndex, KeyType KeyType, long ScanMin, long ScanMax) SelectPrimaryStream(FieldEvaluator[] orderedEvaluators, ComponentTable table,
        int orderByFieldIndex)
    {
        // OrderBy PK → must use PK scan
        if (orderByFieldIndex == -1)
        {
            return (-1, default, 0, 0);
        }

        var indexedFieldInfos = table.IndexedFieldInfos;

        for (var i = 0; i < orderedEvaluators.Length; i++)
        {
            ref var eval = ref orderedEvaluators[i];

            // NE cannot narrow a range
            if (eval.CompareOp == CompareOp.NotEqual)
            {
                continue;
            }

            // Must reference a valid indexed field
            if (eval.FieldIndex >= indexedFieldInfos.Length)
            {
                continue;
            }

            ref var ifi = ref indexedFieldInfos[eval.FieldIndex];

            // If OrderBy is specified, only select this field if it matches
            if (orderByFieldIndex != int.MinValue && orderByFieldIndex != eval.FieldIndex)
            {
                continue;
            }

            // Empty index → no benefit
            if (ifi.Index.EntryCount == 0)
            {
                continue;
            }

            // Use type-appropriate max/min for unbounded ranges so the plan remains valid when reused after new keys are inserted (e.g., overflow recovery).
            // long.MaxValue/MinValue cannot be used because LongToKey truncates to the target type (e.g., (int)long.MaxValue = -1), creating invalid scan ranges.
            var typeMin = TypeMinAsLong(eval.KeyType);
            var typeMax = TypeMaxAsLong(eval.KeyType);
            var isInteger = IsIntegerKeyType(eval.KeyType);
            var (scanMin, scanMax) = ComputeBounds(ref eval, typeMin, typeMax, isInteger);

            // Merge bounds from additional evaluators on the same field (e.g., B >= 5 && B < 15 → intersect ranges).
            var selectedFieldIndex = eval.FieldIndex;
            var selectedKeyType = eval.KeyType;
            for (var j = i + 1; j < orderedEvaluators.Length; j++)
            {
                ref var other = ref orderedEvaluators[j];
                if (other.FieldIndex != selectedFieldIndex || other.CompareOp == CompareOp.NotEqual)
                {
                    continue;
                }

                var (otherMin, otherMax) = ComputeBounds(ref other, typeMin, typeMax, isInteger);
                // Intersect: tighten both bounds
                if (otherMin > scanMin)
                {
                    scanMin = otherMin;
                }

                if (otherMax < scanMax)
                {
                    scanMax = otherMax;
                }
            }

            return (selectedFieldIndex, selectedKeyType, scanMin, scanMax);
        }

        return (-1, default, 0, 0);
    }

    private static (FieldEvaluator[] Ordered, long[] Estimates) OrderBySelectivity(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator)
    {
        if (evaluators.Length == 0)
        {
            return ([], []);
        }

        // Phase 7: Query:Plan:Sort span — wraps the cardinality-estimate + insertion-sort pass.
        var sortStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var sortScope = TyphonEvent.BeginQueryPlanSort((byte)Math.Min(evaluators.Length, byte.MaxValue), 0);
        try
        {

            // Copy evaluators and estimate cardinality in a single pass
            var ordered = new FieldEvaluator[evaluators.Length];
            var estimates = new long[evaluators.Length];
            for (var i = 0; i < evaluators.Length; i++)
            {
                ordered[i] = evaluators[i];
                ref var eval = ref ordered[i];
                estimates[i] = estimator.EstimateCardinality(table, eval.FieldIndex, eval.CompareOp, eval.Threshold);
            }

            // Insertion sort by ascending cardinality, tie-break by lower FieldIndex.
            // Optimal for typical predicate counts (1-3), avoids delegate allocation from Array.Sort.
            for (var i = 1; i < ordered.Length; i++)
            {
                var keyEval = ordered[i];
                var keyEst = estimates[i];
                var j = i - 1;
                while (j >= 0 && (estimates[j] > keyEst || (estimates[j] == keyEst && ordered[j].FieldIndex > keyEval.FieldIndex)))
                {
                    ordered[j + 1] = ordered[j];
                    estimates[j + 1] = estimates[j];
                    j--;
                }
                ordered[j + 1] = keyEval;
                estimates[j + 1] = keyEst;
            }

            var sortNs = (uint)Math.Min((System.Diagnostics.Stopwatch.GetTimestamp() - sortStart) * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency, uint.MaxValue);
            sortScope.SortNs = sortNs;
            return (ordered, estimates);
        }
        finally
        {
            sortScope.Dispose();
        }
    }

    private static bool IsIntegerKeyType(KeyType kt) => kt is KeyType.Bool or KeyType.Byte or KeyType.SByte or KeyType.Short or 
                                                        KeyType.UShort or KeyType.Int or KeyType.UInt or KeyType.Long or KeyType.ULong;

    private static long TypeMaxAsLong(KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool: return 1L;
            case KeyType.SByte: return sbyte.MaxValue;
            case KeyType.Byte: return byte.MaxValue;
            case KeyType.Short: return short.MaxValue;
            case KeyType.UShort: return ushort.MaxValue;
            case KeyType.Int: return int.MaxValue;
            case KeyType.UInt: return uint.MaxValue;
            case KeyType.Long: return long.MaxValue;
            case KeyType.ULong: return unchecked((long)ulong.MaxValue);
            case KeyType.Float:
            {
                var f = float.MaxValue;
                return Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = double.MaxValue;
                return Unsafe.As<double, long>(ref d);
            }
            default: return long.MaxValue;
        }
    }

    private static long TypeMinAsLong(KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool: return 0L;
            case KeyType.SByte: return sbyte.MinValue;
            case KeyType.Byte: return 0L;
            case KeyType.Short: return short.MinValue;
            case KeyType.UShort: return 0L;
            case KeyType.Int: return int.MinValue;
            case KeyType.UInt: return 0L;
            case KeyType.Long: return long.MinValue;
            case KeyType.ULong: return 0L;
            case KeyType.Float:
            {
                var f = float.MinValue;
                return Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = double.MinValue;
                return Unsafe.As<double, long>(ref d);
            }
            default: return long.MinValue;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (long Min, long Max) ComputeBounds(ref FieldEvaluator eval, long typeMin, long typeMax, bool isInteger) => eval.CompareOp switch
    {
        CompareOp.Equal => (eval.Threshold, eval.Threshold),
        CompareOp.GreaterThan => (isInteger ? eval.Threshold + 1 : eval.Threshold, typeMax),
        CompareOp.GreaterThanOrEqual => (eval.Threshold, typeMax),
        CompareOp.LessThan => (typeMin, isInteger ? eval.Threshold - 1 : eval.Threshold),
        CompareOp.LessThanOrEqual => (typeMin, eval.Threshold),
        _ => (typeMin, typeMax)
    };
}
