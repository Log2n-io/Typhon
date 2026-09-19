using Typhon.Engine.Internals;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using System.Diagnostics;
using Typhon.Engine.internals;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// Reactive ECS View with incremental refresh via <see cref="ViewDeltaRingBuffer"/>.
/// Inherits <see cref="ViewBase"/> for entity set management, delta tracking, and ring buffer lifecycle.
/// When FieldEvaluators are present (Expression-based WHERE), registers with <see cref="ViewRegistry"/> for push-based delta notifications.
/// Otherwise, falls back to pull-model (full re-query on each Refresh).
/// </summary>
[PublicAPI]
public unsafe class EcsView<TArchetype> : ViewBase where TArchetype : class
{
    /// <summary>The archetype this view queries — see <see cref="ViewBase.QueriedArchetypeId"/> for why the runtime needs it.</summary>
    internal override ushort QueriedArchetypeId => ArchetypeRegistry.GetMetadata<TArchetype>()?.ArchetypeId ?? ushort.MaxValue;

    private EcsQuery<TArchetype> _query;
    private readonly DatabaseEngine _dbe;
    // Null for pull/membership views by design; shared paths must use _dbe rather than assuming an indexed-field ComponentTable exists.
    private readonly ComponentTable _componentTable;
    private readonly ViewRegistry _registry;

    /// <summary>
    /// The archetype membership channels this view subscribes to, or null when it is not membership-eligible (#790).
    /// </summary>
    /// <remarks>
    /// An array, not one registry: <c>Query&lt;TRoot&gt;()</c> matches the whole archetype subtree, and <c>.With</c>/<c>.Without</c>
    /// narrow that to a fixed SET of archetypes. Membership is the union of their live sets, so the view subscribes to each one.
    /// </remarks>
    private ArchetypeMembershipRegistry[] _membershipRegistries;

    /// <summary>
    /// The per-archetype structural epochs this view has accounted for, parallel to <see cref="_membershipRegistries"/>.
    /// </summary>
    /// <remarks>
    /// Per-archetype, not a sum. A sum is read non-atomically across k counters, so a read that straddles a bump on one archetype and a
    /// later read of another can total to exactly the recorded value while entries sit undrained — the gate then reports "nothing changed"
    /// over a commit that is visible at the reader's snapshot. Monotonicity of each term does not save the sum, because the reads are not
    /// simultaneous. Comparing element-wise has the same O(k) cost and no such window.
    /// </remarks>
    private long[] _lastEpochs;


    /// <summary>
    /// Set when the view's entity set may not correspond to any single consistent snapshot, so the next refresh must re-query rather than
    /// apply deltas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set at subscription, which is what restores the self-healing the pull path had. <c>RefreshPull</c> re-executed the query at the
    /// REFRESH transaction's TSN every time, so any error in the seed corrected itself on the next tick. The channel never re-executes, so
    /// without this the seed is permanent — and the seed is taken at the CREATING transaction's fixed TSN, with that transaction's
    /// uncommitted spawns folded in. Two consequences, both reproduced before this flag existed: a commit landing between that snapshot and
    /// the subscription was in neither the scan nor the buffer and was lost for the life of the view; and a creating transaction that rolled
    /// back left phantom ids in the set forever.
    /// </para>
    /// <para>
    /// Also set when a resync could not be proven clean — see <c>RefreshMembershipResync</c>.
    /// </para>
    /// </remarks>
    private bool _needsResync;
    private readonly int[] _evaluatorLookup;

    // Typed delegate for reading component data + evaluating fields (captures component type T at construction)
    private readonly EcsViewFieldReader _fieldReader;

    // Reusable scratch list for pull-mode refresh removals (avoids per-refresh allocation)
    private List<long> _pullRemoveScratch;

    // OR branch state (null for single-branch / pull mode)
    private readonly FieldEvaluator[][] _branchEvaluators;
    private readonly int[][] _branchEvalLookup;
    private readonly Dictionary<long, ushort> _branchBitmaps;
    private bool IsOrMode => _branchEvaluators != null;

    /// <summary>Incremental mode with cached execution plan.</summary>
    internal EcsView(EcsQuery<TArchetype> query, FieldEvaluator[] evaluators, ComponentTable componentTable,
        EcsViewFieldReader fieldReader, ExecutionPlan plan,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0,
        string sourceFile = null, int sourceLine = 0, string sourceMethod = null) :
        base(evaluators, BuildFieldDependencies(evaluators), componentTable.DBE.MemoryAllocator, componentTable, [plan], bufferCapacity, baseTSN,
            sourceFile, sourceLine, sourceMethod)
    {
        _query = query;
        _query.DetachTransaction();
        _dbe = componentTable.DBE;
        _componentTable = componentTable;
        _registry = componentTable.ViewRegistry;
        _evaluatorLookup = BuildEvaluatorLookup(evaluators);
        _fieldReader = fieldReader;
    }

    /// <summary>OR mode: multiple branches with per-entity branch bitmaps.</summary>
    internal EcsView(EcsQuery<TArchetype> query, FieldEvaluator[][] branchEvaluators, ExecutionPlan[] plans, ComponentTable componentTable,
        EcsViewFieldReader fieldReader, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0,
        string sourceFile = null, int sourceLine = 0, string sourceMethod = null) :
        base(FlattenEvaluators(branchEvaluators), BuildFieldDependenciesMulti(branchEvaluators), componentTable.DBE.MemoryAllocator,
            componentTable, plans, bufferCapacity, baseTSN, sourceFile, sourceLine, sourceMethod)
    {
        if (branchEvaluators.Length > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(branchEvaluators),
                $"OR views support at most 16 branches (got {branchEvaluators.Length}). Branch bitmaps use ushort.");
        }

