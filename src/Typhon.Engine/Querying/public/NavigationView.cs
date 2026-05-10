using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Navigation join view: tracks source entities whose FK-referenced target satisfies predicates.
/// Registered in both source and target ViewRegistries for incremental refresh.
/// </summary>
/// <remarks>
/// <para>
/// Ring buffer semantics differ from <see cref="View{T1,T2}"/>:
/// componentTag=0 entries carry the source entity PK, componentTag=1 entries carry the target entity PK.
/// Forward navigation (source FK changes or source predicate changes) is 1:1.
/// Reverse navigation (target predicate changes) requires fan-out: one target change may affect many source entities.
/// </para>
/// </remarks>
public unsafe class NavigationView<TSource, TTarget> : ViewBase where TSource : unmanaged where TTarget : unmanaged
{
    private readonly ComponentTable _sourceTable;
    private readonly ComponentTable _targetTable;
    private readonly FieldEvaluator[] _sourceEvaluators;
    private readonly FieldEvaluator[] _targetEvaluators;
    private readonly int[] _sourceEvalLookup;
    private readonly int[] _targetEvalLookup;
    private readonly int _fkFieldIndex;      // Index into source's IndexedFieldInfos for the FK field
    private readonly int _fkFieldOffset;     // Byte offset of FK field within source component struct

    internal NavigationView(FieldEvaluator[] sourceEvaluators, FieldEvaluator[] targetEvaluators, ComponentTable sourceTable, ComponentTable targetTable,
        int fkFieldIndex, int fkFieldOffset, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(CombineEvaluators(sourceEvaluators, targetEvaluators), [], sourceTable.DBE.MemoryAllocator, sourceTable, bufferCapacity, baseTSN)
    {
        _sourceTable = sourceTable;
        _targetTable = targetTable;
        _sourceEvaluators = sourceEvaluators;
        _targetEvaluators = targetEvaluators;
        _sourceEvalLookup = BuildEvaluatorLookup(sourceEvaluators);
        _targetEvalLookup = BuildEvaluatorLookup(targetEvaluators);
        _fkFieldIndex = fkFieldIndex;
        _fkFieldOffset = fkFieldOffset;
    }

    /// <summary>
    /// Populates the initial entity set by scanning all archetype EntityMaps that contain TSource.
    /// For each visible source entity, evaluates the full predicate (source fields + FK → target fields).
    /// </summary>
    internal void PopulateFromEntityMaps(Transaction tx)
    {
        var sourceTypeId = ArchetypeRegistry.GetComponentTypeId<TSource>();
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.TryGetSlot(sourceTypeId, out _))
            {
                continue;
            }

            var engineState = _sourceTable.DBE._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null)
            {
                continue;
            }

