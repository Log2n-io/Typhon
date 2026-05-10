using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> by scanning secondary indexes and evaluating field predicates inline.
/// For Versioned components, walks the MVCC revision chain directly from the index value (no EntityMap re-lookup).
/// For SingleVersion components, reads component data and entityPK from the inline chunk overhead.
/// Predicates are ordered by ascending selectivity for short-circuit efficiency.
/// </summary>
internal class PipelineExecutor
{
    public static readonly PipelineExecutor Instance = new();

    private PipelineExecutor() { }

    /// <summary>
    /// Executes the plan and adds matching entity PKs to the caller-provided <paramref name="result"/> set (unordered).
    /// The caller owns the collection lifecycle — one-shot queries pass a fresh instance, Views reuse and clear theirs.
    /// </summary>
    /// <param name="plan">The execution plan built by <see cref="PlanBuilder"/>.</param>
    /// <param name="evaluators">All field evaluators, ordered by ascending selectivity (most selective first).</param>
    /// <param name="table">The component table to read entities from.</param>
    /// <param name="tx">Transaction for MVCC-consistent reads.</param>
    /// <param name="result">Caller-provided set to populate. Must be empty (or pre-cleared by caller).</param>
    public void Execute(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashMap<long> result) 
        => ExecuteCore(plan, evaluators, table, tx, result, null, 0, int.MaxValue);

    /// <summary>
    /// Executes the plan and adds matching entity PKs to the caller-provided <paramref name="result"/> list preserving iteration order.
    /// Supports Skip/Take with early termination.
    /// </summary>
    public void ExecuteOrdered(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result, int skip = 0,
        int take = int.MaxValue) => ExecuteCore(plan, evaluators, table, tx, null, result, skip, take);

    /// <summary>
    /// Counts matching entities without allocating a result collection. Runs the same scan + filter pipeline but only increments a counter.
    /// </summary>
    public int Count(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx)
    {
        // Phase 7: Query:Count span. ResultCount filled at exit.
        var countScope = TyphonEvent.BeginQueryCount();
        try
        {
            int result;
            if (plan.UsesSecondaryIndex)
            {
                result = CountCoreSecondaryIndex(plan, evaluators, table, tx);
            }
            else
            {
                // PK B+Tree removed — non-secondary-index count path returns 0.
                // All current callers (EcsQuery.Count via WhereField, EcsView.CountScan) use secondary indexes.
                result = 0;
            }
            countScope.ResultCount = result;
            return result;
        }
        finally
        {
            countScope.Dispose();
        }
    }