        _query = query;
        _query.DetachTransaction();
        _dbe = componentTable.DBE;
        _componentTable = componentTable;
        _registry = componentTable.ViewRegistry;
        _fieldReader = fieldReader;
        _branchEvaluators = branchEvaluators;
        _branchEvalLookup = BuildBranchEvalLookup(branchEvaluators);
        _branchBitmaps = new Dictionary<long, ushort>();
    }

    /// <summary>Pull mode: created without FieldEvaluators (opaque WHERE or no WHERE).</summary>
    internal EcsView(EcsQuery<TArchetype> query, DatabaseEngine dbe, IMemoryAllocator allocator, IResource resourceParent,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0, string sourceFile = null, int sourceLine = 0,
        string sourceMethod = null)
        : base([], [], allocator, resourceParent, bufferCapacity, baseTSN, sourceFile, sourceLine, sourceMethod)
    {
        _query = query;
        _query.DetachTransaction();
        _dbe = dbe;
        _reclaimerFromQuery = (resourceParent as ComponentTable)?.DBE?.ViewBufferReclaimer;
    }

    /// <summary>
    /// Subscribe this view to its archetype's whole-membership channel (#790). Called from <c>EcsQuery.ToPullView</c> for archetype-only
    /// queries, BEFORE the initial population — registering after it would leave a window in which a concurrent commit's entities reach
    /// neither the population scan nor the buffer.
    /// </summary>
    internal void SubscribeToMembership(ArchetypeMembershipRegistry[] registries)
    {
        _membershipRegistries = registries;
        _lastEpochs = new long[registries.Length];
        // The first refresh re-queries regardless, so the epochs recorded here are a placeholder, not a claim. Anything that lands between
        // now and then is repaired by that re-query rather than gated away — see _needsResync.
        _needsResync = true;
        for (var i = 0; i < registries.Length; i++)
        {
            registries[i].Register(this, DeltaBuffer);
        }
        ReadEpochsInto(_lastEpochs);
        IsMembershipEligible = true;
    }

    /// <summary>Snapshots each subscribed archetype's structural epoch into <paramref name="into"/>.</summary>
    private void ReadEpochsInto(long[] into)
    {
        var regs = _membershipRegistries;
        for (var i = 0; i < regs.Length; i++)
        {
            into[i] = regs[i].StructuralEpoch;
        }
    }



    /// <summary>True when no subscribed archetype's epoch has moved since <see cref="_lastEpochs"/> was recorded.</summary>
    private bool EpochsUnchanged()
    {
        var regs = _membershipRegistries;
        for (var i = 0; i < regs.Length; i++)
        {
            if (regs[i].StructuralEpoch != _lastEpochs[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc/>
    private protected override ViewBufferReclaimer BufferReclaimer => _componentTable?.DBE?.ViewBufferReclaimer ?? _reclaimerFromQuery;

    /// <summary>
    /// Reclaimer captured at construction, for the pull/membership shape whose <c>_componentTable</c> is null.
    /// </summary>
    /// <remarks>
    /// Set ONLY by that constructor. The incremental and OR constructors both set <c>_componentTable</c>, so the left operand of
    /// <see cref="BufferReclaimer"/> always wins for them — assigning this there as well was dead, and it pulled the engine's reclaimer
    /// allocation onto every view construction rather than the far rarer disposal.
    /// </remarks>
    private readonly ViewBufferReclaimer _reclaimerFromQuery;

    /// <summary>Removes this view from its query registry so it stops receiving change notifications; invoked during teardown.</summary>
    protected override void DeregisterFromRegistries()
    {
        _registry?.DeregisterView(this);
        if (_membershipRegistries != null)
        {
            for (var i = 0; i < _membershipRegistries.Length; i++)
            {
                _membershipRegistries[i].Deregister(this);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EntityId convenience API (converts from internal long representation)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Test if an entity is currently in the View.</summary>
    public bool Contains(EntityId id) => Contains((long)id.RawValue);

    // EntityId-based delta caches (rebuilt on each Refresh for backward compat)
    private readonly List<EntityId> _addedCache = new();
    private readonly List<EntityId> _removedCache = new();

    /// <summary>True if any entities were added or removed since the last Refresh.</summary>
    public bool HasChanges => _addedCache.Count > 0 || _removedCache.Count > 0;

    /// <summary>Entities that entered the View since the last Refresh.</summary>
    public IReadOnlyList<EntityId> Added => _addedCache;

    /// <summary>Entities that left the View since the last Refresh.</summary>
    public IReadOnlyList<EntityId> Removed => _removedCache;

    /// <summary>Build EntityId caches from ViewBase's internal delta dictionary.</summary>
    private void BuildEntityIdCaches()
    {
        var delta = GetDelta();
        foreach (var pk in delta.Added)
        {
            _addedCache.Add(EntityId.FromRaw(pk));
        }
        foreach (var pk in delta.Removed)
        {
            _removedCache.Add(EntityId.FromRaw(pk));
        }
    }

    /// <summary>Iterate EntityIds in the view.</summary>
    public EntityIdEnumerator GetEntityEnumerator() => new(GetEnumerator());

    /// <summary>Enumerator that wraps HashMap&lt;long&gt;.Enumerator and yields EntityId. Ref struct (HashMap enumerator is ref struct).</summary>
    [PublicAPI]
    public ref struct EntityIdEnumerator
    {
        private HashMap<long>.Enumerator _inner;

        internal EntityIdEnumerator(HashMap<long>.Enumerator inner) => _inner = inner;

        /// <summary>Returns this enumerator, enabling <c>foreach</c> directly over it.</summary>
        public EntityIdEnumerator GetEnumerator() => this;

        /// <summary>The <see cref="EntityId"/> at the current position.</summary>
        public EntityId Current => EntityId.FromRaw(_inner.Current);

        /// <summary>Advances to the next entity; returns <see langword="false"/> when the view is exhausted.</summary>
        public bool MoveNext() => _inner.MoveNext();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drain the ring buffer up to the transaction's snapshot TSN, evaluate field predicates, and update the entity set and delta tracking.
    /// Falls back to full re-query when no FieldEvaluators are present (pull mode).
    /// </summary>
    /// <summary>
    /// P2 of umbrella #342 — emit a QueryDefinitionDescribe for this view's owning EcsQuery. Called per system-tick from the runtime; the producer-side tracker
    /// dedups so only the first invocation per (Kind, LocalId) actually writes to the trace. Pull-mode views (no evaluators / no component table)
    /// still emit with an empty evaluator shape — the catalog row shows the EcsQuery's source and target archetype.
    /// </summary>
    internal override void EmitDescriptorIfNeeded()
    {
        // When a cached plan is present (ECS-built incremental views) we know the resolved primary-index field; pass it through so the catalog renders the real
        // index instead of "—".
        var primaryIdx = HasCachedPlan ? (short)ExecutionPlan.PrimaryFieldIndex : (short)-1;
        // Resolve the archetype's id directly from its metadata so pull-mode views (no _componentTable) still carry a meaningful TargetComponentType on the
        // catalog row. ArchetypeMetadata's ArchetypeId is the same identifier the Workbench Schema panel keys archetypes on.
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var targetComponentType = meta?.ArchetypeId ?? 0;
        PlanBuilder.EmitDefinitionDescribe(Evaluators, _componentTable, 1, (uint)_query.EcsQueryId, _query.SourceFile, _query.SourceLine, _query.SourceMethod,
            int.MinValue, false, primaryIdx, targetComponentType);
    }

    /// <summary>
    /// Per-tick QueryPlan span for system-input views (#342, follow-up to P7). Pull-mode views never go through
    /// <c>PlanBuilder.BuildPlan</c> at consumption time so the Workbench Execution Inspector would otherwise stay
    /// permanently empty. The runtime captures the system's start/end timestamps and calls this hook from
    /// <c>OnSystemEndInternal</c>; we emit one <c>QueryPlan</c> span per (system, tick), tagged with the view's
    /// <c>(Kind=1, EcsQueryId)</c> identity so the Workbench can fold executions under the catalog row.
    /// </summary>
    internal override void EmitPerTickQueryPlan(long startTimestamp, long endTimestamp, ushort ownerSystemIdx)
    {
        var hasPlan = HasCachedPlan;
        var evaluatorCount = (byte)Math.Min(Evaluators?.Length ?? 0, byte.MaxValue);
        var indexFieldIdx = hasPlan ? (ushort)Math.Max(0, ExecutionPlan.PrimaryFieldIndex) : (ushort)0;
        var rangeMin = hasPlan ? ExecutionPlan.PrimaryScanMin : long.MinValue;
        var rangeMax = hasPlan ? ExecutionPlan.PrimaryScanMax : long.MaxValue;

        // Execution-site attribution is zero — the runtime drives consumption, no user call site triggers it. The
        // OwnerSystemIdx provides system attribution since parent-span linkage is unreliable in multi-threaded mode
        // (worker threads have no enclosing Typhon span when SystemEndCallback fires).
        TyphonEvent.EmitQueryPlanExternal(startTimestamp, endTimestamp, evaluatorCount, indexFieldIdx, rangeMin, rangeMax, 1, (uint)_query.EcsQueryId,
            0, 0, 0, ownerSystemIdx);
    }

    /// <summary>
    /// Recomputes the view's membership against <paramref name="tx"/>'s snapshot; the <see cref="Added"/> and <see cref="Removed"/> collections reflect the
    /// changes since the previous refresh. The caller-info parameters are captured automatically for diagnostics and should not be supplied explicitly.
    /// </summary>
    /// <param name="tx">Transaction whose snapshot the view is refreshed against.</param>
    /// <param name="callerFile">Auto-captured source file of the call site (diagnostics).</param>
    /// <param name="callerLine">Auto-captured source line of the call site (diagnostics).</param>
    /// <param name="callerMethod">Auto-captured calling member name (diagnostics).</param>
    /// <exception cref="ObjectDisposedException">The view has already been disposed.</exception>
    public override void Refresh(
        Transaction tx,
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site — reserved for future per-refresh execution attribution. The catalog descriptor is emitted from
        // TyphonRuntime's per-tick system-start path (via EmitDescriptorIfNeeded below), not here, because pull-mode/system-input Views never go through
        // Refresh — they're consumed as cached entity sets by the runtime.
        _ = callerFile; _ = callerLine; _ = callerMethod;
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(EcsView<TArchetype>));
        }

        // Each EcsView is bound to a single TArchetype at construction, so we can hand the profiler a concrete archetype ID.
        // A null meta falls back to 0 — can happen if the view's archetype isn't in the registry yet (test-only edge case).
        var archetypeMeta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var scope = TyphonEvent.BeginEcsViewRefresh(archetypeMeta?.ArchetypeId ?? 0);
        try
        {
            // Clear previous delta state
            ClearDelta();
            _addedCache.Clear();
            _removedCache.Clear();

            // Membership mode: an archetype-only query, fed by the per-archetype channel (#790). Checked before the pull branch because it is
            // a strict subset of it — see ViewBase.IsMembershipEligible for why the other two pull shapes must NOT come down here.
            if (IsMembershipEligible)
            {
                RefreshMembership(tx, ref scope);
                return;
            }

            // Pull mode: no FieldEvaluators → full re-query every time
            if (Evaluators.Length == 0)
            {
                RefreshPull(tx);
                BuildEntityIdCaches();
                scope.Mode = EcsViewRefreshMode.Pull;
                scope.ResultCount = _entityIds.Count;
                return;
            }

            // Incremental mode: drain ring buffer
            bool overflow = DeltaBuffer.HasOverflow;
            if (overflow)
            {
                // Phase 7: ECS:View:DeltaBuffer:Overflow instant — operationally critical, fires at the moment overflow is detected.
                // currentTsn = transaction snapshot, tailTsn = last refresh, marginPagesLost = 0 (no per-page accounting at this layer).
                TyphonEvent.EmitEcsViewDeltaBufferOverflow(tx.TSN, LastRefreshTSN, 0);
                SetOverflowDetected(true);
                if (IsOrMode)
                {
                    RefreshFullOr(tx);
                }
                else
                {
                    RefreshFull(tx);
                }
                BuildEntityIdCaches();
                scope.Mode = EcsViewRefreshMode.Overflow;
                scope.ResultCount = _entityIds.Count;
                return;
            }

            // Phase 7: ECS:View:IncrementalDrain span — covers the per-tick delta drain loop. Overflow=0 because we'd have taken the branch above.
            var drainScope = TyphonEvent.BeginEcsViewIncrementalDrain();
            try
            {
                var targetTSN = tx.TSN;
                var deltaCount = 0;
                while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out _))
                {
                    DeltaBuffer.Advance();
                    if (IsOrMode)
                    {
                        ProcessEntryOr(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, tx);
                    }
                    else
                    {
                        ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, tx);
                    }
                    SetLastRefreshTSN(tsn);
                    deltaCount++;
                }
                drainScope.DeltaCount = deltaCount;
                drainScope.Overflow = 0;

                BuildEntityIdCaches();
                scope.Mode = EcsViewRefreshMode.Incremental;
                scope.ResultCount = _entityIds.Count;
                scope.DeltaCount = deltaCount;
            }
            finally
            {
                drainScope.Dispose();
            }
        }
        finally
        {
            scope.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Membership mode (archetype-only — epoch gate + channel drain)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advances an archetype-only view from the membership channel: an O(1) gate when nothing spawned or died, otherwise O(changes).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate is the dominant win, not the drain.</b> A simulation holds tens of views and most of their archetypes are untouched on
    /// any given tick; for those this is one acquire load per archetype and a compare, against the whole-archetype rescan and set-difference
    /// the pull path would otherwise run every tick whether or not anything changed.
    /// </para>
    /// <para>
    /// <b>It is sound only because of MEMB-01</b> — the publisher bumps the epoch AFTER appending every entry for that commit, and before
    /// the commit publishes its TSN. Both halves matter: released early, this returns "nothing to do" over a buffer that has entries in it.
    /// </para>
    /// <para>
    /// Overflow falls back to <c>RefreshFull</c>, which for a view with no evaluators is the pull re-query — the same graceful degradation
    /// the incremental path already has, and no worse than the behaviour this replaces.
    /// </para>
    /// </remarks>
    private void RefreshMembership(Transaction tx, ref EcsViewRefreshEvent scope)
    {
        // Checked FIRST, before overflow. Checked after it, the override stops overriding: a view forced onto the re-query never drains, so
        // past the ring's capacity the sticky overflow flag latches and every subsequent "forced" refresh silently takes the overflow arm
        // instead — which is not the path the caller asked to measure or to use as a differential oracle.
        if (QueryPathProbe.ForceViewRequery)
        {
            // canSettle must still respect pending work: this arm is checked FIRST, so when both hold it would otherwise settle the view over
            // state that exists only in this transaction's staging — the same permanent-phantom bug the arm below was fixed for. The differential
            // test uses this path as its ORACLE, so letting it settle would make the oracle silently become the thing under test.
            RefreshMembershipResync(tx, ref scope, EcsViewRefreshMode.Pull, !tx.HasPendingEcsWork);
            return;
        }

        // The channel carries COMMITTED entries only, and uncommitted work moves no epoch — so against a transaction holding its own pending
        // spawns or destroys the gate would short-circuit and the view would contradict the transaction refreshing it. RefreshPull folds that
        // overlay in exactly as it always did (pending spawns included, pending destroys excluded), so read-your-own-writes is preserved by
        // falling back to it rather than by teaching the channel about uncommitted state.
        if (tx.HasPendingEcsWork)
        {
            // canSettle: false — this resync CANNOT leave the view anchored. RefreshPull folds in the transaction's uncommitted spawns and skips its
            // pending destroys, so the resulting set is true only for that transaction and only while it is open. Uncommitted work moves no epoch,
            // so the epoch comparison at the end would find nothing changed, clear _needsResync, and mark the view clean over staged-only state: if
            // the transaction then rolls back, no commit ever publishes a matching entry, no epoch ever moves, and every later refresh takes the gate.
            // The phantoms are permanent — the exact failure _needsResync exists to prevent, arriving through the door opened to fix a different one.
            RefreshMembershipResync(tx, ref scope, EcsViewRefreshMode.Pull, false);
            return;
        }

        // Overflow, or a seed/resync that has not been proven consistent yet.
        var overflowed = DeltaBuffer.HasOverflow;
        if (_needsResync || overflowed)
        {
            if (overflowed)
            {
                TyphonEvent.EmitEcsViewDeltaBufferOverflow(tx.TSN, LastRefreshTSN, 0);
                SetOverflowDetected(true);
            }
            // Report Overflow only for a REAL one. Every view's first refresh is a seed resync, so reporting the mode unconditionally would make
            // the profiler show an overflow per view per lifetime while EmitEcsViewDeltaBufferOverflow fires only for genuine ones — two signals
            // that disagree, and an overflow rate that looks catastrophic and is not.
            RefreshMembershipResync(tx, ref scope, overflowed ? EcsViewRefreshMode.Overflow : EcsViewRefreshMode.Pull, true);
            return;
        }

        if (EpochsUnchanged())
        {
            // Nothing has spawned or been destroyed in any archetype this view spans since the last refresh. ClearDelta already ran, so the
            // caller correctly observes no changes.
            QueryPathProbe.MembershipGateHits++;
            SetLastRefreshTSN(tx.TSN);
            scope.Mode = EcsViewRefreshMode.Incremental;
            scope.ResultCount = _entityIds.Count;
            scope.DeltaCount = 0;
            return;
        }

        // Read the epochs BEFORE draining. Read after, a commit that appended during the drain — and whose entries TryPeek left behind
        // because its TSN exceeds this snapshot — would be recorded as already accounted for.
        _epochScratch ??= new long[_membershipRegistries.Length];
        ReadEpochsInto(_epochScratch);

        QueryPathProbe.MembershipDrains++;
        var drainScope = TyphonEvent.BeginEcsViewIncrementalDrain();
        try
        {
            var targetTSN = tx.TSN;
            var deltaCount = 0;
            while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out _))
            {
                DeltaBuffer.Advance();
                ProcessMembershipEntry(ref entry, (flags & 0x80) != 0);
                SetLastRefreshTSN(tsn);
                deltaCount++;
            }
            drainScope.DeltaCount = deltaCount;
            drainScope.Overflow = 0;

            // Record the epochs as consumed only if the buffer actually drained. TryPeek stops at the first entry whose TSN exceeds this
            // snapshot — correct, those commits are not visible here yet — and recording anyway would gate the next refresh away from
            // entries still sitting there. Count is the live tail-minus-head, so a concurrent append also (harmlessly) keeps the gate open.
            if (DeltaBuffer.Count == 0)
            {
                Array.Copy(_epochScratch, _lastEpochs, _lastEpochs.Length);
            }

            SetLastRefreshTSN(targetTSN);
            BuildEntityIdCaches();
            scope.Mode = EcsViewRefreshMode.Incremental;
            scope.ResultCount = _entityIds.Count;
            scope.DeltaCount = deltaCount;
        }
        finally
        {
            drainScope.Dispose();
        }
    }

    /// <summary>Scratch for the pre-drain epoch snapshot; reused so the refresh path allocates nothing.</summary>
    private long[] _epochScratch;

    /// <summary>
    /// Rebuilds membership by re-querying at the refresh transaction's snapshot, and returns the view to the channel if it can prove nothing
    /// changed underneath it while doing so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the self-healing arm, and the reason the channel is safe to gate on an epoch at all: any state the incremental path cannot
    /// account for — the initial seed, an overflow that dropped entries, a transaction with its own uncommitted work — is repaired by a full
    /// recomputation rather than papered over.
    /// </para>
    /// <para>
    /// <b>Why the epochs are re-read afterwards.</b> The re-query runs at this transaction's snapshot; a commit that lands while it runs is
    /// in neither the result nor (after the buffer is cleared) the ring. Recording the pre-read epochs would then gate that commit away
    /// forever. Instead the view stays in resync until one completes with no concurrent structural change, which under sustained churn simply
    /// means it keeps re-querying — the behaviour it had before the channel existed, and the correct degradation.
    /// </para>
    /// </remarks>
    private void RefreshMembershipResync(Transaction tx, ref EcsViewRefreshEvent scope, EcsViewRefreshMode mode, bool canSettle)
    {
        _epochScratch ??= new long[_membershipRegistries.Length];
        ReadEpochsInto(_epochScratch);

        // Clear the flag BEFORE the re-query, atomically — and KEEP the result. "Anything dropped up to here is about to be recomputed" is false
        // for a commit whose TSN exceeds this reader's snapshot: RefreshPull re-queries at tx.TSN only, so entries dropped for a LATER commit are
        // in neither the result nor the ring, and discarding the flag here would let EpochsMatch call the resync clean and gate them away forever.
        var droppedBeforeResync = DeltaBuffer.ClearOverflow();

        RefreshPull(tx);

        // Discard what the re-query already accounted for. Not Reset, which would also throw away entries for commits this reader cannot see yet.
        DrainBufferAfterRefreshFull(tx.TSN);

        // A producer that overflowed WHILE the re-query ran dropped entries the re-query's snapshot could not include, and its epoch bump lands
        // after the drop — so the epoch comparison alone would call this clean.
        var overflowedDuringResync = DeltaBuffer.ClearOverflow() || droppedBeforeResync;

        // Two separate questions, and conflating them is what left the public flag latched.
        //
        // (a) Is the SET exact? Yes — the re-query rebuilt it from storage. So the consumer-facing overflow flag, whose contract is "granular
        //     per-field tracking was lost, treat this as a full invalidation", is honestly cleared by any completed resync.
        SetOverflowDetected(false);

        // (b) May the view go back to trusting the channel? Only if nothing happened underneath this resync that the re-query cannot have covered.
        //     An overflow seen at either end disqualifies it: the dropped entries may belong to a commit whose TSN exceeds this snapshot, in which
        //     case the re-query did not include them AND the epochs read at the start already accounted for the bump — so settling would gate them
        //     away for good. Staying in resync costs one more O(N) at a later snapshot, which does cover them.
        if (canSettle && !overflowedDuringResync && EpochsMatch(_epochScratch))
        {
            Array.Copy(_epochScratch, _lastEpochs, _lastEpochs.Length);
            _needsResync = false;
        }
        else
        {
            _needsResync = true;
        }

        SetLastRefreshTSN(tx.TSN);
        BuildEntityIdCaches();
        scope.Mode = mode;
        scope.ResultCount = _entityIds.Count;
    }

    /// <summary>True when every subscribed archetype's epoch still equals <paramref name="snapshot"/>.</summary>
    private bool EpochsMatch(long[] snapshot)
    {
        var regs = _membershipRegistries;
        for (var i = 0; i < regs.Length; i++)
        {
            if (regs[i].StructuralEpoch != snapshot[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Applies one membership entry. No evaluator lookup, no component read, no archetype mask test — the channel is per-archetype and the
    /// view subscribed to exactly the archetypes its mask selects, so every entry that arrives already belongs.
    /// </summary>
    /// <remarks>
    /// The entry's <c>BeforeKey</c> carries the entity's <c>ClusterLocation</c> as an OPAQUE value. Nothing here dereferences it, and
    /// nothing here may start to (MEMB-02): resolving it to a chunk pointer at refresh time would reintroduce the whole #582 freed-chunk
    /// hazard on a path that currently has none. It is carried so cluster-native membership (stage 2) needs no second entry format.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessMembershipEntry(ref ViewDeltaEntry entry, bool isDeletion)
    {
        var pk = (long)entry.EntityPK.RawValue;
        if (isDeletion)
        {
            if (_entityIds.TryRemove(pk))
            {
                CompactDelta(pk, DeltaKind.Removed);
            }
            return;
        }

        if (_entityIds.TryAdd(pk))
        {
            CompactDelta(pk, DeltaKind.Added);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pull mode (no FieldEvaluators — re-query + diff)
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshPull(Transaction tx)
    {
        // Phase 7: ECS:View:RefreshPull span. queryNs/archetypeMaskBits left at 0 — no per-call accounting at this layer.
        QueryPathProbe.ViewRequeries++;
        var pullScope = TyphonEvent.BeginEcsViewRefreshPull(0, 0);
        try
        {
            _query.UpdateTransaction(tx);
            var newSet = _query.Execute();

            // Compute deltas: Added/Removed
            foreach (var id in newSet)
            {
                var pk = (long)id.RawValue;
                if (_entityIds.TryAdd(pk))
                {
                    CompactDelta(pk, DeltaKind.Added);
                }
            }

            // Check for removals (reuse scratch list to avoid per-refresh allocation)
            _pullRemoveScratch ??= [];
            _pullRemoveScratch.Clear();
            foreach (var pk in _entityIds)
            {
                if (!newSet.Contains(EntityId.FromRaw(pk)))
                {
                    _pullRemoveScratch.Add(pk);
                }
            }

            for (var i = 0; i < _pullRemoveScratch.Count; i++)
            {
                _entityIds.TryRemove(_pullRemoveScratch[i]);
                CompactDelta(_pullRemoveScratch[i], DeltaKind.Removed);
            }

            SetLastRefreshTSN(tx.TSN);
        }
        finally
        {
            _query.DetachTransaction();
            pullScope.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Incremental mode — delta processing (ported from View<T>)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntry(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        ref var eval = ref FindEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        // Check archetype mask: only process entities from matching archetypes
        var entityId = entry.EntityPK;
        if (!_query.MaskTestPublicByRouting(_dbe, entityId.ArchetypeId))
        {
            return;
        }

        // The view's entity set is keyed on the raw EntityId value (see PopulateFromEntityMaps / _entityIds).
        var pk = (long)entityId.RawValue;

        var wasInView = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
        var shouldBeInView = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

        if (Evaluators.Length == 1)
        {
            ApplyDelta(pk, wasInView, shouldBeInView);
        }
        else
        {
            ProcessMultiField(pk, fieldIndex, wasInView, shouldBeInView, tx);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EvaluateKey(ref FieldEvaluator eval, ref KeyBytes8 key)
    {
        // A stack copy: the evaluator takes a pointer, and a pointer may address only the stack or engine memory, wherever the entry lives.
        var local = key;
        return FieldEvaluator.Evaluate(ref eval, (byte*)&local);
    }

    private void ProcessMultiField(long pk, int fieldIndex, bool wasInView, bool shouldBeInView, Transaction tx)
    {
        if (wasInView == shouldBeInView)
        {
            if (shouldBeInView && _entityIds.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Modified);
            }
            return;
        }

        if (!wasInView)
        {
            // OUT→IN: verify all other fields pass
            if (_fieldReader != null && _fieldReader.CheckOtherFields(pk, Evaluators, fieldIndex, tx))
            {
                ApplyDelta(pk, false, true);
            }
        }
        else
        {
            // IN→OUT: remove if entity was in view
            if (_entityIds.Contains(pk))
            {
                ApplyDelta(pk, true, false);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Full refresh (overflow recovery — uses EcsQuery broad scan)
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshFull(Transaction tx)
    {
        var oldCount = _entityIds.Count;
        // Phase 7: ECS:View:RefreshFull span — overflow-recovery full re-query. NewCount + RequeryNs filled at exit.
        var fullScope = TyphonEvent.BeginEcsViewRefreshFull(oldCount, 0, 0);
        try
        {
            var oldEntities = _entityIds.Clone();

            DeltaBuffer.Reset(tx.TSN);
            _entityIds.Clear();

            // #668: this used to have three branches and only this one was reachable. The other two required a view with a field reader but no cached plan
            // (the constructor producing that shape had no callers) or a pull view (which returns from Refresh before the overflow check that leads here).
            // Dead code that looks like a fallback hides how narrow the live path is — and one of the dead branches was the CORRECT one for cluster-backed
            // archetypes, which is how #663 read as "correct or broken depending on whether a plan exists" when it was always broken.
            Debug.Assert(HasCachedPlanInternal && _fieldReader != null,
                "RefreshFull is overflow recovery for an INCREMENTAL view — it rebuilds membership from the cached plan and the field reader. A pull view "
                + "reaching it means Refresh's pull-mode return was bypassed, and this would rebuild a predicate-filtered set without evaluating the "
                + "predicate: wrong rows, not a slow refresh.");

            var requeryStart = Stopwatch.GetTimestamp();

            // Cross-archetype scan with the cached plan: a cluster-backed archetype keeps its indexes on the archetype, so a plan targeting the
            // ComponentTable tree would repopulate to empty (#663).
            _query.UpdateTransaction(tx);
            _query.ExecuteFullScanAcrossArchetypes(CachedPlan, CachedPlan.OrderedEvaluators, _componentTable, _entityIds);
            fullScope.RequeryNs = (uint)Math.Min((Stopwatch.GetTimestamp() - requeryStart) * 1_000_000_000L / Stopwatch.Frequency, uint.MaxValue);

            DrainBufferAfterRefreshFull(tx.TSN);
            ComputeRefreshFullDeltas(oldEntities);

            SetOverflowDetected(false);
            SetLastRefreshTSN(tx.TSN);

            fullScope.NewCount = _entityIds.Count;
        }
        finally
        {
            _query.DetachTransaction();
            fullScope.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OR mode — branch bitmap processing (ported from OrView<T>)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Populate initial entity set for OR mode — executes each branch plan and unions results with bitmap tracking.</summary>
    internal void PopulateInitialOr(Transaction tx)
    {
        var plans = CachedPlans;
        if (plans == null) return;

        _query.UpdateTransaction(tx);
        try
        {
            for (var b = 0; b < plans.Length; b++)
            {
                var branchResult = new HashMap<long>();
                // Cross-archetype: a cluster-backed archetype's indexes live on the archetype, so scanning only the ComponentTable tree leaves every OR branch
                // empty (#663).
                _query.ExecuteFullScanAcrossArchetypes(plans[b], plans[b].OrderedEvaluators, _componentTable, branchResult);
                var bit = (ushort)(1 << b);
                foreach (var pk in branchResult)
                {
                    var entityId = EntityId.FromRaw(pk);
                    if (!_query.MaskTestPublicByRouting(_dbe, entityId.ArchetypeId))
                    {
                        continue;
                    }

                    _entityIds.TryAdd(pk);
                    ref var bitmapRef = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_branchBitmaps, pk, out _);
                    bitmapRef |= bit;
                }
            }
        }
        finally
        {
            _query.DetachTransaction();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntryOr(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        var entityId = entry.EntityPK;
        if (!_query.MaskTestPublicByRouting(_dbe, entityId.ArchetypeId))
        {
            return;
        }

        var pk = (long)entityId.RawValue;
        _branchBitmaps.TryGetValue(pk, out var oldBitmap);
        if (isCreation)
        {
            oldBitmap = 0;
        }

        var wasInView = oldBitmap != 0;
        var newBitmap = oldBitmap;

        for (var b = 0; b < _branchEvaluators.Length; b++)
        {
            var branchEvals = _branchEvaluators[b];
            var evalIndex = FindEvaluatorInBranch(b, fieldIndex);
            if (evalIndex < 0)
            {
                continue;
            }

            ref var eval = ref branchEvals[evalIndex];
            var bit = (ushort)(1 << b);

            var fieldWasIn = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
            var fieldIsIn = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

            if (fieldWasIn == fieldIsIn)
            {
                continue;
            }

            if (!fieldWasIn)
            {
                if (_fieldReader.CheckOtherFieldsInBranch(pk, branchEvals, fieldIndex, tx))
                {
                    newBitmap |= bit;
                }
            }
            else
            {
                newBitmap &= (ushort)~bit;
            }
        }

        if (isDeletion)
        {
            newBitmap = 0;
        }

        var shouldBeInView = newBitmap != 0;

        if (newBitmap != 0)
        {
            _branchBitmaps[pk] = newBitmap;
        }
        else if (oldBitmap != 0)
        {
            _branchBitmaps.Remove(pk);
        }

        if (wasInView && shouldBeInView && oldBitmap != newBitmap)
        {
            CompactDelta(pk, DeltaKind.Modified);
        }
        else
        {
            ApplyDelta(pk, wasInView, shouldBeInView);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindEvaluatorInBranch(int branchIndex, int fieldIndex)
    {
        var lookup = _branchEvalLookup[branchIndex];
        return (uint)fieldIndex < (uint)lookup.Length ? lookup[fieldIndex] : -1;
    }

    private void RefreshFullOr(Transaction tx)
    {
        var oldCount = _entityIds.Count;
        var plans = CachedPlans;
        // Phase 7: ECS:View:RefreshFullOr span — OR-mode overflow recovery.
        var fullOrScope = TyphonEvent.BeginEcsViewRefreshFullOr(oldCount, 0, (byte)Math.Min(plans?.Length ?? 0, byte.MaxValue));
        try
        {
            var oldEntities = _entityIds.Clone();
            DeltaBuffer.Reset(tx.TSN);
            _entityIds.Clear();
            _branchBitmaps.Clear();

            if (plans != null)
            {
                _query.UpdateTransaction(tx);
                for (var b = 0; b < plans.Length; b++)
                {
                    var branchResult = new HashMap<long>();
                    // Cross-archetype — same reason as PopulateInitialOr (#663).
                    _query.ExecuteFullScanAcrossArchetypes(plans[b], plans[b].OrderedEvaluators, _componentTable, branchResult);
                    var bit = (ushort)(1 << b);
                    foreach (var pk in branchResult)
                    {
                        var eid = EntityId.FromRaw(pk);
                        if (!_query.MaskTestPublicByRouting(_dbe, eid.ArchetypeId))
                        {
                            continue;
                        }

                        _entityIds.TryAdd(pk);
                        ref var bitmapRef = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_branchBitmaps, pk, out _);
                        bitmapRef |= bit;
                    }
                }
            }

            DrainBufferAfterRefreshFull(tx.TSN);
            ComputeRefreshFullDeltas(oldEntities);
            SetOverflowDetected(false);
            SetLastRefreshTSN(tx.TSN);

            fullOrScope.NewCount = _entityIds.Count;
        }
        finally
        {
            _query.DetachTransaction();
            fullOrScope.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Evaluator lookup
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldEvaluator FindEvaluator(int fieldIndex)
    {
        if (_evaluatorLookup != null && (uint)fieldIndex < (uint)_evaluatorLookup.Length)
        {
            var idx = _evaluatorLookup[fieldIndex];
            if (idx >= 0)
            {
                return ref Evaluators[idx];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }

    private static int[] BuildEvaluatorLookup(FieldEvaluator[] evaluators)
    {
        if (evaluators.Length == 0)
        {
            return null;
        }

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
            return null;
        }
        var lookup = new int[maxField + 1];
        Array.Fill(lookup, -1);
        for (var i = 0; i < evaluators.Length; i++)
        {
            lookup[evaluators[i].FieldIndex] = i;
        }
        return lookup;
    }

    private static int[] BuildFieldDependencies(FieldEvaluator[] evaluators)
    {
        if (evaluators.Length == 0)
        {
            return [];
        }

        var fieldIndices = new HashSet<int>();
        for (var i = 0; i < evaluators.Length; i++)
        {
            fieldIndices.Add(evaluators[i].FieldIndex);
        }
        var deps = new int[fieldIndices.Count];
        fieldIndices.CopyTo(deps);
        Array.Sort(deps);
        return deps;
    }

    // ── Multi-branch helpers ──

    private static FieldEvaluator[] FlattenEvaluators(FieldEvaluator[][] branchEvaluators)
    {
        var total = 0;
        for (var i = 0; i < branchEvaluators.Length; i++)
        {
            total += branchEvaluators[i].Length;
        }

        var result = new FieldEvaluator[total];
        var offset = 0;
        for (var i = 0; i < branchEvaluators.Length; i++)
        {
            Array.Copy(branchEvaluators[i], 0, result, offset, branchEvaluators[i].Length);
            offset += branchEvaluators[i].Length;
        }
        return result;
    }

    private static int[] BuildFieldDependenciesMulti(FieldEvaluator[][] branchEvaluators)
    {
        var fieldIndices = new HashSet<int>();
        for (var b = 0; b < branchEvaluators.Length; b++)
        {
            for (var i = 0; i < branchEvaluators[b].Length; i++)
            {
                fieldIndices.Add(branchEvaluators[b][i].FieldIndex);
            }
        }

        var deps = new int[fieldIndices.Count];
        fieldIndices.CopyTo(deps);
        Array.Sort(deps);
        return deps;
    }

    private static int[][] BuildBranchEvalLookup(FieldEvaluator[][] branchEvaluators)
    {
        var result = new int[branchEvaluators.Length][];
        for (var b = 0; b < branchEvaluators.Length; b++)
        {
            var evals = branchEvaluators[b];
            var maxField = -1;
            for (var i = 0; i < evals.Length; i++)
            {
                if (evals[i].FieldIndex > maxField)
                {
                    maxField = evals[i].FieldIndex;
                }
            }

            if (maxField < 0) { result[b] = []; continue; }
            var lookup = new int[maxField + 1];
            Array.Fill(lookup, -1);
            for (var i = 0; i < evals.Length; i++)
            {
                lookup[evals[i].FieldIndex] = i;
            }

            result[b] = lookup;
        }
        return result;
    }
}

/// <summary>
/// Abstracts typed component reading for <see cref="EcsView{TArchetype}"/>.
/// Created by a generic factory that captures the component type T, allowing EcsView to remain parameterized only by TArchetype.
/// </summary>
internal abstract class EcsViewFieldReader
{
    public abstract bool CheckOtherFields(long pk, FieldEvaluator[] evaluators, int skipFieldIndex, Transaction tx);
    public abstract bool CheckOtherFieldsInBranch(long pk, FieldEvaluator[] branchEvals, int skipFieldIndex, Transaction tx);
    public abstract bool EvaluateAllFields(long pk, FieldEvaluator[] evaluators, Transaction tx);
}

/// <summary>
/// Typed implementation that reads component <typeparamref name="T"/> via <see cref="EntityAccessor.Open(EntityId)"/> + <see cref="EntityRef.TryRead{T}"/>.
/// </summary>
internal sealed unsafe class EcsViewFieldReader<T> : EcsViewFieldReader where T : unmanaged
{
    public static readonly EcsViewFieldReader<T> Instance = new();

    public override bool CheckOtherFields(long pk, FieldEvaluator[] evaluators, int skipFieldIndex, Transaction tx)
    {
        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.TryRead<T>(out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex == skipFieldIndex)
            {
                continue;
            }
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    public override bool CheckOtherFieldsInBranch(long pk, FieldEvaluator[] branchEvals, int skipFieldIndex, Transaction tx)
    {
        if (branchEvals.Length == 1)
        {
            return true; // Only one field in this branch, and it already passed
        }

        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.TryRead<T>(out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < branchEvals.Length; i++)
        {
            if (branchEvals[i].FieldIndex == skipFieldIndex)
            {
                continue;
            }
            ref var eval = ref branchEvals[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    public override bool EvaluateAllFields(long pk, FieldEvaluator[] evaluators, Transaction tx)
    {
        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.TryRead<T>(out var comp))
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

}