            using var scanGuard = EpochGuard.Enter(_sourceTable.DBE.EpochManager);
            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var collector = new SourceCollector
            {
                ArchetypeId = meta.ArchetypeId,
                View = this,
                Tx = tx,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref collector);
            accessor.Dispose();
        }
    }

    private struct SourceCollector : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ushort ArchetypeId;
        public NavigationView<TSource, TTarget> View;
        public Transaction Tx;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);
            if (!header.IsVisibleAt(Tx.TSN))
            {
                return true;
            }

            var entityId = new EntityId(key, ArchetypeId);
            var sourcePK = (long)entityId.RawValue;

            if (View.EvaluateFullPredicate(sourcePK, Tx))
            {
                View.AddEntityDirect(sourcePK);
            }

            return true;
        }
    }

    protected override void DeregisterFromRegistries()
    {
        _sourceTable.ViewRegistry.DeregisterView(this);
        _targetTable.ViewRegistry.DeregisterView(this);
    }

    public override void Refresh(Transaction tx)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(NavigationView<,>));
        }

        if (DeltaBuffer.HasOverflow)
        {
            SetOverflowDetected(true);
            RefreshFull(tx);
            return;
        }

        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out var componentTag))
        {
            DeltaBuffer.Advance();
            ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, componentTag, tx);
            SetLastRefreshTSN(tsn);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntry(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, byte componentTag, Transaction tx)
    {
        if (componentTag == 0)
        {
            // Source component change
            if (fieldIndex == _fkFieldIndex)
            {
                // FK field changed — forward navigation
                ProcessFKChange(ref entry, isDeletion, tx);
            }
            else
            {
                // Source predicate field changed — boundary crossing on source
                ProcessSourcePredicateChange(ref entry, fieldIndex, isCreation, isDeletion, tx);
            }
        }
        else
        {
            // Target component change — reverse navigation (fan-out)
            ProcessTargetFieldChange(ref entry, fieldIndex, isCreation, isDeletion, tx);
        }
    }

    /// <summary>
    /// Handles FK field changes on a source entity. The entity now points to a different target.
    /// BeforeKey/AfterKey contain the old/new target PKs (as long values in KeyBytes8).
    /// </summary>
    private void ProcessFKChange(ref ViewDeltaEntry entry, bool isDeletion, Transaction tx)
    {
        var sourcePK = entry.EntityPK;
        var wasInView = _entityIds.Contains(sourcePK);

        if (isDeletion)
        {
            if (wasInView)
            {
                ApplyDelta(sourcePK, true, false);
            }
            return;
        }

        // Evaluate whether this source entity should now be in the view
        var shouldBeInView = EvaluateFullPredicate(sourcePK, tx);
        ApplyDelta(sourcePK, wasInView, shouldBeInView);
    }

    /// <summary>
    /// Handles source predicate field changes (non-FK fields on source component).
    /// Same boundary-crossing pattern as View&lt;T&gt;.ProcessMultiField, but target is read from FK value.
    /// </summary>
    private void ProcessSourcePredicateChange(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        var sourcePK = entry.EntityPK;

        // Find the evaluator for this field
        ref var eval = ref FindSourceEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        var wasInView = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
        var shouldPass = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

        if (wasInView == shouldPass)
        {
            // No boundary crossing — if entity is in view, mark Modified
            if (shouldPass && _entityIds.Contains(sourcePK))
            {
                CompactDelta(sourcePK, DeltaKind.Modified);
            }
            return;
        }

        if (!wasInView)
        {
            // OUT→IN: verify all other predicates (source + target via FK)
            if (CheckOtherSourceFieldsAndTarget(sourcePK, fieldIndex, tx))
            {
                ApplyDelta(sourcePK, _entityIds.Contains(sourcePK), true);
            }
        }
        else
        {
            // IN→OUT: remove if entity was in view
            if (_entityIds.Contains(sourcePK))
            {
                ApplyDelta(sourcePK, true, false);
            }
        }
    }

    /// <summary>
    /// Handles target field changes — reverse navigation with fan-out.
    /// When a target field crosses a boundary, all source entities pointing to that target via FK must be re-evaluated.
    /// </summary>
    private void ProcessTargetFieldChange(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        var targetPK = entry.EntityPK;

        // Find the target evaluator for this field
        ref var eval = ref FindTargetEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        // Check boundary crossing on the target evaluator
        var targetWasIn = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
        var targetIsIn = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

        if (targetWasIn == targetIsIn)
        {
            // No boundary crossing on target — skip expensive reverse lookup
            // But if target is still qualifying, mark affected sources as Modified
            if (targetIsIn)
            {
                MarkSourcesModified(targetPK);
            }
            return;
        }

        // Boundary crossing on target — must fan out to all source entities
        ReverseLookupAndUpdate(targetPK, tx);
    }

    /// <summary>
    /// Evaluates whether a source entity should be in the view by reading source, extracting FK, reading target, and evaluating all predicates.
    /// </summary>
    private bool EvaluateFullPredicate(long sourcePK, Transaction tx)
    {
        if (!tx.QueryRead<TSource>(sourcePK, out var sourceComp))
        {
            return false;
        }

        // Evaluate source predicates
        var sourcePtr = (byte*)Unsafe.AsPointer(ref sourceComp);
        for (var i = 0; i < _sourceEvaluators.Length; i++)
        {
            ref var eval = ref _sourceEvaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, sourcePtr + eval.FieldOffset))
            {
                return false;
            }
        }

        // Extract FK value
        var fkValue = *(long*)(sourcePtr + _fkFieldOffset);
        if (fkValue == 0)
        {
            return false;
        }

        // Read and evaluate target
        if (!tx.QueryRead<TTarget>(fkValue, out var targetComp))
        {
            return false;
        }

        if (_targetEvaluators.Length == 0)
        {
            return true;
        }

        var targetPtr = (byte*)Unsafe.AsPointer(ref targetComp);
        for (var i = 0; i < _targetEvaluators.Length; i++)
        {
            ref var eval = ref _targetEvaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, targetPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks all source predicates except the changed one, plus target predicates via FK.
    /// Used during source predicate boundary crossing (OUT→IN).
    /// </summary>
    private bool CheckOtherSourceFieldsAndTarget(long sourcePK, int changedFieldIndex, Transaction tx)
    {
        if (!tx.QueryRead<TSource>(sourcePK, out var sourceComp))
        {
            return false;
        }

        var sourcePtr = (byte*)Unsafe.AsPointer(ref sourceComp);

        // Check other source fields
        for (var i = 0; i < _sourceEvaluators.Length; i++)
        {
            if (_sourceEvaluators[i].FieldIndex == changedFieldIndex)
            {
                continue;
            }
            ref var eval = ref _sourceEvaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, sourcePtr + eval.FieldOffset))
            {
                return false;
            }
        }

        // Extract FK and check target
        var fkValue = *(long*)(sourcePtr + _fkFieldOffset);
        if (fkValue == 0)
        {
            return false;
        }

        if (!tx.QueryRead<TTarget>(fkValue, out var targetComp))
        {
            return false;
        }

        if (_targetEvaluators.Length == 0)
        {
            return true;
        }

        var targetPtr = (byte*)Unsafe.AsPointer(ref targetComp);
        for (var i = 0; i < _targetEvaluators.Length; i++)
        {
            ref var eval = ref _targetEvaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, targetPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates target predicates only for the given target PK. Used by fan-out to avoid redundant target reads.
    /// </summary>
    private bool EvaluateTargetPredicates(long targetPK, Transaction tx)
    {
        if (_targetEvaluators.Length == 0)
        {
            return true;
        }

        if (!tx.QueryRead<TTarget>(targetPK, out var targetComp))
        {
            return false;
        }

        var targetPtr = (byte*)Unsafe.AsPointer(ref targetComp);
        for (var i = 0; i < _targetEvaluators.Length; i++)
        {
            ref var eval = ref _targetEvaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, targetPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates source predicates only (no FK/target check). Used by fan-out when target is pre-evaluated.
    /// </summary>
    private bool EvaluateSourcePredicates(long sourcePK, Transaction tx)
    {
        if (!tx.QueryRead<TSource>(sourcePK, out var sourceComp))
        {
            return false;
        }

        var sourcePtr = (byte*)Unsafe.AsPointer(ref sourceComp);
        for (var i = 0; i < _sourceEvaluators.Length; i++)
        {
            ref var eval = ref _sourceEvaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, sourcePtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reverse lookup via FK index: finds all source entities that point to the given target PK, then re-evaluates each source entity's full predicate and updates the view.
    /// The target is read once and its predicates evaluated upfront — all sources in this fan-out share the same target.
    /// </summary>
    private void ReverseLookupAndUpdate(long targetPK, Transaction tx)
    {
        // Pre-evaluate target predicates once — all sources in this fan-out share the same target
        bool targetPasses = EvaluateTargetPredicates(targetPK, tx);

        var fkIndexInfo = PipelineExecutor.FindFKIndex(_sourceTable, _fkFieldOffset);
        var fkIndex = (BTree<long, PersistentStore>)fkIndexInfo.Index;
        var compRevAccessor = _sourceTable.CompRevTableSegment.CreateChunkAccessor();

        try
        {
            var enumerator = fkIndex.EnumerateRangeMultiple(targetPK, targetPK);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                            var sourcePK = header.EntityPK;

                            var wasInView = _entityIds.Contains(sourcePK);
                            var shouldBeInView = targetPasses && EvaluateSourcePredicates(sourcePK, tx);
                            ApplyDelta(sourcePK, wasInView, shouldBeInView);
                        }
                    } while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }
        finally
        {
            compRevAccessor.Dispose();
        }
    }

    /// <summary>
    /// Marks all source entities pointing to a target as Modified (target field changed but didn't cross boundary).
    /// </summary>
    private void MarkSourcesModified(long targetPK)
    {
        var fkIndexInfo = PipelineExecutor.FindFKIndex(_sourceTable, _fkFieldOffset);
        var fkIndex = (BTree<long, PersistentStore>)fkIndexInfo.Index;
        var compRevAccessor = _sourceTable.CompRevTableSegment.CreateChunkAccessor();

        try
        {
            var enumerator = fkIndex.EnumerateRangeMultiple(targetPK, targetPK);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                            var sourcePK = header.EntityPK;

                            if (_entityIds.Contains(sourcePK))
                            {
                                CompactDelta(sourcePK, DeltaKind.Modified);
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
        finally
        {
            compRevAccessor.Dispose();
        }
    }

    private void RefreshFull(Transaction tx)
    {
        var oldEntities = _entityIds.Clone();

        DeltaBuffer.Reset(tx.TSN);

        _entityIds.Clear();

        DrainBufferAfterRefreshFull(tx.TSN);
        ComputeRefreshFullDeltas(oldEntities);

        SetOverflowDetected(false);
        SetLastRefreshTSN(tx.TSN);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EvaluateKey(ref FieldEvaluator eval, ref KeyBytes8 key) => FieldEvaluator.Evaluate(ref eval, (byte*)Unsafe.AsPointer(ref key));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldEvaluator FindSourceEvaluator(int fieldIndex)
    {
        if ((uint)fieldIndex < (uint)_sourceEvalLookup.Length)
        {
            var idx = _sourceEvalLookup[fieldIndex];
            if (idx >= 0)
            {
                return ref _sourceEvaluators[idx];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldEvaluator FindTargetEvaluator(int fieldIndex)
    {
        if ((uint)fieldIndex < (uint)_targetEvalLookup.Length)
        {
            var idx = _targetEvalLookup[fieldIndex];
            if (idx >= 0)
            {
                return ref _targetEvaluators[idx];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }

    private static int[] BuildEvaluatorLookup(FieldEvaluator[] evaluators)
    {
        var maxField = -1;
        for (var i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex > maxField)
            {
                maxField = evaluators[i].FieldIndex;
            }
        }
        if (maxField < 0)
        {
            return [];
        }
        var lookup = new int[maxField + 1];
        Array.Fill(lookup, -1);
        for (var i = 0; i < evaluators.Length; i++)
        {
            lookup[evaluators[i].FieldIndex] = i;
        }
        return lookup;
    }

    private static FieldEvaluator[] CombineEvaluators(FieldEvaluator[] source, FieldEvaluator[] target)
    {
        var combined = new FieldEvaluator[source.Length + target.Length];
        Array.Copy(source, combined, source.Length);
        Array.Copy(target, 0, combined, source.Length, target.Length);
        return combined;
    }
}