    /// <summary>
    /// Dispatches to the typed count method based on the primary key type and storage mode.
    /// </summary>
    private int CountCoreSecondaryIndex(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx)
    {
        // Phase 7: Query:Execute:IndexScan span — wraps the secondary-index dispatch path.
        var scanScope = TyphonEvent.BeginQueryExecuteIndexScan((ushort)Math.Max(0, plan.PrimaryFieldIndex), (byte)table.StorageMode);
        try
        {
            var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];

            if (table.StorageMode == StorageMode.Transient)
            {
                var compAccessor = table.TransientComponentSegment.CreateChunkAccessor();
                try
                {
                    return plan.PrimaryKeyType switch
                    {
                        KeyType.Byte   => CountPKsTypedNonVersioned((BTree<byte, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.SByte  => CountPKsTypedNonVersioned((BTree<sbyte, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.Short  => CountPKsTypedNonVersioned((BTree<short, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.UShort => CountPKsTypedNonVersioned((BTree<ushort, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.Int    => CountPKsTypedNonVersioned((BTree<int, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.UInt   => CountPKsTypedNonVersioned((BTree<uint, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.Long   => CountPKsTypedNonVersioned((BTree<long, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.ULong  => CountPKsTypedNonVersioned((BTree<long, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.Float  => CountPKsTypedNonVersioned((BTree<float, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        KeyType.Double => CountPKsTypedNonVersioned((BTree<double, TransientStore>)ifi.Index, plan, table, evaluators, ref compAccessor),
                        _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for Transient index scan")
                    };
                }
                finally
                {
                    compAccessor.Dispose();
                }
            }

            return plan.PrimaryKeyType switch
            {
                KeyType.Byte => CountPKsTyped((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.SByte => CountPKsTyped((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.Short => CountPKsTyped((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.UShort => CountPKsTyped((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.Int => CountPKsTyped((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.UInt => CountPKsTyped((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.Long => CountPKsTyped((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.ULong => CountPKsTyped((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.Float => CountPKsTyped((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                KeyType.Double => CountPKsTyped((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
                _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
            };
        }
        finally
        {
            scanScope.Dispose();
        }
    }

    private static int CountPKsTyped<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators, Transaction tx) where TKey : unmanaged
    {
        // SingleVersion: index values are component chunkIds — read data directly, no CompRevTable.
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            return CountPKsTypedSV(index, plan, table, evaluators);
        }

        // Transient: should not reach here — dispatched via CountPKsTypedTransient
        Debug.Assert(table.StorageMode != StorageMode.Transient);

        // Combined scan + chain walk + evaluate: the index value IS the compRevFirstChunkId.
        // Walk the revision chain directly from here — no EntityMap re-lookup (eliminates the
        // double CompRevTable walk that QueryRead would perform via GetCompRevInfoFromIndex).
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var count = 0;

        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();
        var compContentAccessor = hasFilters ? table.ComponentSegment.CreateChunkAccessor() : default;

        // Phase 7: Query:Execute:Iterate / Filter spans.
        var iterScope = TyphonEvent.BeginQueryExecuteIterate();
        var filterScope = TyphonEvent.BeginQueryExecuteFilter((byte)Math.Min(nonPrimaryEvals.Length, byte.MaxValue));
        try
        {
            if (index.AllowMultiple)
            {
                var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        if (TelemetryConfig.QueryExecuteIterateActive) iterScope.ChunkCount++;
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                if (TelemetryConfig.QueryExecuteIterateActive) iterScope.EntryCount++;
                                if (CountOneVersioned(values[j], ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx))
                                {
                                    count++;
                                }
                                else if (TelemetryConfig.QueryExecuteFilterActive)
                                {
                                    filterScope.RejectedCount++;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (TelemetryConfig.QueryExecuteIterateActive) { iterScope.ChunkCount++; iterScope.EntryCount++; }
                    if (CountOneVersioned(kv.Value, ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx))
                    {
                        count++;
                    }
                    else if (TelemetryConfig.QueryExecuteFilterActive)
                    {
                        filterScope.RejectedCount++;
                    }
                }
            }
        }
        finally
        {
            filterScope.Dispose();
            iterScope.Dispose();
            compRevAccessor.Dispose();
            if (hasFilters)
            {
                compContentAccessor.Dispose();
            }
        }

        return count;
    }

    /// <summary>
    /// Evaluate one Versioned entity from its compRevFirstChunkId: walk revision chain for MVCC visibility,
    /// optionally read component data and evaluate non-primary filters. No EntityMap lookup needed.
    /// </summary>
    private static unsafe bool CountOneVersioned(int compRevFirstChunkId, ref ChunkAccessor<PersistentStore> compRevAccessor,
        ref ChunkAccessor<PersistentStore> compContentAccessor, ComponentTable table, FieldEvaluator[] nonPrimaryEvals, bool hasFilters, Transaction tx)
    {
        if (!hasFilters)
        {
            // Primary-only: lightweight MVCC visibility check via EntityMap (no chain walk needed).
            // Read entityPK from CompRevStorageHeader, then check IsEntityVisible.
            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
            return tx.IsEntityVisible(header.EntityPK);
        }

        // Non-primary filters present: walk revision chain to resolve CurCompContentChunkId for field evaluation.
        var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, tx.TSN);
        if (chainResult.IsFailure || chainResult.Value.CurCompContentChunkId == 0)
        {
            return false;
        }

        byte* ptr = compContentAccessor.GetChunkAddress(chainResult.Value.CurCompContentChunkId) + table.ComponentOverhead;
        for (var i = 0; i < nonPrimaryEvals.Length; i++)
        {
            ref var eval = ref nonPrimaryEvals[i];
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// SV-specific count: iterates the secondary index directly, reading component data from chunkIds
    /// without CompRevTable resolution (SV has no revision chains). For primary-only queries,
    /// counts index entries directly. For non-primary filters, reads component data from the chunk.
    /// </summary>
    private static int CountPKsTypedSV<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var count = 0;

        // For SV, component data is read directly from the component segment (no revision chain).
        var compAccessor = table.ComponentSegment.CreateChunkAccessor();

        // Phase 7: Query:Execute:Iterate / Filter spans — SV count path.
        var iterScope = TyphonEvent.BeginQueryExecuteIterate();
        var filterScope = TyphonEvent.BeginQueryExecuteFilter((byte)Math.Min(nonPrimaryEvals.Length, byte.MaxValue));
        try
        {
            if (index.AllowMultiple)
            {
                var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        if (TelemetryConfig.QueryExecuteIterateActive) iterScope.ChunkCount++;
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                if (TelemetryConfig.QueryExecuteIterateActive) iterScope.EntryCount++;
                                if (!hasFilters || EvaluateFiltersSV(nonPrimaryEvals, table, values[j], ref compAccessor))
                                {
                                    count++;
                                }
                                else if (TelemetryConfig.QueryExecuteFilterActive)
                                {
                                    filterScope.RejectedCount++;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (TelemetryConfig.QueryExecuteIterateActive) { iterScope.ChunkCount++; iterScope.EntryCount++; }
                    if (!hasFilters || EvaluateFiltersSV(nonPrimaryEvals, table, kv.Value, ref compAccessor))
                    {
                        count++;
                    }
                    else if (TelemetryConfig.QueryExecuteFilterActive)
                    {
                        filterScope.RejectedCount++;
                    }
                }
            }
        }
        finally
        {
            filterScope.Dispose();
            iterScope.Dispose();
            compAccessor.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Evaluates non-primary field predicates directly on SV component data.
    /// No MVCC resolution — SV writes are in-place, index entries are current.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool EvaluateFiltersSV(FieldEvaluator[] evaluators, ComponentTable table, int chunkId, ref ChunkAccessor<PersistentStore> compAccessor)
    {
        byte* ptr = compAccessor.GetChunkAddress(chunkId) + table.ComponentOverhead;
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Filters out evaluators whose FieldIndex matches the primary scan field — those conditions are already guaranteed by the B+Tree range scan.
    /// Returns the original array if no filtering needed.
    /// </summary>
    private static FieldEvaluator[] ComputeNonPrimaryEvaluators(FieldEvaluator[] evaluators, int primaryFieldIndex)
    {
        if (primaryFieldIndex < 0 || evaluators.Length == 0)
        {
            return evaluators;
        }

        int nonPrimaryCount = 0;
        for (int i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex != primaryFieldIndex)
            {
                nonPrimaryCount++;
            }
        }

        if (nonPrimaryCount == evaluators.Length)
        {
            return evaluators; // No primary evaluators to filter
        }

        if (nonPrimaryCount == 0)
        {
            return []; // All evaluators covered by index scan
        }

        var result = new FieldEvaluator[nonPrimaryCount];
        int j = 0;
        for (int i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex != primaryFieldIndex)
            {
                result[j++] = evaluators[i];
            }
        }
        return result;
    }

    private void ExecuteCore(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashMap<long> unorderedResult,
        List<long> orderedResult, int skip, int take)
    {
        if (take == 0)
        {
            return;
        }

        if (plan.UsesSecondaryIndex)
        {
            ExecuteCoreSecondaryIndex(plan, evaluators, table, tx, unorderedResult, orderedResult, skip, take);
        }
    }

    /// <summary>
    /// Secondary index scan path: scans a secondary index (unique or AllowMultiple) for matching key values, recovers entity PKs via
    /// <see cref="CompRevStorageHeader.EntityPK"/>, then evaluates remaining predicates via component reads.
    /// </summary>
    private void ExecuteCoreSecondaryIndex(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashMap<long> unorderedResult, List<long> orderedResult, int skip, int take)
    {
        // SingleVersion: combined scan+evaluate in one pass (no QueryRead, no CompRevTable).
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            ExecuteCoreSecondaryIndexSV(plan, evaluators, table, unorderedResult, orderedResult, skip, take);
            return;
        }

        // Transient: same pattern as SV but using TransientStore accessors.
        if (table.StorageMode == StorageMode.Transient)
        {
            ExecuteCoreSecondaryIndexTransient(plan, evaluators, table, unorderedResult, orderedResult, skip, take);
            return;
        }

        // Combined scan + chain walk + evaluate: same optimization as Count —
        // walk revision chain directly from index value, skip EntityMap re-lookup.
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];

        switch (plan.PrimaryKeyType)
        {
            case KeyType.Byte:   ExecutePKsTypedVersioned((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.SByte:  ExecutePKsTypedVersioned((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Short:  ExecutePKsTypedVersioned((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UShort: ExecutePKsTypedVersioned((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Int:    ExecutePKsTypedVersioned((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UInt:   ExecutePKsTypedVersioned((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Long:   ExecutePKsTypedVersioned((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.ULong:  ExecutePKsTypedVersioned((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Float:  ExecutePKsTypedVersioned((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Double: ExecutePKsTypedVersioned((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for Versioned index scan");
        }
    }

    /// <summary>
    /// Versioned combined Execute: iterates index, walks revision chain directly from compRevFirstChunkId (no EntityMap re-lookup),
    /// evaluates non-primary filters on the resolved component data, collects entity PKs.
    /// </summary>
    private static void ExecutePKsTypedVersioned<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table,
        FieldEvaluator[] evaluators, Transaction tx, HashMap<long> unorderedResult, List<long> orderedResult, int skip, int take) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var collected = 0;

        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();
        var compContentAccessor = hasFilters ? table.ComponentSegment.CreateChunkAccessor() : default;

        // Phase 7: Query:Execute:Iterate / Filter / Pagination spans — Versioned Execute path.
        // Filter:RejectedCount tracks entries rejected by MVCC visibility or non-primary filters.
        // Pagination:EarlyTerm = 1 when collected >= take triggers an early return.
        var iterScope = TyphonEvent.BeginQueryExecuteIterate();
        var filterScope = TyphonEvent.BeginQueryExecuteFilter((byte)Math.Min(nonPrimaryEvals.Length, byte.MaxValue));
        var pageScope = TyphonEvent.BeginQueryExecutePagination(skip, take);
        try
        {
            if (index.AllowMultiple)
            {
                var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        if (TelemetryConfig.QueryExecuteIterateActive) iterScope.ChunkCount++;
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                if (TelemetryConfig.QueryExecuteIterateActive) iterScope.EntryCount++;
                                var collectedBefore = collected;
                                if (ExecuteOneVersioned(values[j], ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx,
                                        unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                                {
                                    if (TelemetryConfig.QueryExecutePaginationActive) pageScope.EarlyTerm = 1;
                                    return;
                                }
                                if (collected == collectedBefore && TelemetryConfig.QueryExecuteFilterActive)
                                {
                                    filterScope.RejectedCount++;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (TelemetryConfig.QueryExecuteIterateActive) { iterScope.ChunkCount++; iterScope.EntryCount++; }
                    var collectedBefore = collected;
                    if (ExecuteOneVersioned(kv.Value, ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx,
                            unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                    {
                        if (TelemetryConfig.QueryExecutePaginationActive) pageScope.EarlyTerm = 1;
                        return;
                    }
                    if (collected == collectedBefore && TelemetryConfig.QueryExecuteFilterActive)
                    {
                        filterScope.RejectedCount++;
                    }
                }
            }
        }
        finally
        {
            pageScope.Dispose();
            filterScope.Dispose();
            iterScope.Dispose();
            compRevAccessor.Dispose();
            if (hasFilters)
            {
                compContentAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Process one Versioned entity for Execute: chain walk, MVCC check, filter evaluation, PK collection.
    /// Returns true if entity was collected (for take limit tracking).
    /// </summary>
    private static unsafe bool ExecuteOneVersioned(int compRevFirstChunkId, ref ChunkAccessor<PersistentStore> compRevAccessor,
        ref ChunkAccessor<PersistentStore> compContentAccessor, ComponentTable table, FieldEvaluator[] nonPrimaryEvals, bool hasFilters, Transaction tx,
        HashMap<long> unorderedResult, List<long> orderedResult, ref int skip, ref int collected)
    {
        // Read entityPK from CompRevStorageHeader
        ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
        long entityPK = header.EntityPK;

        if (!hasFilters)
        {
            // Primary-only: lightweight MVCC visibility check via EntityMap (no chain walk needed)
            if (!tx.IsEntityVisible(entityPK))
            {
                return false;
            }
        }
        else
        {
            // Non-primary filters: walk chain to resolve component data for field evaluation
            var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, tx.TSN);
            if (chainResult.IsFailure || chainResult.Value.CurCompContentChunkId == 0)
            {
                return false;
            }

            byte* ptr = compContentAccessor.GetChunkAddress(chainResult.Value.CurCompContentChunkId) + table.ComponentOverhead;
            for (var i = 0; i < nonPrimaryEvals.Length; i++)
            {
                ref var eval = ref nonPrimaryEvals[i];
                if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
                {
                    return false;
                }
            }
        }

        if (skip > 0) { skip--; return false; }

        if (unorderedResult != null) { unorderedResult.TryAdd(entityPK); }
        else { orderedResult?.Add(entityPK); }

        collected++;
        return true;
    }

    /// <summary>
    /// SV-specific secondary index Execute: dispatches to typed method for index iteration.
    /// Combines scan + evaluate + collect in one pass — no QueryRead, no CompRevTable.
    /// </summary>
    private void ExecuteCoreSecondaryIndexSV(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, HashMap<long> unorderedResult, 
        List<long> orderedResult, int skip, int take)
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];

        switch (plan.PrimaryKeyType)
        {
            case KeyType.Byte:   ExecutePKsTypedSV((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.SByte:  ExecutePKsTypedSV((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Short:  ExecutePKsTypedSV((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UShort: ExecutePKsTypedSV((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Int:    ExecutePKsTypedSV((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UInt:   ExecutePKsTypedSV((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Long:   ExecutePKsTypedSV((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.ULong:  ExecutePKsTypedSV((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Float:  ExecutePKsTypedSV((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Double: ExecutePKsTypedSV((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for SV index scan");
        }
    }

    /// <summary>
    /// Transient secondary index Execute: dispatches to typed generic non-versioned method using TransientStore accessors.
    /// </summary>
    private void ExecuteCoreSecondaryIndexTransient(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, HashMap<long> unorderedResult,
        List<long> orderedResult, int skip, int take)
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];
        var compAccessor = table.TransientComponentSegment.CreateChunkAccessor();
        try
        {
            switch (plan.PrimaryKeyType)
            {
                case KeyType.Byte:   ExecutePKsTypedNonVersioned((BTree<byte, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.SByte:  ExecutePKsTypedNonVersioned((BTree<sbyte, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.Short:  ExecutePKsTypedNonVersioned((BTree<short, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.UShort: ExecutePKsTypedNonVersioned((BTree<ushort, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.Int:    ExecutePKsTypedNonVersioned((BTree<int, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.UInt:   ExecutePKsTypedNonVersioned((BTree<uint, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.Long:   ExecutePKsTypedNonVersioned((BTree<long, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.ULong:  ExecutePKsTypedNonVersioned((BTree<long, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.Float:  ExecutePKsTypedNonVersioned((BTree<float, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                case KeyType.Double: ExecutePKsTypedNonVersioned((BTree<double, TransientStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take, ref compAccessor); break;
                default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for Transient index scan");
            }
        }
        finally
        {
            compAccessor.Dispose();
        }
    }

    /// <summary>
    /// SV typed Execute: iterates index range, reads entityPK from inline chunk overhead (offset 0), evaluates non-primary filters from component data,
    /// collects matching PKs.
    /// </summary>
    private static unsafe void ExecutePKsTypedSV<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, 
        FieldEvaluator[] evaluators, HashMap<long> unorderedResult, List<long> orderedResult, int skip, int take) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var collected = 0;

        var compAccessor = table.ComponentSegment.CreateChunkAccessor();

        // Phase 7: Query:Execute:Iterate / Filter / Pagination spans — SV Execute path.
        var iterScope = TyphonEvent.BeginQueryExecuteIterate();
        var filterScope = TyphonEvent.BeginQueryExecuteFilter((byte)Math.Min(nonPrimaryEvals.Length, byte.MaxValue));
        var pageScope = TyphonEvent.BeginQueryExecutePagination(skip, take);
        try
        {
            if (index.AllowMultiple)
            {
                var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        if (TelemetryConfig.QueryExecuteIterateActive) iterScope.ChunkCount++;
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                if (TelemetryConfig.QueryExecuteIterateActive) iterScope.EntryCount++;
                                if (hasFilters && !EvaluateFiltersSV(nonPrimaryEvals, table, values[j], ref compAccessor))
                                {
                                    if (TelemetryConfig.QueryExecuteFilterActive) filterScope.RejectedCount++;
                                    continue;
                                }

                                long entityPK = *(long*)compAccessor.GetChunkAddress(values[j]);
                                if (skip > 0)
                                {
                                    skip--; continue;
                                }

                                if (unorderedResult != null)
                                {
                                    unorderedResult.TryAdd(entityPK);
                                }
                                else
                                {
                                    orderedResult?.Add(entityPK);
                                }

                                if (++collected >= take)
                                {
                                    if (TelemetryConfig.QueryExecutePaginationActive) pageScope.EarlyTerm = 1;
                                    return;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (TelemetryConfig.QueryExecuteIterateActive) { iterScope.ChunkCount++; iterScope.EntryCount++; }
                    if (hasFilters && !EvaluateFiltersSV(nonPrimaryEvals, table, kv.Value, ref compAccessor))
                    {
                        if (TelemetryConfig.QueryExecuteFilterActive) filterScope.RejectedCount++;
                        continue;
                    }

                    long entityPK = *(long*)compAccessor.GetChunkAddress(kv.Value);
                    if (skip > 0)
                    {
                        skip--; continue;
                    }

                    if (unorderedResult != null)
                    {
                        unorderedResult.TryAdd(entityPK);
                    }
                    else
                    {
                        orderedResult?.Add(entityPK);
                    }

                    if (++collected >= take)
                    {
                        if (TelemetryConfig.QueryExecutePaginationActive) pageScope.EarlyTerm = 1;
                        return;
                    }
                }
            }
        }
        finally
        {
            pageScope.Dispose();
            filterScope.Dispose();
            iterScope.Dispose();
            compAccessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Generic non-versioned (SV + Transient) Count/Execute — parameterized on TStore
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Non-versioned count: iterates a secondary index, reading component data from chunkIds directly.
    /// No CompRevTable, no MVCC — works for both SV (PersistentStore) and Transient (TransientStore).
    /// </summary>
    private static int CountPKsTypedNonVersioned<TKey, TStore>(BTree<TKey, TStore> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators, 
        ref ChunkAccessor<TStore> compAccessor) where TKey : unmanaged where TStore : struct, IPageStore
    {
        var minKey = BTree<TKey, TStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, TStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var count = 0;

        // Phase 7: Query:Execute:Iterate / Filter spans — Transient/non-versioned count path.
        var iterScope = TyphonEvent.BeginQueryExecuteIterate();
        var filterScope = TyphonEvent.BeginQueryExecuteFilter((byte)Math.Min(nonPrimaryEvals.Length, byte.MaxValue));
        try
        {
            if (index.AllowMultiple)
            {
                var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        if (TelemetryConfig.QueryExecuteIterateActive) iterScope.ChunkCount++;
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                if (TelemetryConfig.QueryExecuteIterateActive) iterScope.EntryCount++;
                                if (!hasFilters || EvaluateFiltersNonVersioned(nonPrimaryEvals, table, values[j], ref compAccessor))
                                {
                                    count++;
                                }
                                else if (TelemetryConfig.QueryExecuteFilterActive)
                                {
                                    filterScope.RejectedCount++;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (TelemetryConfig.QueryExecuteIterateActive) { iterScope.ChunkCount++; iterScope.EntryCount++; }
                    if (!hasFilters || EvaluateFiltersNonVersioned(nonPrimaryEvals, table, kv.Value, ref compAccessor))
                    {
                        count++;
                    }
                    else if (TelemetryConfig.QueryExecuteFilterActive)
                    {
                        filterScope.RejectedCount++;
                    }
                }
            }
        }
        finally
        {
            filterScope.Dispose();
            iterScope.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Non-versioned Execute: iterates index range, reads entityPK from inline chunk overhead, collects matching PKs. Works for both SV and Transient store types.
    /// </summary>
    private static unsafe void ExecutePKsTypedNonVersioned<TKey, TStore>(BTree<TKey, TStore> index, ExecutionPlan plan, ComponentTable table,
        FieldEvaluator[] evaluators, HashMap<long> unorderedResult, List<long> orderedResult, int skip, int take,
        ref ChunkAccessor<TStore> compAccessor) where TKey : unmanaged where TStore : struct, IPageStore
    {
        var minKey = BTree<TKey, TStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, TStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var collected = 0;

        // Phase 7: Query:Execute:Iterate / Filter / Pagination spans — Transient/non-versioned Execute path.
        var iterScope = TyphonEvent.BeginQueryExecuteIterate();
        var filterScope = TyphonEvent.BeginQueryExecuteFilter((byte)Math.Min(nonPrimaryEvals.Length, byte.MaxValue));
        var pageScope = TyphonEvent.BeginQueryExecutePagination(skip, take);
        try
        {
            if (index.AllowMultiple)
            {
                var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        if (TelemetryConfig.QueryExecuteIterateActive) iterScope.ChunkCount++;
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                if (TelemetryConfig.QueryExecuteIterateActive) iterScope.EntryCount++;
                                if (hasFilters && !EvaluateFiltersNonVersioned(nonPrimaryEvals, table, values[j], ref compAccessor))
                                {
                                    if (TelemetryConfig.QueryExecuteFilterActive) filterScope.RejectedCount++;
                                    continue;
                                }

                                long entityPK = *(long*)compAccessor.GetChunkAddress(values[j]);
                                if (skip > 0)
                                {
                                    skip--; continue;
                                }

                                if (unorderedResult != null)
                                {
                                    unorderedResult.TryAdd(entityPK);
                                }
                                else
                                {
                                    orderedResult?.Add(entityPK);
                                }

                                if (++collected >= take)
                                {
                                    if (TelemetryConfig.QueryExecutePaginationActive) pageScope.EarlyTerm = 1;
                                    return;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (TelemetryConfig.QueryExecuteIterateActive) { iterScope.ChunkCount++; iterScope.EntryCount++; }
                    if (hasFilters && !EvaluateFiltersNonVersioned(nonPrimaryEvals, table, kv.Value, ref compAccessor))
                    {
                        if (TelemetryConfig.QueryExecuteFilterActive) filterScope.RejectedCount++;
                        continue;
                    }

                    long entityPK = *(long*)compAccessor.GetChunkAddress(kv.Value);
                    if (skip > 0)
                    {
                        skip--; continue;
                    }

                    if (unorderedResult != null)
                    {
                        unorderedResult.TryAdd(entityPK);
                    }
                    else
                    {
                        orderedResult?.Add(entityPK);
                    }

                    if (++collected >= take)
                    {
                        if (TelemetryConfig.QueryExecutePaginationActive) pageScope.EarlyTerm = 1;
                        return;
                    }
                }
            }
        }
        finally
        {
            pageScope.Dispose();
            filterScope.Dispose();
            iterScope.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool EvaluateFiltersNonVersioned<TStore>(FieldEvaluator[] evaluators, ComponentTable table, int chunkId,
        ref ChunkAccessor<TStore> compAccessor) where TStore : struct, IPageStore
    {
        byte* ptr = compAccessor.GetChunkAddress(chunkId) + table.ComponentOverhead;
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool EvaluateFilters<T>(FieldEvaluator[] evaluators, Transaction tx, long pk) where T : unmanaged
    {
        if (!tx.QueryRead<T>(pk, out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    #region Navigation overloads
    // Two-component overloads (ExecuteOrderedTwo, ExecuteCoreTwo, etc.) removed — dead code after ECS migration.
    // The ECS API uses .Where<T>(Func<T, bool>) for secondary component filtering, which does a broad scan with
    // QueryRead<T> per entity. It never called the PipelineExecutor two-component path.


    /// <summary>
    /// Finds the IndexedFieldInfo for the FK field by matching its offset.
    /// </summary>
    internal static IndexedFieldInfo FindFKIndex(ComponentTable ct, int fkFieldOffset)
    {
        var expectedOffset = ct.ComponentOverhead + fkFieldOffset;

        for (var i = 0; i < ct.IndexedFieldInfos.Length; i++)
        {
            if (ct.IndexedFieldInfos[i].OffsetToField == expectedOffset)
            {
                return ct.IndexedFieldInfos[i];
            }
        }

        throw new InvalidOperationException("FK field index not found. Ensure the FK field has [Index(AllowMultiple = true)].");
    }

    #endregion
}
