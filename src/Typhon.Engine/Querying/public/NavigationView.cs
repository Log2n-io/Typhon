using System;
using System.Runtime.CompilerServices;
using Typhon.Engine.internals;

namespace Typhon.Engine;

/// <summary>
/// Navigation join view: tracks source entities whose FK-referenced target satisfies predicates.
/// Registered in both source and target ViewRegistries for incremental refresh.
/// </summary>
/// <remarks>
/// <para>
/// Ring buffer semantics differ from a single-entity multi-component <see cref="EcsView{TArchetype}"/>:
/// componentTag=0 entries carry the source entity PK, componentTag=1 entries carry the target entity PK (a different entity).
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
    private readonly int _fkFieldIndex;      // Index into the source definition's field list — matched against delta entries' fieldIndex
    private readonly int _fkFieldOffset;     // Byte offset of FK field within source component struct

    // Position of the FK field among the source component's INDEXED fields. Distinct from _fkFieldIndex above, which is a definition field index.
    // Indexes both IndexedFieldInfos[o] and, for a cluster-backed archetype, IndexSlots[s].Fields[o].
    private readonly int _fkFieldOrdinal;

    // The refresh's FK lookups at its snapshot (#1247): built at the first lookup a refresh makes, released when it ends.
    private FkSnapshot _fkSnapshot;

    // Which archetypes can hold the source component, split by index home. Resolved ONCE here rather than per lookup: ReverseLookupAndUpdate runs per
    // target delta entry, so anything built inside it is rebuilt on every fan-out (#662 AC: no allocation per target PK).
    private readonly FkCandidates _candidates;

    internal NavigationView(FieldEvaluator[] sourceEvaluators, FieldEvaluator[] targetEvaluators, ComponentTable sourceTable, ComponentTable targetTable,
        int fkFieldIndex, int fkFieldOffset, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0, string sourceFile = null, 
        int sourceLine = 0, string sourceMethod = null) :
        base(CombineEvaluators(sourceEvaluators, targetEvaluators), [], sourceTable.DBE.MemoryAllocator, sourceTable, bufferCapacity, baseTSN, sourceFile, 
            sourceLine, sourceMethod)
    {
        _sourceTable = sourceTable;
        _targetTable = targetTable;
        _sourceEvaluators = sourceEvaluators;
        _targetEvaluators = targetEvaluators;
        _sourceEvalLookup = BuildEvaluatorLookup(sourceEvaluators);
        _targetEvalLookup = BuildEvaluatorLookup(targetEvaluators);
        _fkFieldIndex = fkFieldIndex;
        _fkFieldOffset = fkFieldOffset;
        _fkFieldOrdinal = PipelineExecutor.FindFKIndexOrdinal(sourceTable, fkFieldOffset);
        _candidates = FkReverseLookup.ResolveCandidates(sourceTable.DBE, ArchetypeRegistry.GetComponentTypeId<TSource>());
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
                RoutingId = _sourceTable.DBE.RoutingIdOf(meta),
                View = this,
                Tx = tx,
                Map = engineState.EntityMap,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref collector);
            accessor.Dispose();
        }
    }

    private struct SourceCollector : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public RawValuePagedHashMap<long, PersistentStore> Map;
        public ushort RoutingId;
        public NavigationView<TSource, TTarget> View;
        public Transaction Tx;

        public bool Process(long key, byte* value)
        {
            if (!EntityVisibility.IsVisibleInScan(value, Tx.TSN, Tx.DBE.CommitCards, Map, key))
            {
                return true;
            }

            var entityId = new EntityId(key, RoutingId);
            var sourcePK = (long)entityId.RawValue;

            if (View.EvaluateFullPredicate(sourcePK, Tx))
            {
                View.AddEntityDirect(sourcePK);
            }

            return true;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A NavigationView publishes into BOTH its source and target registries, so it is exposed to the same use-after-free as every other view
    /// (#864). Without this override <c>ViewBase.BufferReclaimer</c> returns null, <c>Retire</c> takes its no-reclaimer branch and frees the block
    /// inline — which after the removal of <c>Thread.SpinWait(100)</c> would be strictly worse than the behaviour it replaced.
    /// </remarks>
    private protected override ViewBufferReclaimer BufferReclaimer => _sourceTable?.DBE?.ViewBufferReclaimer;

    /// <inheritdoc/>
    protected override void DeregisterFromRegistries()
    {
        _sourceTable.ViewRegistry.DeregisterView(this);
        _targetTable.ViewRegistry.DeregisterView(this);
    }

    /// <inheritdoc/>
    public override void Refresh(
        Transaction tx,
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site; consumed by trace emission in P2 (issue #335).
        _ = callerFile; _ = callerLine; _ = callerMethod;

        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(NavigationView<,>));
        }

        try
        {
            if (NeedsRefreshFull)
            {
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
        finally
        {
            _fkSnapshot?.Dispose();
            _fkSnapshot = null;
        }
    }

    /// <summary>
    /// The sources whose foreign key names a target AT THE REFRESH'S SNAPSHOT (#1247, VIS-13), committed state only: the HEAD tree held the newest
    /// foreign keys, so a refresh at an older snapshot fanned a target's change out to sources re-keyed onto it since, and missed ones re-keyed away or
    /// destroyed since. Built once per refresh.
    /// </summary>
    private FkSnapshot SourcesAt(Transaction tx) => _fkSnapshot ??= new FkSnapshot(tx, in _candidates, _fkFieldOrdinal, includeOwn: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntry(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, byte componentTag, Transaction tx)
    {
        // An enable carries no key (NotifyViewsForEnableDisable): the source is re-evaluated from what it reads, a target's sources are fanned out to.
        if (isCreation && entry.AfterKey.IsZero)
        {
            var pk = (long)entry.EntityPK.RawValue;
            if (componentTag == 0)
            {
                ApplyMembership(pk, EvaluateFullPredicate(pk, tx));
            }
            else
            {
                ReverseLookupAndUpdate(pk, tx);
            }

            return;
        }

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
        // _entityIds is keyed on the raw EntityId value (PopulateFromEntityMaps, ReverseLookupAndUpdate) — convert once here.
        var sourcePK = (long)entry.EntityPK.RawValue;
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
    /// Same boundary-crossing pattern as a single-component field-predicate change, but the target is read via the FK value.
    /// </summary>
    private void ProcessSourcePredicateChange(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        var sourcePK = (long)entry.EntityPK.RawValue;

        // Find the evaluator for this field
        ref var eval = ref FindSourceEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        // Membership from the view's own set, not the entry's BeforeKey — the writer's read value, stale under concurrent writers (step 1.5).
        var wasInView = _entityIds.Contains(sourcePK);
        var shouldPass = !isDeletion && SourceFieldPasses(fieldIndex, ref entry.AfterKey);

        if (wasInView)
        {
            // IN: stays while this predicate passes, leaves when it fails
            ApplyDelta(sourcePK, true, shouldPass);
        }
        else if (shouldPass && CheckOtherSourceFieldsAndTarget(sourcePK, fieldIndex, tx))
        {
            // OUT→IN: verify all other predicates (source + target via FK)
            ApplyDelta(sourcePK, false, true);
        }
    }

    /// <summary>
    /// Handles target field changes — reverse navigation with fan-out.
    /// When a target field crosses a boundary, all source entities pointing to that target via FK must be re-evaluated.
    /// </summary>
    private void ProcessTargetFieldChange(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        var targetPK = (long)entry.EntityPK.RawValue;

        // Find the target evaluator for this field
        ref var eval = ref FindTargetEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        // Check boundary crossing on every target evaluator of the field (VDELTA-01): two for a range. A deletion always crosses: a destroy's or a
        // disable's entry
        // carries no key to say the target was in, and the reverse lookup decides from what it reads.
        var targetWasIn = !isCreation && (isDeletion || TargetFieldPasses(fieldIndex, ref entry.BeforeKey));
        var targetIsIn = !isDeletion && TargetFieldPasses(fieldIndex, ref entry.AfterKey);

        if (targetWasIn == targetIsIn)
        {
            // No boundary crossing on target — skip expensive reverse lookup
            // But if target is still qualifying, mark affected sources as Modified
            if (targetIsIn)
            {
                MarkSourcesModified(targetPK, tx);
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
        if (!tx.TryReadCommitted<TSource>(sourcePK, false, out var sourceComp))
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
        if (!tx.TryReadCommitted<TTarget>(fkValue, false, out var targetComp))
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
        if (!tx.TryReadCommitted<TSource>(sourcePK, false, out var sourceComp))
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

        if (!tx.TryReadCommitted<TTarget>(fkValue, false, out var targetComp))
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

        if (!tx.TryReadCommitted<TTarget>(targetPK, false, out var targetComp))
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
        if (!tx.TryReadCommitted<TSource>(sourcePK, false, out var sourceComp))
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

        var action = new ApplyDeltaAction { View = this, Tx = tx, TargetPasses = targetPasses };
        FkReverseLookup.ForEachSource(_sourceTable.DBE, _sourceTable, in _candidates, _fkFieldOrdinal, targetPK, ref action, SourcesAt(tx));
    }

    /// <summary>Applies the source-side view transition for each entity found by the reverse lookup.</summary>
    private struct ApplyDeltaAction : IFkSourceAction
    {
        public NavigationView<TSource, TTarget> View;
        public Transaction Tx;
        public bool TargetPasses;

        public bool Process(long sourcePK, ArchetypeMetadata meta)
        {
            var wasInView = View._entityIds.Contains(sourcePK);
            var shouldBeInView = TargetPasses && View.EvaluateSourcePredicates(sourcePK, Tx);
            View.ApplyDelta(sourcePK, wasInView, shouldBeInView);
            return true;
        }
    }

    /// <summary>
    /// Marks all source entities pointing to a target as Modified (target field changed but didn't cross boundary).
    /// </summary>
    private void MarkSourcesModified(long targetPK, Transaction tx)
    {
        var action = new MarkModifiedAction { View = this };
        FkReverseLookup.ForEachSource(_sourceTable.DBE, _sourceTable, in _candidates, _fkFieldOrdinal, targetPK, ref action, SourcesAt(tx));
    }

    /// <summary>Marks each already-present source entity Modified.</summary>
    private struct MarkModifiedAction : IFkSourceAction
    {
        public NavigationView<TSource, TTarget> View;

        public bool Process(long sourcePK, ArchetypeMetadata meta)
        {
            if (View._entityIds.Contains(sourcePK))
            {
                View.CompactDelta(sourcePK, DeltaKind.Modified);
            }
            return true;
        }
    }

    private void RefreshFull(Transaction tx)
    {
        var oldEntities = _entityIds.Clone();

        var droppedBefore = BeginRefreshFull();

        // Rebuilt from storage, as the builder seeds it. It cleared the set and computed the deltas from nothing, so an overflow emptied the view.
        _entityIds.Clear();
        PopulateFromEntityMaps(tx);
        ComputeRefreshFullDeltas(oldEntities);

        // The entries the re-scan may have missed (a commit between its delta and its value) are folded, not discarded: see EndRefreshFull.
        while (DeltaBuffer.TryPeek(tx.TSN, out var entry, out var flags, out var tsn, out var componentTag))
        {
            var fold = FoldsHead;   // an entry appended before a drop that may have superseded it: the re-scan stands for its entity
            DeltaBuffer.Advance();
            if (fold)
            {
                ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, componentTag, tx);
            }

            SetLastRefreshTSN(tsn);
        }

        EndRefreshFull(droppedBefore);

        SetOverflowDetected(false);
        SetLastRefreshTSN(tx.TSN);
    }

    /// <summary>Whether <paramref name="key"/> passes every source evaluator of <paramref name="fieldIndex"/> — two for a range on one field.</summary>
    private bool SourceFieldPasses(int fieldIndex, ref KeyBytes8 key)
    {
        for (var i = 0; i < _sourceEvaluators.Length; i++)
        {
            if (_sourceEvaluators[i].FieldIndex == fieldIndex && !EvaluateKey(ref _sourceEvaluators[i], ref key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="key"/> passes every target evaluator of <paramref name="fieldIndex"/>.</summary>
    private bool TargetFieldPasses(int fieldIndex, ref KeyBytes8 key)
    {
        for (var i = 0; i < _targetEvaluators.Length; i++)
        {
            if (_targetEvaluators[i].FieldIndex == fieldIndex && !EvaluateKey(ref _targetEvaluators[i], ref key))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EvaluateKey(ref FieldEvaluator eval, ref KeyBytes8 key)
    {
        // A stack copy: the evaluator takes a pointer, and a pointer may address only the stack or engine memory, wherever the entry lives.
        var local = key;
        return FieldEvaluator.Evaluate(ref eval, (byte*)&local);
    }

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
