using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using JetBrains.Annotations;
using Typhon.Engine.internals;
using Typhon.Profiler;
using Typhon.Schema.Definition;
using static Typhon.Engine.Internals.SnapshotSlot;

namespace Typhon.Engine;

/// <summary>Type of spatial predicate attached to an EcsQuery.</summary>
internal enum SpatialQueryType : byte
{
    None = 0,
    AABB = 1,
    Radius = 2,
    Ray = 3,
    Frustum = 4,
}

/// <summary>
/// Allocates process-global, monotonically-increasing ids for <see cref="EcsQuery{TArchetype}.EcsQueryId"/>. Non-generic on purpose: a static field on the
/// generic <see cref="EcsQuery{TArchetype}"/> gives each closed type its own counter, so ids would only be unique per archetype type — breaking the global
/// uniqueness the profiler's <c>(Kind=1, EcsQueryId)</c> identity relies on. Mirrors <see cref="ViewBase"/>'s non-generic <c>NextViewId</c>.
/// </summary>
internal static class EcsQueryIdAllocator
{
    internal static int Next;
}

/// <summary>
/// ECS query builder with three-tier evaluation: T1 (ArchetypeMask), T2 (EnabledBits), T3 (WHERE — future).
/// Supports polymorphic queries (archetype + descendants) and exact queries (single archetype).
/// </summary>
[PublicAPI]
#pragma warning disable TYPHON005 // EcsQuery borrows Transaction, doesn't own it
public unsafe struct EcsQuery<TArchetype> where TArchetype : class
{
    /// <summary>
    /// Monotonic, globally-unique handle for this query struct instance, assigned at construction (mirrors <see cref="ViewBase.ViewId"/>).
    /// Each <c>tx.Query&lt;T&gt;()</c> / <c>tx.QueryExact&lt;T&gt;()</c> call produces a fresh ID; fluent mutation methods preserve it because the struct
    /// value carries the field. The profiler consumer thread dedupes multiple instance IDs into "definitions" using <c>(constructionSite, structuralShape)</c>
    /// — see issue #335 / #336.
    /// </summary>
    public int EcsQueryId { get; }

    /// <summary>User source file where this query was constructed (or zeroed when constructed without attribution). See <see cref="ViewBase.SourceFile"/>.</summary>
    public string SourceFile { get; }

    /// <summary>User source line where this query was constructed. Zero if unattributed.</summary>
    public int SourceLine { get; }

    /// <summary>User source method name where this query was constructed. Null if unattributed.</summary>
    public string SourceMethod { get; }

    private Transaction _tx;
    private ArchetypeMask256 _mask256;          // used when _useLargeMask == false
    private ArchetypeMaskLarge _maskLarge;       // used when _useLargeMask == true
    private bool _useLargeMask;
    private int _enabledTypeIdCount;
    private int _disabledTypeIdCount;

    // Expression-based WHERE state (for incremental views)
    private FieldPredicate[][] _fieldPredicateBranches;
    private ComponentTable _whereComponentTable;
    private EcsViewFieldReader _whereFieldReader;

    // OrderBy/Skip/Take state
    private OrderByField? _orderBy;
    private int _skip;
    private int _take;
    private int _enabledTypeId0, _enabledTypeId1, _enabledTypeId2, _enabledTypeId3;
    private int _disabledTypeId0, _disabledTypeId1, _disabledTypeId2, _disabledTypeId3;
    private EntityFilter _whereFilter;
    private EntityFilter _pendingSpawnFieldFilter;

    // Spatial query predicate (at most one per query)
    private ComponentTable _spatialTable;
    private SpatialQueryType _spatialQueryType;
    // The realm the spatial predicate is evaluated in (Realms C1): realm 0 unless InRealm named another.
    private RealmId _spatialRealm;
    private bool _realmNamed;
    // Inline query parameters: meaning depends on _spatialQueryType
    // AABB: [min0..max0..] in [0]..[5]. Radius: center in [0]..[2], radius in [3]. Ray: origin in [0]..[2], dir in [3]..[5], maxDist in [6].
    // Frustum: the bounding box of the frustum in [0]..[5], same layout as AABB; the planes themselves live in _frustumPlanes.
    private fixed double _spatialParams[7];

    /// <summary>
    /// First buffer size tried for a cluster ray or frustum query.
    /// </summary>
    /// <remarks>
    /// <para><b>Those two cluster APIs truncate silently.</b> Both fill a caller-supplied <c>Span</c> and stop when it is full, which is right for a picking
    /// ray asking for the nearest few and wrong for a query whose contract is "every match" — a short buffer would be an <c>SQ-01</c> false negative that no
    /// oracle test would catch unless it happened to exceed the size chosen here. So the buffer is grown and the query re-run whenever the returned count
    /// equals the capacity offered. That test cannot distinguish "exactly full" from "truncated", which costs one redundant re-run on an exact multiple and
    /// is the correct way round: guessing wrong re-runs, guessing wrong the other way loses rows.</para>
    /// <para><b>The re-run is safe because the query is a pure read.</b> Both walks take an <c>EpochGuard</c> per call and mutate nothing, so a second pass
    /// over the same grid returns the same set.</para>
    /// </remarks>
    private const int InitialClusterResultCapacity = 1024;

    /// <summary>Ceiling on the growth above — 16 M ids is 128 MB of buffer, well past any result set a single query should be materialising.</summary>
    /// <remarks>
    /// Reaching it <b>throws</b>. The first version of the growth loop stopped doubling here and processed the truncated buffer as if it were complete, which
    /// relocated the <c>SQ-01</c> false negative to 16 M rather than removing it — the exact defect the loop exists to prevent, at a size no test would reach.
    /// </remarks>
    private const int MaxClusterResultCapacity = 1 << 24;

    /// <summary>
    /// Largest capacity still worth renting from <see cref="ArrayPool{T}"/>; above this the loop allocates directly.
    /// </summary>
    /// <remarks>
    /// <c>ArrayPool&lt;T&gt;.Shared</c>'s largest bucket is 2²⁰ elements. A <c>Rent</c> above that allocates a fresh array and the matching <c>Return</c>
    /// drops it, so the doubling loop would churn one large-object allocation per attempt — for a result set that actually needs 16 M, several hundred MB of
    /// LOH garbage for one query. Past the bucket the loop allocates once and keeps it for the attempt, which is the same cost without pretending to pool.
    /// </remarks>
    private const int MaxPooledResultCapacity = 1 << 20;

    /// <summary>
    /// Upper bound on <c>WhereFrustum</c>'s plane count.
    /// </summary>
    /// <remarks>
    /// The cluster frustum query <c>stackalloc</c>s <c>planeCount × (dim + 1)</c> doubles per call and again per promoted cell. An unbounded count from user
    /// input is a stack overflow, which kills the process rather than raising something a caller can catch. Sixty-four is far past any real frustum — a camera
    /// has six planes, a portal-clipped one a handful more.
    /// </remarks>
    private const int MaxFrustumPlanes = 64;

    // Frustum planes, packed (normalX, normalY[, normalZ], distance) — 3 doubles per plane in 2D, 4 in 3D. A plane set does not fit the inline buffer and
    // its length depends on the caller, so it is the one spatial parameter held by reference.
    private double[] _frustumPlanes;
    private int _frustumPlaneCount;

    internal EcsQuery(Transaction tx, bool polymorphic, string sourceFile = null, int sourceLine = 0, string sourceMethod = null)
    {
        EcsQueryId = Interlocked.Increment(ref EcsQueryIdAllocator.Next);
        SourceFile = sourceFile;
        SourceLine = sourceLine;
        SourceMethod = sourceMethod;
        _tx = tx;
        _useLargeMask = !ArchetypeRegistry.UseSmallMask;

        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        if (meta == null)
        {
            return;
        }

        // Phase 7: ECS:Query:Construct span — covers archetype mask resolution.
        var ctorScope = TyphonEvent.BeginEcsQueryConstruct(
            Math.Min(meta.ArchetypeId, ushort.MaxValue),
            (byte)(polymorphic ? 1 : 0),
            (byte)(_useLargeMask ? 1 : 0));  // 0 = Mask256, 1 = MaskLarge
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. Pure bit math; if a callee changes, re-tag to variant B.
        // Phase 7: ECS:Query:SubtreeExpand span — covers polymorphic subtree expansion (when applicable).
        if (polymorphic && meta.SubtreeArchetypeIds != null)
        {
            var subtreeScope = TyphonEvent.BeginEcsQuerySubtreeExpand(
                (ushort)Math.Min(meta.SubtreeArchetypeIds.Length, ushort.MaxValue),
                Math.Min(meta.ArchetypeId, ushort.MaxValue));
            if (_useLargeMask)
            {
                _maskLarge = ArchetypeMaskLarge.FromSubtree(meta.SubtreeArchetypeIds, ArchetypeRegistry.MaxArchetypeId);
            }
            else
            {
                _mask256 = ArchetypeMask256.FromSubtree(meta.SubtreeArchetypeIds);
            }
            subtreeScope.Dispose();
        }
        else
        {
            if (_useLargeMask)
            {
                _maskLarge = ArchetypeMaskLarge.FromArchetype(meta.ArchetypeId, ArchetypeRegistry.MaxArchetypeId);
            }
            else
            {
                _mask256 = ArchetypeMask256.FromArchetype(meta.ArchetypeId);
            }
        }
        // PROFILING-SPAN-NO-THROW-END
        ctorScope.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 1 constraints — ArchetypeMask
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Include only archetypes that declare <typeparamref name="T"/>. Mask AND.</summary>
    public EcsQuery<TArchetype> With<T>() where T : unmanaged
    {
        var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (typeId < 0)
        {
            _mask256 = default;
            _maskLarge = default;
            return this;
        }
        if (_useLargeMask)
        {
            _maskLarge = _maskLarge.And(ArchetypeRegistry.GetComponentMaskLarge(typeId));
        }
        else
        {
            _mask256 = _mask256.And(ArchetypeRegistry.GetComponentMask(typeId));
        }
        return this;
    }

    /// <summary>Exclude archetypes that declare <typeparamref name="T"/>. Mask AND NOT.</summary>
    public EcsQuery<TArchetype> Without<T>() where T : unmanaged
    {
        var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (typeId < 0)
        {
            return this;
        }
        if (_useLargeMask)
        {
            _maskLarge = _maskLarge.AndNot(ArchetypeRegistry.GetComponentMaskLarge(typeId));
        }
        else
        {
            _mask256 = _mask256.AndNot(ArchetypeRegistry.GetComponentMask(typeId));
        }
        return this;
    }

    /// <summary>Remove an archetype subtree. Mask AND NOT subtree.</summary>
    public EcsQuery<TArchetype> Exclude<TExcluded>() where TExcluded : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TExcluded>();
        if (meta == null)
        {
            return this;
        }

        if (_useLargeMask)
        {
            var excludeMask = meta.SubtreeArchetypeIds != null ? 
                ArchetypeMaskLarge.FromSubtree(meta.SubtreeArchetypeIds, ArchetypeRegistry.MaxArchetypeId) :
                ArchetypeMaskLarge.FromArchetype(meta.ArchetypeId, ArchetypeRegistry.MaxArchetypeId);
            _maskLarge = _maskLarge.AndNot(excludeMask);
        }
        else
        {
            var excludeMask = meta.SubtreeArchetypeIds != null ? 
                ArchetypeMask256.FromSubtree(meta.SubtreeArchetypeIds) : ArchetypeMask256.FromArchetype(meta.ArchetypeId);
            _mask256 = _mask256.AndNot(excludeMask);
        }
        return this;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 2 constraints — EnabledBits
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Include only entities where <typeparamref name="T"/> is enabled.</summary>
    public EcsQuery<TArchetype> Enabled<T>() where T : unmanaged
    {
        var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        CheckConfig.Require(CheckConfig.Enabled, typeId >= 0, $"Component {typeof(T).Name} not registered");
        // Phase 7: ECS:Query:Constraint:Enabled instant.
        TyphonEvent.EmitEcsQueryConstraintEnabled((ushort)Math.Min(typeId, ushort.MaxValue), 1);
        AddEnabledTypeId(typeId);
        return this;
    }

    /// <summary>Include only entities where <typeparamref name="T"/> is disabled.</summary>
    public EcsQuery<TArchetype> Disabled<T>() where T : unmanaged
    {
        var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        CheckConfig.Require(CheckConfig.Enabled, typeId >= 0, $"Component {typeof(T).Name} not registered");
        // Phase 7: ECS:Query:Constraint:Enabled instant (enableBit=0 means Disabled).
        TyphonEvent.EmitEcsQueryConstraintEnabled((ushort)Math.Min(typeId, ushort.MaxValue), 0);
        AddDisabledTypeId(typeId);
        return this;
    }

    private void AddEnabledTypeId(int typeId)
    {
        switch (_enabledTypeIdCount)
        {
            case 0: _enabledTypeId0 = typeId; break;
            case 1: _enabledTypeId1 = typeId; break;
            case 2: _enabledTypeId2 = typeId; break;
            case 3: _enabledTypeId3 = typeId; break;
            default: throw new InvalidOperationException("Max 4 Enabled<T> constraints per query. Use archetype hierarchy or component composition to reduce filter count.");
        }
        _enabledTypeIdCount++;
    }

    private void AddDisabledTypeId(int typeId)
    {
        switch (_disabledTypeIdCount)
        {
            case 0: _disabledTypeId0 = typeId; break;
            case 1: _disabledTypeId1 = typeId; break;
            case 2: _disabledTypeId2 = typeId; break;
            case 3: _disabledTypeId3 = typeId; break;
            default: throw new InvalidOperationException("Max 4 Disabled<T> constraints per query. Use archetype hierarchy or component composition to reduce filter count.");
        }
        _disabledTypeIdCount++;
    }

    private readonly bool HasT2 => _enabledTypeIdCount > 0 || _disabledTypeIdCount > 0;

    /// <summary>The component types this query names <c>Disabled&lt;T&gt;()</c>, which its per-entity predicates read disabled (QENABLE-01).</summary>
    private readonly DisabledTypeIds DisabledNames => new(_disabledTypeIdCount, _disabledTypeId0, _disabledTypeId1, _disabledTypeId2, _disabledTypeId3);

    /// <summary>
    /// QENABLE-01 on the paths that test entities one at a time — the spatial candidates: the query's <c>Enabled&lt;U&gt;()</c> / <c>Disabled&lt;U&gt;()</c>
    /// against <paramref name="id"/>'s bits at this snapshot, this transaction's own changes included, as the broad scan applies them.
    /// </summary>
    private bool AdmitsT2(EntityId id)
    {
        // TryOpen: a spatial hit can be an entity this transaction destroyed, or one the snapshot does not see — no entity, nothing to admit.
        if (!_tx.TryOpen(id, out var entity))
        {
            return false;
        }

        return ResolveT2Masks(entity._archetype, out var requiredEnabled, out var requiredDisabled)
               && (entity._enabledBits & requiredEnabled) == requiredEnabled && (entity._enabledBits & requiredDisabled) == 0;
    }

    private readonly bool MaskIsEmpty => _useLargeMask ? _maskLarge.IsEmpty : _mask256.IsEmpty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool MaskTest(ushort archetypeId) => _useLargeMask ? _maskLarge.Test(archetypeId) : _mask256.Test(archetypeId);

    /// <summary>Mask test for an EntityId-derived <b>routing</b> id (masks are catalog-space; translate routing → catalog first). Returns false for an
    /// unknown routing id, matching the pre-refactor behaviour of testing an unregistered archetype id.</summary>
    private readonly bool MaskTestByRouting(ushort routingId)
    {
        var m = _tx.DBE.GetMetaByRouting(routingId);
        return m != null && MaskTest(m.ArchetypeId);
    }

    private readonly int MaskMaxId => _useLargeMask ? _maskLarge.MaxId : _mask256.MaxId;

    /// <summary>
    /// The statistics array the planner should estimate this query's selectivity from — the per-archetype one when the query resolves to exactly one
    /// cluster-backed archetype, otherwise the shared per-ComponentTable array (#665).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Statistics describe a key DISTRIBUTION, and the two homes hold different populations. For a cluster-backed archetype the ComponentTable's array wraps
    /// a tree with no entries, so <c>EntryCount == 0</c> and every estimate comes back 0 — which the planner reads as "perfectly selective" and the cluster
    /// executor separately reads as "unknown, take Path B". The plan is built from a number that describes nothing.
    /// </para>
    /// <para>
    /// Restricted to a single matching archetype on purpose. Across several there is no one distribution to hand the planner: blending them would make a
    /// predicate that is selective within one archetype read as unselective, which is the same defect as sharing one array in the first place. Those queries
    /// keep today's behaviour, and the honest fix for them is a merged estimate — a follow-up, not this issue.
    /// </para>
    /// </remarks>
    internal IndexStatistics[] PlannerStats(ComponentTable ct)
    {
        var dbe = _tx.DBE;
        IndexStatistics[] found = null;

        // Walk the archetype ids this query's mask can select, not the registry's enumerator. GetAllArchetypes() scans
        // the registry's full 4096-entry capacity through a `yield return` iterator, so the enumerable allocates and
        // every step is an interface-dispatched MoveNext that cannot inline. That is the right shape for its other
        // callers — schema validation and the Workbench inspector, both once per process — and the wrong one here,
        // because this runs on EVERY plan build. Measured at ~3 us per call against a ~5 us query, which is what
        // doubled ClusterRegressionBenchmarks.IndexedQuery_1Percent when #665 introduced this method (#629).
        //
        // The set is identical: Archetypes is indexed BY archetype id (GetMetadata is a plain array read),
        // MaxArchetypeId bounds the live range, and a null slot is an unregistered id — the same entries
        // GetAllArchetypes() skips. Testing the mask BEFORE resolving the metadata is also what turns the remaining
        // work into a bit test per id instead of a dereference per archetype.
        var maxId = ArchetypeRegistry.MaxArchetypeId;
        for (var id = 0; id <= maxId; id++)
        {
            if (!MaskTest((ushort)id))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)id);
            if (meta == null)
            {
                continue;
            }

            if (found != null)
            {
                return null;   // more than one archetype matches — no single distribution to describe them
            }

            var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
            if (clusterState == null)
            {
                return null;
            }

            var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
            if (ixSlotIdx < 0)
            {
                return null;
            }

            found = transientHome ? clusterState.TransientIndexSlots[ixSlotIdx].Stats : clusterState.IndexSlots[ixSlotIdx].Stats;
        }

        return found;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 3 constraints — WHERE predicates (broad scan evaluation)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Filter entities by a component field predicate. Evaluated per-entity during broad scan via <see cref="EntityAccessor.Open(EntityId)"/> +
    /// <see cref="EntityRef.TryRead{T}(out T)"/>. Multiple Where calls chain as AND (each must pass).
    /// </summary>
    /// <remarks>Targeted scan (index-first) is not yet available — always uses broad scan.</remarks>
    /// <remarks>
    /// <typeparamref name="T"/> is read when enabled at the snapshot, or disabled and present when the query names <c>Disabled&lt;T&gt;()</c> (QENABLE-01) —
    /// the contract of <see cref="WhereField{T}"/>.
    /// </remarks>
    public EcsQuery<TArchetype> Where<T>(Func<T, bool> predicate) where T : unmanaged
    {
        var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        var prevFilter = _whereFilter;
        _whereFilter = prevFilter == null ? (id, tx, readDisabled) =>
            {
                if (!tx.TryOpen(id, out var entity))
                {
                    return false;
                }

                return entity.TryReadForPredicate<T>(readDisabled.Contains(typeId), out var value) && predicate(value);
            } : (id, tx, readDisabled) =>
            {
                if (!prevFilter(id, tx, readDisabled))
                {
                    return false;
                }
                if (!tx.TryOpen(id, out var entity))
                {
                    return false;
                }

                return entity.TryReadForPredicate<T>(readDisabled.Contains(typeId), out var value) && predicate(value);
            };
        return this;
    }

    /// <summary>
    /// Filter entities by an indexed-field predicate, enabling incremental view refresh via <see cref="ViewDeltaRingBuffer"/>.
    /// The expression is parsed into <see cref="FieldEvaluator"/> for boundary crossing detection. Requires indexed fields.
    /// </summary>
    /// <remarks>
    /// Chained calls AND together, and every call must target the SAME component — a second call naming a different one throws. Predicates are merged into
    /// one branch set that records field names but not which component each came from, so a cross-component chain has no way to resolve correctly; see the
    /// guard below. To filter on a second component use <see cref="Where{T}"/>, which opens the entity and reads each component by its own type.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> is not registered, or a previous <c>WhereField</c> on this query targeted a different component.
    /// </exception>
    public EcsQuery<TArchetype> WhereField<T>(Expression<Func<T, bool>> predicate) where T : unmanaged
    {
        var ct = _tx.DBE.GetComponentTable<T>();
        if (ct == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T).Name} is not registered.");
        }

        // Reject a second WhereField on a DIFFERENT component instead of answering it wrongly. Each call's branches are cross-producted into one flat
        // FieldPredicate[][] below, and a FieldPredicate carries only a field NAME — never the component it came from — while _whereComponentTable is
        // overwritten by whichever call ran last. So every predicate ends up resolved against the LAST component: a name unique to the first component
        // throws deep in QueryResolverHelper, and a name both components share (Code, Score, Level...) resolves silently against the wrong one and the
        // query returns the wrong rows. The information is not mis-propagated, it is never captured, so there is nothing to recover at execution time.
        // Cross-component predicates ARE supported — through ExpressionParser.Parse<T1, T2>, which splits by lambda PARAMETER (see NavigationQueryBuilder).
        // Routing WhereField through that is a feature, tracked separately; until then, raising beats a silent wrong answer.
        if (_whereComponentTable != null && !ReferenceEquals(_whereComponentTable, ct))
        {
            throw new InvalidOperationException(
                $"WhereField cannot combine predicates on two different components ('{_whereComponentTable.Definition.Name}' then "
                + $"'{ct.Definition.Name}'). Chained WhereField calls must all target the same component. Use Where<T>(...) for the second component — "
                + "it evaluates per entity and composes correctly across components.");
        }

        var branches = ExpressionParser.ParseDnf(predicate);

        if (_fieldPredicateBranches != null)
        {
            // Multiple WhereField calls: cross-product (AND of ORs)
            var combined = new FieldPredicate[_fieldPredicateBranches.Length * branches.Length][];
            var idx = 0;
            for (var l = 0; l < _fieldPredicateBranches.Length; l++)
            {
                for (var r = 0; r < branches.Length; r++)
                {
                    var merged = new FieldPredicate[_fieldPredicateBranches[l].Length + branches[r].Length];
                    Array.Copy(_fieldPredicateBranches[l], merged, _fieldPredicateBranches[l].Length);
                    Array.Copy(branches[r], 0, merged, _fieldPredicateBranches[l].Length, branches[r].Length);
                    combined[idx++] = merged;
                }
            }
            _fieldPredicateBranches = combined;
        }
        else
        {
            _fieldPredicateBranches = branches;
        }

        _whereComponentTable = ct;
        _whereFieldReader = EcsViewFieldReader<T>.Instance;

        // Build fallback filter for pending spawns (read-your-own-writes).
        // Pending spawns have no secondary index entries — they can't be found by the targeted scan.
        // This compiled predicate is evaluated via tx.Open() + TryRead() for pending spawn entities only.
        // Kept separate from _whereFilter to avoid re-evaluating committed entities that the index already filtered.
        //
        // Deferred compilation: Expression.Compile() costs ~100+ µs. Since pending spawns are rare (only entities
        // spawned in the current, not-yet-committed transaction), defer compilation until the predicate is actually
        // needed. We store the expression as an untyped object and compile only on first invocation of the filter.
        // The compiled delegate is cached in a local captured by the closure.
        object predicateExpr = predicate;
        Func<T, bool> compiledPredicate = null;
        var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        var prevPendingFilter = _pendingSpawnFieldFilter;
        _pendingSpawnFieldFilter = prevPendingFilter == null ? (id, tx, readDisabled) =>
            {
                compiledPredicate ??= ((Expression<Func<T, bool>>)predicateExpr).Compile();
                if (!tx.TryOpen(id, out var entity))
                {
                    return false;
                }

                return entity.TryReadForPredicate<T>(readDisabled.Contains(typeId), out var value) && compiledPredicate(value);
            } : (id, tx, readDisabled) =>
            {
                if (!prevPendingFilter(id, tx, readDisabled))
                {
                    return false;
                }
                compiledPredicate ??= ((Expression<Func<T, bool>>)predicateExpr).Compile();
                if (!tx.TryOpen(id, out var entity))
                {
                    return false;
                }

                return entity.TryReadForPredicate<T>(readDisabled.Contains(typeId), out var value) && compiledPredicate(value);
            };

        return this;
    }

    /// <summary>True if this query has Expression-based field predicates (enabling incremental views).</summary>
    internal readonly bool HasFieldPredicates => _fieldPredicateBranches != null;

    /// <summary>
    /// Returns the sole DNF branch of the field predicate, or throws when the predicate is a multi-branch OR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An OR predicate is parsed into <c>N</c> disjunctive-normal-form branches, and the correct answer is the <b>union</b> of all of them. Every
    /// <i>one-shot</i> terminal (<c>Execute</c> / <c>Count</c> / <c>Any</c> / <c>ExecuteOrdered</c>) plans only <c>_fieldPredicateBranches[0]</c> — there is no
    /// union step — so branches <c>1..N</c> used to be discarded silently: <c>Count()</c> under-reported, <c>Any()</c> could return <c>false</c> while matches
    /// existed in a later branch, and <c>Execute()</c> returned a strict subset. Nothing threw (#590). <c>ExecuteOrdered</c> additionally had no guard at all
    /// despite the overview documenting one (#592).
    /// </para>
    /// <para>
    /// Until the one-shot path unions per-branch plans, throwing is the sanctioned resolution: a loud failure beats a partial answer the caller has no reason
    /// to distrust. The <b>view</b> path is unaffected and remains the supported way to run an OR query — <see cref="EcsView{TArchetype}"/> genuinely maintains
    /// per-branch bitmaps.
    /// </para>
    /// </remarks>
    /// <param name="terminal">Terminal name, for the exception message.</param>
    private readonly FieldPredicate[] SingleBranchOrThrow(string terminal)
    {
        if (_fieldPredicateBranches.Length > 1)
        {
            throw new InvalidOperationException(
                $"{terminal} does not support OR predicates — the predicate has {_fieldPredicateBranches.Length} DNF branches and the one-shot path evaluates "
                + "only the first, which would return a partial result. Use ToView() (incremental views evaluate every branch), or split the query into one "
                + "call per branch and union the results yourself.");
        }

        return _fieldPredicateBranches[0];
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial predicates
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Guard: the component carries <c>[SpatialIndex]</c>, so there is a tree to query. Unconditional, not strict-mode gated (#897).
    /// </summary>
    /// <typeparam name="T">The component named by the spatial predicate.</typeparam>
    /// <exception cref="InvalidOperationException">The component has no spatial index.</exception>
    /// <remarks>
    /// <para>
    /// <b>Why this one is not behind <see cref="CheckConfig.Enabled"/>.</b> That gate exists so a cheap user-facing check costs nothing when off, and
    /// the bargain it offers is "a diagnostic when strict mode is on, silence when it is off". Here the alternative to the diagnostic is not silence:
    /// the query dereferences a null index state and the user gets a bare <see cref="NullReferenceException"/> from inside the engine. That reads as an
    /// engine bug, so it gets filed as one instead of prompting the attribute that was missing. A misleading diagnostic is worse than none, which is
    /// what makes this check different from the others behind the gate.
    /// </para>
    /// <para>
    /// <b>And it costs nothing measurable.</b> A query builder is constructed once per query, never per entity, so this is one null test on a path that
    /// already did a dictionary lookup for the component table on the line above.
    /// </para>
    /// <para>
    /// The typed surface, <c>ClusterSpatialQuery&lt;TArch&gt;</c>, has always thrown here and named the fix. This brings the fluent surface to the same
    /// behaviour rather than inventing one.
    /// </para>
    /// </remarks>
    private readonly void RequireSpatialIndex<T>() where T : unmanaged
    {
        if (_spatialTable?.SpatialIndex == null)
        {
            ThrowNoSpatialIndex<T>();
        }
    }

    /// <summary>Out of line, so the message is not built into every inlined predicate call — the shape <c>ClusterSpatialQuery</c> uses.</summary>
    /// <typeparam name="T">The component named by the spatial predicate.</typeparam>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoSpatialIndex<T>() =>
        throw new InvalidOperationException(
            $"Component {typeof(T).Name} has no [SpatialIndex], so it cannot answer a spatial predicate "
            + "(WhereNearby / WhereInAABB / WhereRay / WhereFrustum). Put [SpatialIndex] on the component's AABB or bounding-sphere field, and ensure "
            + "ConfigureSpatialGrid was called on the engine before InitializeArchetypes.");

    /// <summary>Guard: at most one spatial predicate per query — a second call would silently overwrite the first.</summary>
    private readonly void ThrowIfSpatialAlreadySet()
    {
        if (_spatialQueryType != SpatialQueryType.None)
        {
            throw new InvalidOperationException(
                "Only one spatial predicate is allowed per query (WhereNearby / WhereInAABB / WhereRay / WhereFrustum). "
                + "Run separate queries for multiple regions.");
        }
    }

    /// <summary>
    /// Evaluates the query's spatial predicate (<c>WhereNearby</c> / <c>WhereInAABB</c> / <c>WhereRay</c> / <c>WhereFrustum</c>) in realm
    /// <paramref name="realm"/>: only that realm's entities match, whatever their coordinates. Realm 0 when never called. The realm must be registered;
    /// the check runs when the query executes.
    /// </summary>
    public EcsQuery<TArchetype> InRealm(RealmId realm)
    {
        _spatialRealm = realm;
        _realmNamed = true;
        return this;
    }

    // InRealm scopes the spatial predicate (SQ-08). Named without one it would be ignored and the query would answer every realm's entities: refused.
    private readonly void CheckRealmScope()
    {
        if (_realmNamed && _spatialQueryType == SpatialQueryType.None)
        {
            throw new InvalidOperationException(
                $"InRealm({_spatialRealm.Value}) scopes a spatial predicate (WhereNearby / WhereInAABB / WhereRay / WhereFrustum), and this query has none: "
                + "it would answer every realm's entities. To walk one realm's clusters, use GetClusterEnumerator(realm) on the archetype's accessor.");
        }
    }

    /// <summary>Filter by radius (sphere) around a center point. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereNearby<T>(double centerX, double centerY, double centerZ, double radius) where T : unmanaged
    {
        ThrowIfSpatialAlreadySet();
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        RequireSpatialIndex<T>();
        _spatialQueryType = SpatialQueryType.Radius;
        _spatialParams[0] = centerX; _spatialParams[1] = centerY; _spatialParams[2] = centerZ; _spatialParams[3] = radius;
        // Phase 7: ECS:Query:Spatial:Attach instant. queryBox encodes the bounding box of the radius sphere.
        TyphonEvent.EmitEcsQuerySpatialAttach((byte)SpatialQueryType.Radius, (float)(centerX - radius), (float)(centerY - radius), (float)(centerX + radius), (float)(centerY + radius));
        return this;
    }

    /// <summary>Filter by AABB overlap. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereInAABB<T>(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) where T : unmanaged
    {
        ThrowIfSpatialAlreadySet();
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        RequireSpatialIndex<T>();
        _spatialQueryType = SpatialQueryType.AABB;
        _spatialParams[0] = minX; _spatialParams[1] = minY; _spatialParams[2] = minZ;
        _spatialParams[3] = maxX; _spatialParams[4] = maxY; _spatialParams[5] = maxZ;
        // Phase 7: ECS:Query:Spatial:Attach instant — XY plane projection of the AABB for the wire payload.
        TyphonEvent.EmitEcsQuerySpatialAttach((byte)SpatialQueryType.AABB, (float)minX, (float)minY, (float)maxX, (float)maxY);
        return this;
    }

    /// <summary>Filter by ray intersection. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereRay<T>(double originX, double originY, double originZ, double dirX, double dirY, double dirZ, double maxDist)
        where T : unmanaged
    {
        ThrowIfSpatialAlreadySet();
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        RequireSpatialIndex<T>();
        _spatialQueryType = SpatialQueryType.Ray;
        _spatialParams[0] = originX; _spatialParams[1] = originY; _spatialParams[2] = originZ;
        _spatialParams[3] = dirX; _spatialParams[4] = dirY; _spatialParams[5] = dirZ; _spatialParams[6] = maxDist;
        // Phase 7: ECS:Query:Spatial:Attach instant — origin + endpoint XY projection.
        TyphonEvent.EmitEcsQuerySpatialAttach((byte)SpatialQueryType.Ray, (float)originX, (float)originY, (float)(originX + dirX * maxDist), (float)(originY + dirY * maxDist));
        return this;
    }

    /// <summary>
    /// Filter by a set of half-space planes — a camera frustum, or any convex region. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.
    /// </summary>
    /// <param name="planes">
    /// Packed <c>(normalX, normalY, [normalZ,] distance)</c> — <b>three</b> doubles per plane for a 2D spatial component, <b>four</b> for a 3D one. A point
    /// is inside a plane when <c>dot(n, p) + d &gt;= 0</c>, and inside the region when it is inside every plane.
    /// </param>
    /// <param name="planeCount">How many planes <paramref name="planes"/> holds.</param>
    /// <param name="boundsMinX">World-space minimum corner of a box containing the region.</param>
    /// <param name="boundsMinY">See <paramref name="boundsMinX"/>.</param>
    /// <param name="boundsMinZ">See <paramref name="boundsMinX"/>. Ignored for a 2D component.</param>
    /// <param name="boundsMaxX">World-space maximum corner of that box.</param>
    /// <param name="boundsMaxY">See <paramref name="boundsMaxX"/>.</param>
    /// <param name="boundsMaxZ">See <paramref name="boundsMaxX"/>. Ignored for a 2D component.</param>
    /// <remarks>
    /// <para><b>The bounding box is required, and it is the caller's to compute.</b> A set of half-spaces need not be bounded at all — six camera planes are,
    /// but three are not — so there is no general way to derive which cells to walk from the planes alone, and walking the whole grid to find out would cost
    /// more than the query. A camera frustum's box falls out of its eight corners. An over-generous box costs a few rejected cells, never wrong results; a box
    /// that does not contain the region is a false negative, so err wide.</para>
    /// </remarks>
    public EcsQuery<TArchetype> WhereFrustum<T>(ReadOnlySpan<double> planes, int planeCount, double boundsMinX, double boundsMinY, double boundsMinZ,
        double boundsMaxX, double boundsMaxY, double boundsMaxZ) where T : unmanaged
    {
        ThrowIfSpatialAlreadySet();
        if (planeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planeCount), planeCount, "A frustum query needs at least one plane.");
        }

        if (planeCount > MaxFrustumPlanes)
        {
            throw new ArgumentOutOfRangeException(nameof(planeCount), planeCount,
                $"A frustum query is limited to {MaxFrustumPlanes} planes; the query walks them in stack-allocated buffers.");
        }

        _spatialTable = _tx.DBE.GetComponentTable<T>();
        RequireSpatialIndex<T>();
        _spatialQueryType = SpatialQueryType.Frustum;
        _frustumPlanes = planes.ToArray();
        _frustumPlaneCount = planeCount;
        _spatialParams[0] = boundsMinX; _spatialParams[1] = boundsMinY; _spatialParams[2] = boundsMinZ;
        _spatialParams[3] = boundsMaxX; _spatialParams[4] = boundsMaxY; _spatialParams[5] = boundsMaxZ;
        TyphonEvent.EmitEcsQuerySpatialAttach((byte)SpatialQueryType.Frustum, (float)boundsMinX, (float)boundsMinY, (float)boundsMaxX, (float)boundsMaxY);
        return this;
    }

    /// <summary>
    /// Start a navigation (FK join) query from the source archetype to a target component type.
    /// The FK field selector identifies the long FK field on the source component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works against both persistent storage modes and both index homes. A cluster-backed archetype keeps its field indexes on the
    /// ARCHETYPE (values are packed cluster locations) while the rest use the shared component-table index (values are chunk ids);
    /// the reverse lookup scans whichever owns each candidate archetype. <see cref="StorageMode.Transient"/> is still rejected —
    /// it has no persistent index to navigate at all.
    /// </para>
    /// <para>
    /// Until issue #662 this resolved the foreign-key index off the component table only, so a <see cref="StorageMode.SingleVersion"/>
    /// source threw <see cref="NotSupportedException"/> (issue #623) — and, worse, a Versioned source in an archetype made
    /// cluster-eligible by a SingleVersion sibling silently returned nothing, because the guard tested the component's storage mode
    /// rather than the archetype's composition.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">The source component is <see cref="StorageMode.Transient"/>.</exception>
    public readonly EcsNavigationQueryBuilder<TArchetype, TSource, TTarget> NavigateField<TSource, TTarget>(Expression<Func<TSource, long>> fkSelector)
        where TSource : unmanaged where TTarget : unmanaged
    {
        var fkFieldName = ExpressionParser.ExtractFieldName(fkSelector);
        return new EcsNavigationQueryBuilder<TArchetype, TSource, TTarget>(this, _tx, fkFieldName);
    }

    /// <summary>Test if an archetype ID matches the query mask. Used by EcsView to filter delta entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool MaskTestPublic(ushort archetypeId) => MaskTest(archetypeId);

    /// <summary>
    /// Routing-id variant for long-lived callers such as <see cref="EcsView{TArchetype}"/>.
    /// Routing metadata belongs to the database, not to a transaction snapshot; taking the engine explicitly avoids
    /// dereferencing the pooled transaction that originally constructed this query after its lease has ended (#862).
    /// </summary>
    internal readonly bool MaskTestPublicByRouting(DatabaseEngine dbe, ushort routingId)
    {
        var meta = dbe.GetMetaByRouting(routingId);
        return meta != null && MaskTest(meta.ArchetypeId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OrderBy / Skip / Take
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Order results by an indexed field. Requires <see cref="WhereField{T}"/> to identify the component.</summary>
    public EcsQuery<TArchetype> OrderByField<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        _orderBy = ResolveOrderBy(keySelector, false);
        return this;
    }

    /// <summary>Order results descending by an indexed field.</summary>
    public EcsQuery<TArchetype> OrderByFieldDescending<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        _orderBy = ResolveOrderBy(keySelector, true);
        return this;
    }

    /// <summary>Skip the first <paramref name="count"/> results. Requires OrderBy.</summary>
    public EcsQuery<TArchetype> Skip(int count)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("Skip requires OrderByField.");
        }
        _skip = count;
        return this;
    }

    /// <summary>Take at most <paramref name="count"/> results. Requires OrderBy.</summary>
    public EcsQuery<TArchetype> Take(int count)
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("Take requires OrderByField.");
        }
        _take = count;
        return this;
    }

    private OrderByField ResolveOrderBy<T, TKey>(Expression<Func<T, TKey>> keySelector, bool descending) where T : unmanaged
    {
        if (_whereComponentTable == null)
        {
            throw new InvalidOperationException("OrderByField requires WhereField to be called first to identify the component table.");
        }
        var fieldName = ExpressionParser.ExtractFieldName(keySelector);
        if (!_whereComponentTable.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on component '{_whereComponentTable.Definition.Name}'.");
        }
        if (!field.HasIndex)
        {
            throw new InvalidOperationException($"Field '{fieldName}' must be indexed to use as OrderBy.");
        }

        // A field no ordered tree key encodes cannot order a query. Accepted, it sorted by entity key without a word (QORD-01).
        if (!QueryResolverHelper.TryMapFieldTypeToKeyType(field.Type, out var keyType) || !KeyRange.IsStreamable(keyType))
        {
            throw new InvalidOperationException($"Field '{fieldName}' ({field.Type}) cannot order a query: its index has no ordered key.");
        }

        return new OrderByField(QueryResolverHelper.FindFieldIndex(_whereComponentTable.Definition, field), descending: descending, keyType: keyType,
            keyTypeKnown: true);
    }

    /// <summary>Resolve T2 masks for a specific archetype.</summary>
    private bool ResolveT2Masks(ArchetypeMetadata meta, out ushort requiredEnabled, out ushort requiredDisabled)
    {
        requiredEnabled = 0;
        requiredDisabled = 0;

        for (var i = 0; i < _enabledTypeIdCount; i++)
        {
            var typeId = i switch { 0 => _enabledTypeId0, 1 => _enabledTypeId1, 2 => _enabledTypeId2, _ => _enabledTypeId3 };
            if (!meta.TryGetSlot(typeId, out var slot))
            {
                return false;
            }
            requiredEnabled |= (ushort)(1 << slot);
        }

        for (var i = 0; i < _disabledTypeIdCount; i++)
        {
            var typeId = i switch { 0 => _disabledTypeId0, 1 => _disabledTypeId1, 2 => _disabledTypeId2, _ => _disabledTypeId3 };
            if (!meta.TryGetSlot(typeId, out var slot))
            {
                continue;
            }
            requiredDisabled |= (ushort)(1 << slot);
        }

        return true;
    }

    /// <summary>
    /// The enabled-state gate of this query's field predicate on one archetype (QENABLE-01): false — the archetype contributes nothing — when a component
    /// the query requires enabled is not in it, as the broad scan decides through <see cref="ResolveT2Masks"/>.
    /// </summary>
    private bool TryCreateEnabledGate(ArchetypeMetadata meta, ArchetypeClusterInfo layout, int predicateSlot, out EnabledGate gate)
    {
        gate = default;
        ushort namedEnabled = 0, namedDisabled = 0;
        if (HasT2 && !ResolveT2Masks(meta, out namedEnabled, out namedDisabled))
        {
            return false;
        }

        gate = EnabledGate.Create(meta, _tx.DBE._archetypeStates[meta.ArchetypeId], layout, predicateSlot, namedEnabled, namedDisabled, _tx);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Execution — broad scan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a persistent, refreshable View from this query.
    /// If Expression-based WHERE (WhereField) was used, creates an incremental view with ring buffer delta notifications.
    /// Otherwise, creates a pull-model view (full re-query on each Refresh).
    /// </summary>
    /// <remarks>
    /// The three trailing <c>caller…</c> parameters are populated by <c>[CallerFilePath]</c> / <c>[CallerLineNumber]</c> / <c>[CallerMemberName]</c>
    /// at the user's <c>.ToView()</c> call site and become the View's definition-site source location (see <see cref="ViewBase.SourceFile"/>).
    /// </remarks>
    public EcsView<TArchetype> ToView(
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity,
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        CheckRealmScope();
        if (_orderBy.HasValue)
        {
            throw new InvalidOperationException("A View is unordered; OrderBy / Skip / Take are not supported on ToView().");
        }

        if (HasFieldPredicates)
        {
            if (_whereFilter != null)
            {
                throw new InvalidOperationException(
                    "An incremental view (WhereField) does not apply a chained .Where(lambda). Fold the condition into WhereField, " +
                    "or drop WhereField to build a pull view from .Where(...).");
            }
            if (_spatialQueryType != SpatialQueryType.None)
            {
                throw new InvalidOperationException(
                    "A view cannot combine WhereField with a spatial predicate (WhereNearby / WhereInAABB / WhereRay / WhereFrustum).");
            }

            // Enabled<T> / Disabled<T> beside a field predicate: the incremental drain judges an entry by its component's own enabled bit alone, while the
            // population honours every constraint (QENABLE-01) — the view would drift from its own refresh. A pull view re-runs the query and agrees.
            if (HasT2)
            {
                return ToPullView(bufferCapacity, callerFile, callerLine, callerMethod);
            }

            return ToIncrementalView(bufferCapacity, callerFile, callerLine, callerMethod);
        }

        // Pull mode: no field evaluators (spatial / Where(lambda) are honoured by Execute() inside ToPullView).
        return ToPullView(bufferCapacity, callerFile, callerLine, callerMethod);
    }

    /// <summary>
    /// True when this query's result IS the whole live membership of its archetype set, so an unfiltered view over it can be maintained from
    /// the per-archetype membership channel instead of a re-query (#790).
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than "took the pull path". A <c>.Where(lambda)</c> predicate is an opaque delegate over component values and a
    /// spatial predicate depends on position — both change membership on ordinary component writes that emit no spawn and no destroy, so the
    /// channel would report a fraction of their real changes and the view would be quietly wrong. They keep the honest re-query.
    /// The archetype mask itself is a static property of each archetype, so narrowing it with <c>.With</c>/<c>.Without</c> is still membership.
    /// </remarks>
    private readonly bool IsMembershipQuery => !HasFieldPredicates && _whereFilter == null && _spatialQueryType == SpatialQueryType.None && !HasT2;

    /// <summary>
    /// The membership channels of every archetype this query's mask selects. A root-archetype query spans its whole subtree, so membership is
    /// the union of their live sets and the view must subscribe to each.
    /// </summary>
    private ArchetypeMembershipRegistry[] CollectMembershipRegistries()
    {
        var dbe = _tx.DBE;
        var maxId = MaskMaxId;
        var found = new List<ArchetypeMembershipRegistry>(4);
        for (var archBit = 0; archBit <= maxId; archBit++)
        {
            if (!MaskTest((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }

            var es = dbe._archetypeStates[meta.ArchetypeId];
            if (es != null)
            {
                found.Add(es.MembershipViews);
            }
        }
        return found.ToArray();
    }

    private EcsView<TArchetype> ToPullView(int bufferCapacity, string callerFile, int callerLine, string callerMethod)
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
        var firstTable = engineState.SlotToComponentTable[0];

        var view = new EcsView<TArchetype>(this, firstTable.DBE, firstTable.DBE.MemoryAllocator, firstTable, bufferCapacity, _tx.TSN, callerFile, callerLine,
            callerMethod);

        // Subscribe BEFORE the initial scan, for the same reason ToIncrementalView registers before its population: a commit landing between
        // the two would otherwise reach neither the scan nor the buffer, and the entity would be missing until something unrelated forced a
        // full refresh. The reverse overlap is harmless — an entity in both the scan and the buffer re-adds idempotently and yields no delta.
        // Everything from the subscription onward can throw — Execute runs a plan, and on two of the three pull shapes it runs USER code (the
        // .Where delegate, the spatial predicates). A throw after subscribing would leave a view nobody holds, so nobody can Dispose it, so
        // nothing ever deregisters it: a permanently-subscribed orphan whose pinned ring buffer every future commit on those archetypes appends
        // into. Before the subscription moved above Execute() the same throw simply propagated having allocated nothing.
        try
        {
            if (IsMembershipQuery)
            {
                var registries = CollectMembershipRegistries();
                if (registries.Length > 0)
                {
                    view.SubscribeToMembership(registries);
                }
            }

            var initialSet = Execute();

            // Pre-size the entity-id set to the exact final count: initialSet is a HashSet (all keys distinct) and the View's map is fresh, so every key is a
            // genuine add. This collapses the ~log2(count/64) incremental resizes of the populate loop into a single right-sized POH allocation.
            view.EntityIdsInternal.EnsureCapacity(initialSet.Count);

            // Populate initial entity set
            foreach (var id in initialSet)
            {
                view.AddEntityDirect((long)id.RawValue);
            }
        }
        catch
        {
            view.Dispose();
            throw;
        }

        return view;
    }

    private EcsView<TArchetype> ToIncrementalView(int bufferCapacity, string callerFile, int callerLine, string callerMethod)
    {
        var ct = _whereComponentTable;
        var branches = _fieldPredicateBranches;

        if (branches.Length > 1)
        {
            // OR path: create EcsOrView
            return ToOrView(ct, branches, bufferCapacity, callerFile, callerLine, callerMethod);
        }

        // Single AND branch
        var evaluators = QueryResolverHelper.ResolveEvaluators(branches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlanAttributed(evaluators, ct, PlannerStats(ct), AdvancedSelectivityEstimator.Instance, null,
            1, (uint)EcsQueryId, SourceFile, SourceLine, SourceMethod, callerFile, callerLine, callerMethod);

        var view = new EcsView<TArchetype>(this, evaluators, ct, _whereFieldReader, plan, bufferCapacity, _tx.TSN, callerFile, callerLine, callerMethod);

        // Register with ViewRegistry for delta notifications
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        // Initial population. Must go through the cross-archetype scan, not IFieldReader.ExecuteFullScan directly: a cluster-backed archetype keeps its
        // indexes on the archetype, so the ComponentTable tree this plan targets is empty and the view would populate to nothing (#663).
        ExecuteFullScanAcrossArchetypes(plan, plan.OrderedEvaluators, ct, view.EntityIdsInternal);

        // Process any deltas that arrived during population
        view.RefreshFromScheduler(_tx);
        view.ClearDelta();
        view.OweRefreshFull();

        return view;
    }

    private EcsView<TArchetype> ToOrView(ComponentTable ct, FieldPredicate[][] branches, int bufferCapacity, string callerFile, int callerLine, string callerMethod)
    {
        var branchEvaluators = new FieldEvaluator[branches.Length][];
        var plans = new ExecutionPlan[branches.Length];
        for (var b = 0; b < branches.Length; b++)
        {
            branchEvaluators[b] = QueryResolverHelper.ResolveEvaluators(branches[b], ct, 0, (byte)b);
            plans[b] = PlanBuilder.Instance.BuildPlanAttributed(branchEvaluators[b], ct, PlannerStats(ct), AdvancedSelectivityEstimator.Instance, null,
                1, (uint)EcsQueryId, SourceFile, SourceLine, SourceMethod, callerFile, callerLine, callerMethod);
        }

        var view = new EcsView<TArchetype>(this, branchEvaluators, plans, ct, _whereFieldReader, bufferCapacity, _tx.TSN, callerFile, callerLine, callerMethod);
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        view.PopulateInitialOr(_tx);
        view.RefreshFromScheduler(_tx);
        view.ClearDelta();
        view.OweRefreshFull();

        return view;
    }

    /// <summary>Rebind this query to a different transaction (different TSN → different visibility).</summary>
    internal void UpdateTransaction(Transaction tx) => _tx = tx;

    /// <summary>
    /// Drop the borrowed transaction when this query definition is retained by a long-lived view. View paths rebind explicitly around
    /// snapshot-dependent execution; keeping the creator lease here would let a pooled Transaction escape its lifetime (#862).
    /// </summary>
    internal void DetachTransaction() => _tx = null;

    /// <summary>
    /// True while this query holds a borrowed <see cref="Transaction"/>. A query being executed legitimately holds one; the copy a long-lived
    /// <see cref="EcsView{TArchetype}"/> retains must not between operations (VIEW-01), and nothing but a test asserts on this.
    /// </summary>
    internal readonly bool HoldsTransaction => _tx != null;

    /// <summary>Execute the query and collect matching entity IDs into a HashSet.</summary>
    public HashSet<EntityId> Execute(
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site; consumed by trace emission in P2 (issue #335).
        _ = callerFile; _ = callerLine; _ = callerMethod;
        CheckRealmScope();
        if (_orderBy.HasValue)
        {
            throw new InvalidOperationException(
                "Execute() returns an unordered set and ignores OrderBy / Skip / Take. Use ExecuteOrdered() for ordered or paged results.");
        }
        var scope = TyphonEvent.BeginEcsQueryExecute(0);
        try
        {
            var result = new HashSet<EntityId>(_take > 0 ? _take : 64);
            if (MaskIsEmpty)
            {
                scope.ScanMode = EcsQueryScanMode.Empty;
                scope.ResultCount = 0;
                return result;
            }

            // Spatial-driven scan: the spatial index produces candidates; ExecuteSpatial then ANDs any field/Where
            // predicate per-candidate. Checked before the field-only path so a combined spatial + WhereField query
            // applies BOTH predicates instead of silently dropping the spatial one.
            if (_spatialQueryType != SpatialQueryType.None)
            {
                var spatial = ExecuteSpatial();
                scope.ScanMode = EcsQueryScanMode.Spatial;
                scope.ResultCount = spatial.Count;
                return spatial;
            }

            // Targeted scan via PipelineExecutor when field predicates are present (and no spatial predicate)
            if (HasFieldPredicates)
            {
                var targeted = ExecuteTargeted(callerFile, callerLine, callerMethod);
                scope.ScanMode = EcsQueryScanMode.Targeted;
                scope.ResultCount = targeted.Count;
                return targeted;
            }

            CollectMatching((id, _) => result.Add(id));

            // T3 post-filter: evaluate WHERE predicate per entity via Transaction.Open
            var filter = _whereFilter;
            var tx = _tx;
            var readDisabled = DisabledNames;
            if (filter != null)
            {
                result.RemoveWhere(id => !filter(id, tx, readDisabled));
            }

            scope.ScanMode = EcsQueryScanMode.Broad;
            scope.ResultCount = result.Count;
            return result;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Execute the query with ordering support. Requires <see cref="OrderByField{T,TKey}"/>.</summary>
    public List<EntityId> ExecuteOrdered(
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site; consumed by trace emission in P2 (issue #335).
        _ = callerFile; _ = callerLine; _ = callerMethod;
        CheckRealmScope();
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("ExecuteOrdered requires OrderByField.");
        }
        if (!HasFieldPredicates)
        {
            throw new InvalidOperationException("ExecuteOrdered requires WhereField to identify the component table.");
        }
        if (_spatialQueryType != SpatialQueryType.None)
        {
            throw new InvalidOperationException(
                "ExecuteOrdered() does not support spatial predicates. Run a spatial Execute() (unordered) and sort the result yourself.");
        }
        if (_whereFilter != null)
        {
            throw new InvalidOperationException("ExecuteOrdered() does not apply .Where(lambda) — fold the condition into WhereField.");
        }
        if (MaskIsEmpty)
        {
            return [];
        }

        var ct = _whereComponentTable;
        var evaluators = QueryResolverHelper.ResolveEvaluators(SingleBranchOrThrow("ExecuteOrdered()"), ct, 0);
        var plan = PlanBuilder.Instance.BuildPlanAttributed(evaluators, ct, PlannerStats(ct), AdvancedSelectivityEstimator.Instance, _orderBy.Value,
            1, (uint)EcsQueryId, SourceFile, SourceLine, SourceMethod, callerFile, callerLine, callerMethod);

        // Every archetype whose where-component owns a per-archetype tree can be K-way merged; anything else falls back to scan-then-sort. The third case this
        // used to have — a PipelineExecutor ordered scan over the shared ComponentTable tree — is gone with that tree (#629). It could only ever have returned
        // an empty list once the trees stopped being maintained, so the fallback is strictly the better answer, not merely the surviving one.
        var allArchetypesIndexOnArchetype = true;
        var dbe = _tx.DBE;
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!MaskTest(meta.ArchetypeId))
            {
                continue;
            }

            var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
            if (clusterState?.IndexSlots == null || !meta.HasClusterIndexes)
            {
                allArchetypesIndexOnArchetype = false;
                break;
            }

        }

        return allArchetypesIndexOnArchetype ? ExecuteOrderedClustered(plan, evaluators) : ExecuteOrderedViaSortFallback(evaluators, plan);
    }

    /// <summary>
    /// Ordered execution for cluster-only archetypes using K-way merge over per-archetype B+Trees.
    /// Each archetype's B+Tree yields results in key order; the merge interleaves them in global sort order.
    /// </summary>
    private List<EntityId> ExecuteOrderedClustered(ExecutionPlan plan, FieldEvaluator[] evaluators)
    {
        // #1347: a merge a destroy raced is answered again, its clusters now read whole; past the attempts, the scans answer and the result is sorted.
        for (var attempt = 0; attempt < MaxOrderedMergeAttempts; attempt++)
        {
            var result = MergeOrderedClustered(plan, evaluators, out var raced);
            if (!raced)
            {
                return result;
            }
        }

        return ExecuteOrderedViaSortFallback(evaluators, plan);
    }

    /// <summary>
    /// The slots of <paramref name="candidates"/> whose component at <paramref name="compBase"/> matches every evaluator: the SIMD-eligible ones over the
    /// whole cluster, the others per remaining slot. An evaluator on field <paramref name="enforcedField"/> is skipped for a slot not in
    /// <paramref name="fenceDirty"/> — the selective scan's tree range already decided it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MatchingSlots(FieldEvaluator[] evaluators, ReadOnlySpan<bool> simdEligible, bool anySimd, byte* compBase, int compSize,
        int clusterSize, ulong candidates, ulong fenceDirty, int enforcedField)
    {
        var bits = candidates;
        var scalarLeft = !anySimd;
        if (anySimd)
        {
            for (var e = 0; e < evaluators.Length; e++)
            {
                if (!simdEligible[e])
                {
                    scalarLeft = true;
                }
                else if (bits != 0)
                {
                    bits &= SimdPredicateEvaluator.EvaluateCluster(ref evaluators[e], compBase, compSize, clusterSize);
                }
            }
        }

        // Every evaluator ran over the whole cluster: nothing is left to verify per slot, and the mask is the answer — a second pass over the matches cost
        // the no-update scan ~8 %.
        if (!scalarLeft)
        {
            return bits;
        }

        var matching = 0UL;
        for (; bits != 0; bits &= bits - 1)
        {
            var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
            var entityComp = compBase + slotIndex * compSize;
            var enforced = (fenceDirty & (1UL << slotIndex)) == 0 ? enforcedField : int.MinValue;
            var pass = true;
            for (var e = 0; e < evaluators.Length; e++)
            {
                if ((anySimd && simdEligible[e]) || evaluators[e].FieldIndex == enforced)
                {
                    continue;
                }

                if (!FieldEvaluator.Evaluate(ref evaluators[e], entityComp + evaluators[e].FieldOffset))
                {
                    pass = false;
                    break;
                }
            }

            if (pass)
            {
                matching |= 1UL << slotIndex;
            }
        }

        return matching;
    }

    /// <summary>How many times an ordered merge that a destroy raced is answered again before the sort fallback answers it (#1347).</summary>
    private const int MaxOrderedMergeAttempts = 3;

    /// <summary>
    /// One K-way merge of the ordered query. <paramref name="raced"/>: a destroy newer than the snapshot published while the streams read their trees, so a
    /// row the snapshot sees may have left a tree before its page was read — the result is not to be trusted.
    /// </summary>
    /// <remarks>
    /// The streams read their trees lazily, page by page, so the order the selective scan relies on — the tree read first, the destroy stamp after it — is
    /// not available here. The stamp is read first instead, and again once the merge is done: a destroy that removed a row's entries before the stream read
    /// them folded its stamp before that (FlushPendingDestroys), so the second read sees it changed.
    /// </remarks>
    private List<EntityId> MergeOrderedClustered(ExecutionPlan plan, FieldEvaluator[] evaluators, out bool raced)
    {
        raced = false;
        var dbe = _tx.DBE;
        var txTsn = _tx.TSN;
        // Use rented array instead of List + ToArray to avoid redundant allocations.
        // Typical K is 1-3 archetypes; rent 8 to avoid resize in common cases.
        var streams = ArrayPool<ArchetypeSortedStream>.Shared.Rent(8);
        var streamCount = 0;

        // #1347: the destroy stamp each snapshot-isolated archetype had when its streams opened, checked again after the merge.
        var stampStates = ArrayPool<ArchetypeClusterState>.Shared.Rent(8);
        var stamps = ArrayPool<long>.Shared.Rent(8);
        var stampCount = 0;

        // VIS-12: each archetype read at the snapshot — its update count at the merge's first read, the clusters it read whole — checked after the merge.
        ArchetypeClusterState[] updateStates = null;
        long[] updateMarks = null;
        UpdatedClusterSet[] updateSets = null;
        var updateCount = 0;

        // The rented entity-key sets of the side streams, given back once the merge is done with them.
        long[][] sidePkSets = null;
        var sidePkSetCount = 0;

        // Where this transaction's own pending spawns keep the ordered field, by archetype: they are in no tree yet, and get a stream of their own.
        List<(ArchetypeMetadata Meta, int CompSlot, int FieldOffset, KeyType KeyType)> ownSpawnSources = null;

        // The plan's PrimaryFieldIndex may be -1 when the shared B+Tree has 0 entries (cluster archetypes store entries in per-archetype B+Trees,
        // not the shared one). In that case, use the OrderBy field index directly and full type range for scan bounds.
        Debug.Assert(_orderBy.HasValue, "ExecuteOrderedClustered requires OrderBy to be set");
        var orderByFieldIdx = _orderBy.Value.FieldIndex;
        var descending = plan.Descending;
        var primaryFieldIdx = plan.PrimaryFieldIndex >= 0 ? plan.PrimaryFieldIndex : orderByFieldIdx;

        // The merge evaluates no predicate: a row is returned because the scan range admitted it (the streams re-check only entities written since the last
        // fence, QFENCE-01). So every predicate must be one the range enforces exactly, or the query falls back to ExecuteTargeted (which verifies all
        // evaluators) + sort. A predicate on another field never is: let through, it typed no stream at all and answered nothing (QORD-01). Neither is one
        // ComputeBounds widens on the scan field — `!=` above all, which used to be exempted here and was then never evaluated (#1185).
        for (var e = 0; e < evaluators.Length; e++)
        {
            if (evaluators[e].FieldIndex != primaryFieldIdx || !KeyRange.AdmitsOnlyMatches(ref evaluators[e], evaluators[e].KeyType))
            {
                ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
                ArrayPool<ArchetypeClusterState>.Shared.Return(stampStates, true);
                ArrayPool<long>.Shared.Return(stamps);
                return ExecuteOrderedViaSortFallback(evaluators, plan);
            }
        }

        var sparseZero = false;
        try
        {
            // Open a sorted stream for each matching cluster archetype
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                if (!MaskTest(meta.ArchetypeId) || !meta.HasClusterIndexes)
                {
                    continue;
                }

                var engineState = dbe._archetypeStates[meta.ArchetypeId];
                var clusterState = engineState?.ClusterState;
                if (clusterState?.IndexSlots == null)
                {
                    continue;
                }

                // A Transient home is skipped: ArchetypeSortedStream streams a BTreeBase<PersistentStore>. Skipping falls through to the generic sort, which
                // is correct but not index-accelerated — ordered streaming over the Transient home is a #655 follow-up, tracked in #665.
                var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
                if (ixSlotIdx < 0 || transientHome)
                {
                    continue;
                }

                ref var matchSlot = ref clusterState.IndexSlots[ixSlotIdx];

                // Determine which field's B+Tree to scan for ordering.
                // If the plan selected a secondary index (PrimaryFieldIndex >= 0), use it.
                // Otherwise, use the OrderBy field index directly (the shared B+Tree had 0 entries).
                var fieldIdx = plan.PrimaryFieldIndex >= 0 ? plan.PrimaryFieldIndex : orderByFieldIdx;
                if (fieldIdx < 0 || fieldIdx >= matchSlot.Fields.Length)
                {
                    continue;
                }

                ref var field = ref matchSlot.Fields[fieldIdx];

                // QENABLE-01: the stream admits only the slots the enabled gate does, as the unordered paths.
                if (!TryCreateEnabledGate(meta, clusterState.Layout, matchSlot.Slot, out var gate))
                {
                    continue;
                }

                // Determine scan bounds and key type
                long scanMin, scanMax;
                KeyType keyType;
                if (plan.PrimaryFieldIndex >= 0)
                {
                    // Plan has valid bounds from the shared B+Tree estimator
                    scanMin = plan.PrimaryScanMin;
                    scanMax = plan.PrimaryScanMax;
                    keyType = plan.PrimaryKeyType;
                }
                else
                {
                    // Plan selected nothing: the ordering field's own key type (QORD-01), and bounds from the predicates that name it.
                    keyType = _orderBy.Value.KeyType;

                    // A field no tree key encodes cannot be streamed. Typed with a default Int and bounds left at long.MinValue/MaxValue, the scan truncated
                    // them to (int)0..(int)-1 — an INVERTED range that enumerated nothing. Skipping says the same thing without pretending a stream was built.
                    if (!_orderBy.Value.KeyTypeKnown || !KeyRange.IsStreamable(keyType))
                    {
                        continue;
                    }

                    scanMin = KeyRange.TypeMin(keyType);
                    scanMax = KeyRange.TypeMax(keyType);

                    // Intersect bounds with all evaluators on this field (e.g., Score >= 50 narrows scanMin)
                    KeyRange.Intersect(evaluators, fieldIdx, keyType, ref scanMin, ref scanMax);
                }

                // A sparse tree holds no key 0 (IX-09): streamed over a range admitting it, the merge would drop every entity holding 0.
                if (field.Index.Sparse && KeyRange.AdmitsZero(keyType, scanMin, scanMax))
                {
                    sparseZero = true;
                    break;
                }

                // #1347: an archetype that promises snapshot isolation streams at the reader's snapshot. A destroy newer than it removed entries the
                // snapshot still sees, so each cluster such a death touched is read whole into a sorted side stream, and the tree stream drops its rows.
                StreamSnapshot? snapshot = null;
                if (meta.VersionedSlotMask != 0)
                {
                    var destroyStamp = clusterState.MaxDestroyStamp;
                    QueryPathProbe.OrderedBeforeStreamsHook?.Invoke();
                    Grow(ref stampStates, stampCount);
                    Grow(ref stamps, stampCount);
                    stampStates[stampCount] = clusterState;
                    stamps[stampCount++] = destroyStamp;

                    // VIS-12 (#674): the clusters an update newer than the snapshot touched, read whole at the snapshot too; the archetype's update count is
                    // read again after the merge, whose tree reads are lazy.
                    UpdatedClusterSet updated = default;
                    if ((meta.VersionedSlotMask & (1 << matchSlot.Slot)) != 0)
                    {
                        updated = ClustersUpdatedAfterSnapshot(clusterState, out var updateMark);
                        QueryPathProbe.OrderedAfterUpdateReadHook?.Invoke();
                        Grow(ref updateStates, updateCount);
                        Grow(ref updateMarks, updateCount);
                        Grow(ref updateSets, updateCount);
                        updateStates[updateCount] = clusterState;
                        updateSets[updateCount] = updated;
                        updateMarks[updateCount++] = updateMark;
                    }

                    long[] sidePks = null;
                    var sidePkCount = 0;
                    if (destroyStamp > txTsn || !updated.IsEmpty)
                    {
                        Grow(ref streams, streamCount);
                        streams[streamCount++] = ReadClustersWhole(evaluators, clusterState, engineState, meta, matchSlot.Slot, field.FieldOffset, keyType,
                            descending,
                            ref gate, updated, out sidePks, out sidePkCount);
                        sidePkSets ??= ArrayPool<long[]>.Shared.Rent(4);
                        Grow(ref sidePkSets, sidePkSetCount);
                        sidePkSets[sidePkSetCount++] = sidePks;
                    }

                    snapshot = new StreamSnapshot
                    {
                        State = engineState, Cards = dbe.CommitCards, TxTsn = txTsn, RecordSize = meta._entityRecordSize,
                        SidePks = sidePkCount > 0 ? sidePks : null, SidePkCount = sidePkCount
                    };
                }

                // Grow rented array if needed (rare — most queries match 1-3 archetypes)
                Grow(ref streams, streamCount);

                // No per-stream entry cap: the stream is a live cursor now, so a stream the merge stops consuming stops reading. The cap this used to pass
                // (skip+take) was the bound on how much each stream drained EAGERLY, and it is what made an ordered Take cost K times what it emitted.
                // QFENCE-01: the stream re-checks the entities written since the last fence against these evaluators — see ArchetypeSortedStream.
                streams[streamCount++] = ArchetypeSortedStream.Create(field.Index, keyType, scanMin, scanMax, descending, clusterState, clusterState.Layout,
                    evaluators, matchSlot.Slot, gate, snapshot);
                if (_tx.PendingSpawns is { Count: > 0 })
                {
                    (ownSpawnSources ??= []).Add((meta, matchSlot.Slot, field.FieldOffset, keyType));
                }
            }

            if (sparseZero)
            {
                // The sort fallback reads component data, which holds the 0s.
                for (var i = 0; i < streamCount; i++)
                {
                    streams[i].Dispose();
                }

                ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
                streams = null;
                return ExecuteOrderedViaSortFallback(evaluators, plan);
            }

            // Read-your-own-writes: the spawns this transaction matched, by their own values, merged like any archetype's rows.
            if (ownSpawnSources != null)
            {
                var ownStream = OwnSpawnStream(ownSpawnSources, descending);
                if (ownStream.HasValue)
                {
                    Grow(ref streams, streamCount);
                    streams[streamCount++] = ownStream.Value;
                }
            }

            if (streamCount == 0)
            {
                ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
                return [];
            }

            // KWayMergeState takes ownership of the streams array (ownsArray: true → returns to pool on Dispose)
            var merge = KWayMergeState.Create(streams, streamCount, descending, true);
            streams = null;
            streamCount = 0;
            List<EntityId> merged;
            try
            {
                merged = CollectMergedResults(ref merge, evaluators);
            }
            finally
            {
                merge.Dispose();
            }

            // The tree reads before the stamp's second read, on arm64 too (see the selective scan's walk); JIT-folded on x64.
            if (!X86Base.IsSupported)
            {
                Interlocked.MemoryBarrier();
            }

            for (var i = 0; i < stampCount; i++)
            {
                var now = stampStates[i].MaxDestroyStamp;
                if (now != stamps[i] && now > txTsn)
                {
                    raced = true;
                }
            }

            // An update newer than the snapshot published while the streams read their trees: a row may have moved under them (VIS-12).
            for (var i = 0; i < updateCount && !raced; i++)
            {
                raced = UpdatedSince(updateStates[i], updateMarks[i], updateSets[i]);
            }

            return merged;
        }
        catch
        {
            // Dispose streams on failure path
            if (streams != null)
            {
                for (var i = 0; i < streamCount; i++)
                {
                    streams[i].Dispose();
                }
                ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
            }
            throw;
        }
        finally
        {
            ArrayPool<ArchetypeClusterState>.Shared.Return(stampStates, true);
            ArrayPool<long>.Shared.Return(stamps);
            if (updateStates != null)
            {
                ArrayPool<ArchetypeClusterState>.Shared.Return(updateStates, true);
                ArrayPool<long>.Shared.Return(updateMarks);
                for (var i = 0; i < updateCount; i++)
                {
                    updateSets[i].Return();
                }

                ArrayPool<UpdatedClusterSet>.Shared.Return(updateSets, true);
            }

            if (sidePkSets != null)
            {
                for (var i = 0; i < sidePkSetCount; i++)
                {
                    ArrayPool<long>.Shared.Return(sidePkSets[i]);
                }

                ArrayPool<long[]>.Shared.Return(sidePkSets, true);
            }
        }
    }

    /// <summary>
    /// This transaction's own pending spawns the query matches (the same filter as <see cref="CollectPendingSpawnsWithFieldFilter"/>), sorted by the ordered
    /// field as their point reads see it, into a side stream; <c>null</c> when none matches.
    /// </summary>
    private ArchetypeSortedStream? OwnSpawnStream(List<(ArchetypeMetadata Meta, int CompSlot, int FieldOffset, KeyType KeyType)> sources, bool descending)
    {
        var matched = new HashSet<EntityId>();
        CollectPendingSpawnsWithFieldFilter(matched);
        if (matched.Count == 0)
        {
            return null;
        }

        var keys = ArrayPool<long>.Shared.Rent(matched.Count);
        var pks = ArrayPool<long>.Shared.Rent(matched.Count);
        var count = 0;
        foreach (var id in matched)
        {
            var meta = _tx.DBE.GetMetaByRouting(id.ArchetypeId);
            foreach (var s in sources)
            {
                if (ReferenceEquals(s.Meta, meta) && TryReadOwnSpawnOrderKey(id, s.CompSlot, s.FieldOffset, s.KeyType, out var key))
                {
                    keys[count] = key;
                    pks[count++] = (long)id.RawValue;
                    break;
                }
            }
        }

        Array.Sort(keys, pks, 0, count);
        if (descending)
        {
            Array.Reverse(keys, 0, count);
            Array.Reverse(pks, 0, count);
        }

        return ArchetypeSortedStream.FromSorted(keys, pks, count);
    }

    /// <summary>
    /// The ordered field's key of one of this transaction's own pending spawns, as its point read sees it: a spawn is in no tree and in no cluster until
    /// it commits.
    /// </summary>
    private bool TryReadOwnSpawnOrderKey(EntityId id, int compSlot, int fieldOffset, KeyType keyType, out long orderedKey)
    {
        orderedKey = 0;
        if (!_tx.TryOpen(id, out var entity))
        {
            return false;
        }

        var size = entity.GetComponentSize(compSlot);
        Span<byte> bytes = size <= 256 ? stackalloc byte[256] : new byte[size];
        if (entity.ReadRaw(compSlot, bytes) < fieldOffset + 1)
        {
            return false;
        }

        orderedKey = OrderedKeyEncoding.EncodeAt(bytes[fieldOffset..], keyType);
        return true;
    }

    /// <summary>Grows a rented array so index <paramref name="count"/> fits; rents the first when there is none.</summary>
    private static void Grow<T>(ref T[] array, int count)
    {
        if (array == null)
        {
            array = ArrayPool<T>.Shared.Rent(Math.Max(8, count + 1));
            return;
        }

        if (count < array.Length)
        {
            return;
        }

        var grown = ArrayPool<T>.Shared.Rent(array.Length * 2);
        Array.Copy(array, grown, count);
        ArrayPool<T>.Shared.Return(array, true);
        array = grown;
    }

    /// <summary>
    /// #1347: the rows of each cluster a death newer than this snapshot touched — or a destroy still pending — read whole, as the SoA scan reads a cluster:
    /// every occupied slot and every slot claimed and not occupied (VIS-09), each predicate evaluated on the slot's bytes, each slot decided at the snapshot.
    /// Sorted by the ordered field into a side stream that carries each row's entity key, read with the row; <paramref name="sidePks"/> holds the same keys,
    /// sorted, for the tree stream to drop its rows of those entities — rented, the caller returns it once the merge is done.
    /// </summary>
    /// <remarks>
    /// By entity, not by cluster: an entity a migration moves between the walk and the tree stream's read is in exactly one of the two, whichever cluster it
    /// lands in, and a row is never resolved through a slot a migration released and a spawn reused since.
    /// </remarks>
    private ArchetypeSortedStream ReadClustersWhole(FieldEvaluator[] evaluators, ArchetypeClusterState clusterState, ArchetypeEngineState engineState,
        ArchetypeMetadata meta, int compSlot, int fieldOffset, KeyType keyType, bool descending, ref EnabledGate gate, UpdatedClusterSet updated,
        out long[] sidePks, out int sidePkCount)
    {
        var txTsn = _tx.TSN;
        var cards = _tx.DBE.CommitCards;
        var layout = clusterState.Layout;
        var compOffset = layout.ComponentOffset(compSlot);
        var compSize = layout.ComponentSize(compSlot);
        var recordSize = meta._entityRecordSize;
        byte* record = stackalloc byte[recordSize];

        var activeIds = clusterState.ReadActiveClusterList(out var activeCount);
        var keys = ArrayPool<long>.Shared.Rent(64);
        var pks = ArrayPool<long>.Shared.Rent(64);
        var count = 0;

        // VIS-12: the entities of a cluster an update newer than the snapshot touched, matching or not — the tree holds them at newer keys, so the tree stream
        // drops every one of them, and this stream carries those that match at the snapshot, at the keys they had then.
        long[] dropped = null;
        var droppedCount = 0;
        byte* snapshotColumn = null;   // the transaction's (FillSnapshotColumn), taken at the first cluster read at the snapshot
        var mapAccessor = engineState.EntityMap.Segment.CreateScanAccessor();
        var clusterAccessor = clusterState.ClusterSegment.CreateScanAccessor();
        try
        {
            for (var c = 0; c < activeCount; c++)
            {
                var clusterChunkId = activeIds[c];
                ref var words = ref ArchetypeClusterState.SlotWords(clusterState.SlotWordTable, clusterChunkId);
                var pendingDeaths = ArchetypeClusterState.PendingDeathsOf(ref words);
                var snapshotCluster = updated.Contains(clusterChunkId);
                if (!snapshotCluster && pendingDeaths == 0 && ArchetypeClusterState.MaxDiedOf(ref words) <= txTsn)
                {
                    continue;
                }

                var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                var occupancy = Volatile.Read(ref *(ulong*)clusterBase);
                if (Unsafe.IsNullRef(ref words))
                {
                    words = ref ArchetypeClusterState.SlotWords(clusterState.SlotWordTable, clusterChunkId);
                }

                var pend = ArchetypeClusterState.ClaimOf(ref words) & ~occupancy;
                var admitted = occupancy & gate.ClusterMask(clusterBase, out var gateExact);
                var visit = admitted | pend;
                var compBase = clusterBase + compOffset;
                if (snapshotCluster)
                {
                    for (var all = occupancy | pend; all != 0; all &= all - 1)
                    {
                        Grow(ref dropped, droppedCount);
                        dropped[droppedCount++] = *(long*)(clusterBase + layout.EntityIdsOffset + System.Numerics.BitOperations.TrailingZeroCount(all) * 8);
                    }

                    visit &= FillSnapshotColumn(meta, engineState, clusterState, clusterBase, compSlot, compSize, visit, out snapshotColumn, record,
                        ref mapAccessor);
                    compBase = snapshotColumn;
                }

                while (visit != 0)
                {
                    var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(visit);
                    visit &= visit - 1;
                    var entityComp = compBase + slotIndex * compSize;
                    if (!MatchesAll(evaluators, entityComp))
                    {
                        continue;
                    }

                    var rawId = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    var visible = (pend & (1UL << slotIndex)) != 0
                        ? IsReservedSpawnVisible(rawId, clusterChunkId, slotIndex, engineState, record, txTsn, cards, ref mapAccessor)
                          && gate.AdmitsRecord(rawId, record)
                        : IsVisibleAtSnapshot(rawId, true, engineState, record, recordSize, txTsn, cards, ref mapAccessor)
                          && (!gate.NeedsPerSlot(gateExact) || gate.Admits(rawId, clusterBase, slotIndex, gateExact, record, ref mapAccessor));
                    if (!visible)
                    {
                        continue;
                    }

                    Grow(ref keys, count);
                    Grow(ref pks, count);
                    keys[count] = OrderedKeyEncoding.EncodeAt(entityComp + fieldOffset, keyType);
                    pks[count++] = rawId;
                }
            }
        }
        catch
        {
            ArrayPool<long>.Shared.Return(keys);
            ArrayPool<long>.Shared.Return(pks);
            if (dropped != null)
            {
                ArrayPool<long>.Shared.Return(dropped);
            }

            throw;
        }
        finally
        {
            clusterAccessor.Dispose();
            mapAccessor.Dispose();
        }

        Array.Sort(keys, pks, 0, count);
        if (descending)
        {
            Array.Reverse(keys, 0, count);
            Array.Reverse(pks, 0, count);
        }

        // A fence migration publishes the destination slot before it releases the source, so a walk overlapping it can find one entity in two
        // clusters read whole: it is returned once. Checked on this stream's rows alone, before the dropped entities join them.
        sidePks = ArrayPool<long>.Shared.Rent(Math.Max(count + droppedCount, 1));
        Array.Copy(pks, sidePks, count);
        Array.Sort(sidePks, 0, count);
        if (HasAdjacentDuplicate(sidePks, count))
        {
            count = DropDuplicateRows(keys, pks, count);
            Array.Copy(pks, sidePks, count);
            Array.Sort(sidePks, 0, count);
        }

        // The tree stream drops this stream's rows, and every entity of a cluster read at the snapshot.
        var total = count;
        if (dropped != null)
        {
            Array.Copy(dropped, 0, sidePks, count, droppedCount);
            ArrayPool<long>.Shared.Return(dropped);
            total += droppedCount;
            Array.Sort(sidePks, 0, total);
        }

        sidePkCount = DistinctSorted(sidePks, total);
        return ArchetypeSortedStream.FromSorted(keys, pks, count);
    }

    private static bool HasAdjacentDuplicate(long[] sorted, int count)
    {
        for (var i = 1; i < count; i++)
        {
            if (sorted[i] == sorted[i - 1])
            {
                return true;
            }
        }

        return false;
    }

    private static int DistinctSorted(long[] sorted, int count)
    {
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            if (kept == 0 || sorted[i] != sorted[kept - 1])
            {
                sorted[kept++] = sorted[i];
            }
        }

        return kept;
    }

    /// <summary>Compacts rows sorted by key, keeping each entity's first: rare (a migration overlapping the walk), so a quadratic look-back is fine.</summary>
    private static int DropDuplicateRows(long[] keys, long[] pks, int count)
    {
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var seen = false;
            for (var j = 0; j < kept && !seen; j++)
            {
                seen = pks[j] == pks[i];
            }

            if (!seen)
            {
                keys[kept] = keys[i];
                pks[kept++] = pks[i];
            }
        }

        return kept;
    }

    /// <summary>Collect results from a K-way merge, applying Skip/Take.</summary>
    private List<EntityId> CollectMergedResults(ref KWayMergeState merge, FieldEvaluator[] evaluators)
    {
        var result = new List<EntityId>(_take > 0 ? _take : 64);
        var skipped = 0;
        var taken = 0;
        var take = _take > 0 ? _take : int.MaxValue;

        // Tell the merge up front whether this row is going to be kept: a skipped row needs its ORDER, which the merge
        // already has, but never its entity key — unless this transaction destroyed some entity, which it no longer sees: not counted, not returned.
        var destroys = _tx.PendingDestroys is { Count: > 0 } pendingDestroys ? pendingDestroys : null;
        while (merge.MoveNext(out var entityPK, destroys != null || skipped >= _skip))
        {
            if (destroys != null && destroys.Contains(EntityId.FromRaw(entityPK)))
            {
                continue;
            }

            if (skipped < _skip)
            {
                skipped++;
                continue;
            }
            result.Add(EntityId.FromRaw(entityPK));
            taken++;
            if (taken >= take)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Apply Skip/Take to a pre-sorted list of entity PKs.</summary>
    private List<EntityId> ApplySkipTake(List<long> pks)
    {
        var result = new List<EntityId>(_take > 0 ? _take : Math.Min(pks.Count, 256));
        var skipped = 0;
        var taken = 0;
        var take = _take > 0 ? _take : int.MaxValue;

        for (var i = 0; i < pks.Count; i++)
        {
            if (skipped < _skip)
            {
                skipped++;
                continue;
            }
            result.Add(EntityId.FromRaw(pks[i]));
            taken++;
            if (taken >= take)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Fallback for ordered cluster queries with secondary evaluators (predicates on fields other than the OrderBy field).
    /// Uses ExecuteTargeted (which verifies ALL evaluators per-entity) then sorts by the OrderBy field.
    /// O(n log n) sort instead of O(n log K) merge — acceptable for the rare multi-indexed-field case.
    /// </summary>
    private List<EntityId> ExecuteOrderedViaSortFallback(FieldEvaluator[] evaluators, ExecutionPlan plan)
    {
        // ExecuteTargeted verifies all evaluators, handles both cluster and non-cluster archetypes
        var unordered = ExecuteTargeted();

        // Where each archetype keeps the ordered field, to read each row's key from.
        Debug.Assert(_orderBy != null, nameof(_orderBy) + " != null");
        var orderByFieldIdx = _orderBy.Value.FieldIndex;
        var dbe = _tx.DBE;
        List<(ArchetypeMetadata Meta, ArchetypeEngineState State, ArchetypeClusterState Cluster, int IxSlot, int FieldOffset, KeyType KeyType)>
            hiddenKeySources = null;

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!MaskTest(meta.ArchetypeId) || !meta.HasClusterIndexes)
            {
                continue;
            }

            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState?.IndexSlots == null)
            {
                continue;
            }

            // Persistent home only — see the note in ExecuteOrderedClustered. A Transient home leaves this archetype's entities without a source, so they
            // sort by EntityKey rather than by the ordered field (#655 follow-up, tracked in #665).
            var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
            if (ixSlotIdx < 0 || transientHome || orderByFieldIdx < 0 || orderByFieldIdx >= clusterState.IndexSlots[ixSlotIdx].Fields.Length)
            {
                continue;
            }

            ref var field = ref clusterState.IndexSlots[ixSlotIdx].Fields[orderByFieldIdx];

            // The ordered field's own key type (QORD-01). It came from plan.PrimaryKeyType and evaluators[0].KeyType — two other fields — until #675, then
            // from a predicate naming the field, so a query ordered by a field none of its predicates named sorted by EntityKey (#591's remaining half), until
            // the market storm's query auditor found it.
            var orderKeyType = _orderBy.Value.KeyType;
            if (!_orderBy.Value.KeyTypeKnown || !KeyRange.IsStreamable(orderKeyType))
            {
                continue;
            }

            (hiddenKeySources ??= []).Add((meta, engineState, clusterState, ixSlotIdx, field.FieldOffset, orderKeyType));
        }

        // Each row's key as the reader sees it: its point read at the snapshot, the transaction's own pending spawns included; past that, the bytes its hidden
        // slot kept (VIS-09). The tree used to be read whole — every entry, whatever the result's size — and holds the HEAD's keys, so a reader older than an
        // update sorted that row by its newer value (#674).
        var withKeys = new List<(long orderedKey, EntityId id)>(unordered.Count);
        foreach (var id in unordered)
        {
            if (!TryReadOrderKeyAtSnapshot(id, hiddenKeySources, out var orderedKey) && !TryReadHiddenOrderKey(id, hiddenKeySources, out orderedKey))
            {
                orderedKey = id.EntityKey;
            }

            withKeys.Add((orderedKey, id));
        }

        if (plan.Descending)
        {
            withKeys.Sort((a, b) => b.orderedKey.CompareTo(a.orderedKey));
        }
        else
        {
            withKeys.Sort((a, b) => a.orderedKey.CompareTo(b.orderedKey));
        }

        // Apply Skip/Take
        var result = new List<EntityId>();
        var skipped = 0;
        var taken = 0;
        var take = _take > 0 ? _take : int.MaxValue;
        for (var i = 0; i < withKeys.Count; i++)
        {
            if (skipped < _skip)
            {
                skipped++;
                continue;
            }
            result.Add(withKeys[i].id);
            taken++;
            if (taken >= take)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>The ordered field's key of <paramref name="id"/> as the reader's point read sees it; false when its archetype is not a source.</summary>
    private bool TryReadOrderKeyAtSnapshot(EntityId id,
        List<(ArchetypeMetadata Meta, ArchetypeEngineState State, ArchetypeClusterState Cluster, int IxSlot, int FieldOffset, KeyType KeyType)> sources,
        out long orderedKey)
    {
        orderedKey = 0;
        if (sources == null)
        {
            return false;
        }

        var meta = _tx.DBE.GetMetaByRouting(id.ArchetypeId);
        foreach (var s in sources)
        {
            if (ReferenceEquals(s.Meta, meta))
            {
                return TryReadOwnSpawnOrderKey(id, s.Cluster.IndexSlots[s.IxSlot].Slot, s.FieldOffset, s.KeyType, out orderedKey);
            }
        }

        return false;
    }

    /// <summary>
    /// The clusters of this archetype an update newer than this snapshot touched (VIS-12), empty for none: one load when the archetype's newest update is at
    /// or below the snapshot — every reader newer than the last update — otherwise a walk of every active cluster's update watermark. The caller returns
    /// the set. <paramref name="mark"/>, read first, is the archetype's update count for <see cref="UpdatedSince"/>: the ordered merge reads its trees
    /// lazily, after this.
    /// </summary>
    private UpdatedClusterSet ClustersUpdatedAfterSnapshot(ArchetypeClusterState clusterState, out long mark)
    {
        // The tree reads before these, on arm64 too (an acquire does not hold earlier plain loads back); JIT-folded on x64. The count before the stamp,
        // the stamp before the watermarks: an update folds them in the other order, so one this read misses is the second read's (UpdatedSince).
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }

        mark = clusterState.UpdateCount;
        var txTsn = _tx.TSN;
        return clusterState.MaxUpdateStamp <= txTsn ? default : WatermarksAbove(clusterState, txTsn, default);
    }

    /// <summary>The selective scan's form: read once, after its tree read, with no second read to mark for.</summary>
    private UpdatedClusterSet ClustersUpdatedAfterSnapshot(ArchetypeClusterState clusterState)
    {
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }

        var txTsn = _tx.TSN;
        return clusterState.MaxUpdateStamp <= txTsn ? default : WatermarksAbove(clusterState, txTsn, default);
    }

    /// <summary>
    /// The active clusters whose update watermark is above <paramref name="txTsn"/>, less <paramref name="except"/>, in a rented array sorted for
    /// <see cref="UpdatedClusterSet.Contains"/>: a HashSet per walk allocated on every query of a reader older than an update.
    /// </summary>
    private static UpdatedClusterSet WatermarksAbove(ArchetypeClusterState clusterState, long txTsn, UpdatedClusterSet except)
    {
        QueryPathProbe.UpdateWatermarkWalks++;
        int[] ids = null;
        var count = 0;
        var table = clusterState.SlotWordTable;
        var activeIds = clusterState.ReadActiveClusterList(out var activeCount);
        for (var c = 0; c < activeCount; c++)
        {
            var clusterChunkId = activeIds[c];
            if (ArchetypeClusterState.MaxUpdatedOf(ref ArchetypeClusterState.SlotWords(table, clusterChunkId)) <= txTsn || except.Contains(clusterChunkId))
            {
                continue;
            }

            if (ids == null)
            {
                ids = ArrayPool<int>.Shared.Rent(16);
            }
            else if (count == ids.Length)
            {
                var grown = ArrayPool<int>.Shared.Rent(count * 2);
                Array.Copy(ids, grown, count);
                ArrayPool<int>.Shared.Return(ids);
                ids = grown;
            }

            ids[count++] = clusterChunkId;
        }

        if (count > 1)
        {
            Array.Sort(ids, 0, count);
        }

        return new UpdatedClusterSet(ids, count);
    }

    /// <summary>
    /// After the ordered merge: whether an update published since <paramref name="mark"/> touched a cluster the merge did not read whole at the snapshot —
    /// its index entries may have moved under the streams, so the merge answers again. A cluster in <paramref name="readWhole"/> does not count: its entities
    /// were dropped from the tree stream by key, so a later move of their entries changes nothing.
    /// </summary>
    private bool UpdatedSince(ArchetypeClusterState clusterState, long mark, UpdatedClusterSet readWhole)
    {
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }

        if (clusterState.UpdateCount == mark)
        {
            return false;
        }

        var since = WatermarksAbove(clusterState, _tx.TSN, readWhole);
        var raced = !since.IsEmpty;
        since.Return();
        return raced;
    }

    /// <summary>A sorted set of cluster chunk ids in a rented array, empty by default: the clusters an update newer than a snapshot touched (VIS-12).</summary>
    private readonly struct UpdatedClusterSet
    {
        private readonly int[] _ids;
        private readonly int _count;

        internal UpdatedClusterSet(int[] ids, int count)
        {
            _ids = ids;
            _count = count;
        }

        internal bool IsEmpty => _count == 0;

        internal bool Contains(int clusterChunkId) => _count != 0 && Array.BinarySearch(_ids, 0, _count, clusterChunkId) >= 0;

        /// <summary>Gives the array back; the set is not used after.</summary>
        internal void Return()
        {
            if (_ids != null)
            {
                ArrayPool<int>.Shared.Return(_ids);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="column"/> — a cluster's component column, laid out as the HEAD's — with the value of <paramref name="compSlot"/>'s Versioned
    /// component each of <paramref name="slots"/> held at this snapshot (VIS-12): an update newer than it rewrote the cluster's HEAD. Returns the slots whose
    /// entity had a value at the snapshot. Its reads are point reads through the transaction's accessors, which a back-pressure wait must not refresh the
    /// epoch under (#1230): the walk's refreshable depth is set aside meanwhile.
    /// </summary>
    private ulong FillSnapshotColumn(ArchetypeMetadata meta, ArchetypeEngineState engineState, ArchetypeClusterState clusterState, byte* clusterBase,
        int compSlot, int compSize, ulong slots, out byte* column, byte* record, ref ChunkAccessor<PersistentStore> mapAccessor)
    {
        QueryPathProbe.SnapshotReadClusters++;
        var layout = clusterState.Layout;
        // Sized to the SIMD evaluators' reach, not the cluster's: they gather 8 or 16 lanes at a time up to the cluster size rounded up, unmasked, and
        // mask the extra lanes after — a read past the last slot that must stay inside the column.
        column = _tx.SnapshotColumn(compSize * ((layout.ClusterSize + 15) & ~15));
        var versionedIndex = layout.SlotToVersionedIndex[compSlot];
        var present = 0UL;
        var refreshable = PagedMMF.RefreshableWaitDepth;
        PagedMMF.RefreshableWaitDepth = 0;
        try
        {
            while (slots != 0)
            {
                var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(slots);
                slots &= slots - 1;
                var rawId = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                if (!engineState.EntityMap.TryGet(EntityId.FromRaw(rawId).EntityKey, record, ref mapAccessor))
                {
                    continue;
                }

                var chainRoot = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, versionedIndex);
                if (_tx.TryCopyCommittedAtSnapshot(meta, compSlot, chainRoot, column + slotIndex * compSize, compSize))
                {
                    present |= 1UL << slotIndex;
                }
            }
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth = refreshable;
        }

        return present;
    }

    /// <summary>
    /// The ordered field's key of an entity absent from the tree: one a destroy newer than this snapshot took out of it (#1347), read from the bytes its hidden
    /// slot kept (VIS-09) through its record, or one of this transaction's own pending spawns, read as its point read sees it. False when its archetype is
    /// not a source.
    /// </summary>
    private bool TryReadHiddenOrderKey(EntityId id,
        List<(ArchetypeMetadata Meta, ArchetypeEngineState State, ArchetypeClusterState Cluster, int IxSlot, int FieldOffset, KeyType KeyType)> sources,
        out long orderedKey)
    {
        orderedKey = 0;
        if (sources == null)
        {
            return false;
        }

        var meta = _tx.DBE.GetMetaByRouting(id.ArchetypeId);
        byte* record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        foreach (var s in sources)
        {
            if (!ReferenceEquals(s.Meta, meta))
            {
                continue;
            }

            var mapAccessor = s.State.EntityMap.Segment.CreateScanAccessor();
            var clusterAccessor = s.Cluster.ClusterSegment.CreateScanAccessor();
            try
            {
                var compSlot = s.Cluster.IndexSlots[s.IxSlot].Slot;
                if (!s.State.EntityMap.TryGet(id.EntityKey, record, ref mapAccessor))
                {
                    // No record: one of this transaction's own pending spawns, whose value its point read holds.
                    return TryReadOwnSpawnOrderKey(id, compSlot, s.FieldOffset, s.KeyType, out orderedKey);
                }

                var layout = s.Cluster.Layout;
                var clusterBase = clusterAccessor.GetChunkAddress(ClusterEntityRecordAccessor.GetClusterChunkId(record));
                var entityComp = clusterBase + layout.ComponentOffset(compSlot)
                                 + ClusterEntityRecordAccessor.GetSlotIndex(record) * layout.ComponentSize(compSlot);
                orderedKey = OrderedKeyEncoding.EncodeAt(entityComp + s.FieldOffset, s.KeyType);
                return true;
            }
            finally
            {
                clusterAccessor.Dispose();
                mapAccessor.Dispose();
            }
        }

        return false;
    }

    /// <summary>Execute targeted scan via PipelineExecutor with archetype mask post-filter.</summary>
    private HashSet<EntityId> ExecuteTargeted(string callerFile = null, int callerLine = 0, string callerMethod = null)
    {
        var ct = _whereComponentTable;

        var evaluators = QueryResolverHelper.ResolveEvaluators(SingleBranchOrThrow("One-shot execution"), ct, 0);
        var plan = PlanBuilder.Instance.BuildPlanAttributed(evaluators, ct, PlannerStats(ct), AdvancedSelectivityEstimator.Instance, null,
            1, (uint)EcsQueryId, SourceFile, SourceLine, SourceMethod, callerFile, callerLine, callerMethod);

        // Scan for matching entities across all matching archetypes.
        var result = new HashSet<EntityId>(_take > 0 ? _take : 64);
        var sink = new EntityIdSetSink(result);
        ScanAllArchetypes(plan, evaluators, ct, ref sink);

        // An entity this transaction destroyed is gone for it, as a point read and the broad scan say: the scans read the committed slots.
        if (_tx.PendingDestroys is { Count: > 0 } destroyedHere)
        {
            result.ExceptWith(destroyedHere);
        }

        // Read-your-own-writes: pending spawns have no secondary index entries, so the targeted scan above can't find them. Evaluate them via compiled
        // predicate fallback.
        CollectPendingSpawnsWithFieldFilter(result);

        // Opaque WHERE post-filter (from .Where<T>(Func), separate from WhereField)
        var filter = _whereFilter;
        if (filter != null)
        {
            var tx = _tx;
            var readDisabled = DisabledNames;
            result.RemoveWhere(id => !filter(id, tx, readDisabled));
        }

        return result;
    }

    /// <summary>
    /// Scans every archetype this query's mask admits and deposits the matches in <paramref name="sink"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be "the one place that knows secondary indexes live in two homes". There is one home now (#629):
    /// every index is per-archetype and its values are packed <c>ClusterLocation</c>s. What survives of that job is
    /// choosing, per archetype, between the selective B+Tree scan and the SoA scan — and raising if an archetype
    /// carries the where-component but exposes no index at all, because there is nothing left to fall back to.
    /// </para>
    /// <para>
    /// Shared deliberately. This loop used to exist only inside <see cref="ExecuteTargeted"/>, while the four view-population call sites passed the
    /// ComponentTable straight to <c>PipelineExecutor</c> — scanning a tree that is empty for a cluster-backed archetype, so <c>ToView()</c> came back
    /// permanently empty while <c>Execute()</c> on the same query was correct (#663). One copy cannot drift from the other.
    /// </para>
    /// <para>
    /// Filtering is per ARCHETYPE, before scanning: the loop tests the query mask against each archetype and skips it whole. The deleted shared home could
    /// not do that — one table served every archetype holding the component, so its results had to be filtered per ENTITY by routing id.
    /// </para>
    /// </remarks>
    private void ScanAllArchetypes<TSink>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable ct, ref TSink sink)
        where TSink : struct, IEntityIdSink
    {
        var hasNonClusterArchetypes = false;

        // Direct cluster scan for cluster-eligible archetypes with indexed fields
        {
            var dbe = _tx.DBE;
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                if (!MaskTest(meta.ArchetypeId))
                {
                    continue;
                }

                var engineState = dbe._archetypeStates[meta.ArchetypeId];

                // An archetype that does not carry the where-component AT ALL cannot match a predicate on one of its fields, so it contributes nothing and is
                // skipped silently. This is not the #663 shape below: that one is an archetype which HAS the component and should have contributed. The mask is
                // the queried archetype's whole subtree and WhereField never narrows it, so a component declared on only part of a subtree — one descendant
                // or several — put an archetype with no such component in front of the guard and turned a legitimate polymorphic query into a hard throw.
                if (!ArchetypeCarries(engineState, ct))
                {
                    continue;
                }

                if (!meta.HasClusterIndexes)
                {
                    hasNonClusterArchetypes = true;
                    continue;
                }

                var clusterState = engineState?.ClusterState;
                // A where-component indexed in NEITHER home routes to the cross-archetype scan, which evaluates predicates against component DATA and is
                // therefore correct whichever home owns the index. Without this, FindClusterIndexSlot returns -1 inside ScanPerArchetypeBTree and it returns
                // with NO results — a silently empty query, the same shape as #663.
                if (clusterState == null)
                {
                    hasNonClusterArchetypes = true;
                    continue;
                }

                var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
                if (ixSlotIdx < 0)
                {
                    hasNonClusterArchetypes = true;
                    continue;
                }

                // Query planner: choose Path A (B+Tree selective) vs Path B (zone map + eval) on the primary index's fan-out.
                // A Transient home always takes Path B. Path A range-scans the tree, and the collector is typed to BTreeBase<PersistentStore>; Path B never
                // touches a tree at all, so it is correct for either home. Selecting it here costs a full SoA scan instead of a selective one — a performance
                // gap, not a correctness one, and one that #665 revisits when it unfreezes cluster selectivity statistics (#655).
                if (ChooseSelectivePath(plan, clusterState, engineState, ixSlotIdx, transientHome))
                {
                    QueryPathProbe.SelectiveScans++;
                    ScanPerArchetypeBTreeSelective(plan, evaluators, clusterState, meta, ref sink);
                }
                else
                {
                    QueryPathProbe.FullScans++;
                    ScanPerArchetypeBTree(evaluators, clusterState, meta, ref sink);
                }
            }
        }

        // This used to fall through to a PipelineExecutor scan over the shared ComponentTable index. That home is gone (#629), and a scan of it would have
        // contributed nothing — so an archetype reaching here would silently drop out of the result rather than fail, which is precisely the #663 shape the
        // per-archetype migration existed to remove. Instrumenting the old fallthrough and running the full suite showed it was never entered, so this is a
        // guard against a future classification bug, not a live branch. Loud beats silently-incomplete either way.
        if (hasNonClusterArchetypes)
        {
            ThrowHelper.ThrowInvalidOp(
                $"Query on '{ct?.Definition?.Name}' matched an archetype that CARRIES the where-component but has no per-archetype index for it. There is no "
                + "longer a shared index home to fall back to, and answering from the cluster scan alone would silently omit that archetype's entities.");
        }
    }

    /// <summary>
    /// Whether <paramref name="engineState"/>'s archetype has <paramref name="ct"/> among its component slots at all — the same reference-identity test
    /// <see cref="FindClusterIndexSlot"/> uses, asked one level earlier so "this archetype cannot match" is separated from "this archetype should have matched
    /// and has no index home".
    /// </summary>
    private static bool ArchetypeCarries(ArchetypeEngineState engineState, ComponentTable ct)
    {
        var tables = engineState?.SlotToComponentTable;
        if (tables == null)
        {
            return false;
        }

        for (var slot = 0; slot < tables.Length; slot++)
        {
            if (tables[slot] == ct)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see cref="ScanAllArchetypes{TSink}"/> for callers whose result container is a view's raw-PK entity set. Replaces the bare
    /// <c>IFieldReader.ExecuteFullScan</c> calls at the view-population sites, which saw only the per-ComponentTable index home (#663).
    /// </summary>
    internal void ExecuteFullScanAcrossArchetypes(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable ct, HashMap<long> result)
    {
        var sink = new PkMapSink(result);
        ScanAllArchetypes(plan, evaluators, ct, ref sink);
    }

    /// <summary>
    /// Scan cluster entities for a per-archetype indexed archetype using direct cluster evaluation (Path B).
    /// Evaluates all field predicates on cluster SoA data, resolving EntityKeys from the cluster.
    /// </summary>
    /// <remarks>
    /// Resolves the two segments the scan addresses and hands accessors over them to <see cref="ScanClusterSoa{TSink,TPrimary,TData}"/>. The two are separate
    /// axes (#655): <b>primary</b> holds the occupancy word and the entity-id tail — the cluster segment, or the Transient segment when the archetype is
    /// pure-Transient and has no cluster segment at all (exactly as <c>ArchetypeClusterState.RebuildActiveList</c> already treats it); <b>data</b> holds the
    /// matched slot's component bytes, in whichever segment matches that slot's storage mode. For a SingleVersion / Versioned slot the two collapse onto the
    /// cluster segment, which is why this path needed only one accessor before. Same three-store split as
    /// <see cref="DatabaseEngine.ProcessClusterShadowEntries"/>, and deliberately the same shape.
    /// </remarks>
    private void ScanPerArchetypeBTree<TSink>(FieldEvaluator[] evaluators, ArchetypeClusterState clusterState, ArchetypeMetadata meta, ref TSink result)
        where TSink : struct, IEntityIdSink
    {
        var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
        if (ixSlotIdx < 0)
        {
            return;
        }

        if (!transientHome)
        {
            var svAccessor = clusterState.ClusterSegment.CreateScanAccessor();
            try
            {
                ScanClusterSoa(evaluators, clusterState, meta, clusterState.IndexSlots, ixSlotIdx, ref svAccessor, ref svAccessor, ref result);
            }
            finally
            {
                svAccessor.Dispose();
            }

            return;
        }

        var transientAccessor = clusterState.TransientSegment.CreateScanAccessor();
        try
        {
            if (clusterState.ClusterSegment == null)
            {
                ScanClusterSoa(evaluators, clusterState, meta, clusterState.TransientIndexSlots, ixSlotIdx, ref transientAccessor, ref transientAccessor,
                    ref result);
            }
            else
            {
                var clusterAccessor = clusterState.ClusterSegment.CreateScanAccessor();
                try
                {
                    ScanClusterSoa(evaluators, clusterState, meta, clusterState.TransientIndexSlots, ixSlotIdx, ref clusterAccessor, ref transientAccessor,
                        ref result);
                }
                finally
                {
                    clusterAccessor.Dispose();
                }
            }
        }
        finally
        {
            transientAccessor.Dispose();
        }
    }

    /// <summary>
    /// The store-generic body of Path B: zone-map-prune each active cluster, then evaluate every predicate against the matched slot's SoA column and emit the
    /// entity ids that pass.
    /// </summary>
    /// <remarks>
    /// Never reads a B+Tree — only the zone maps hanging off the index fields — which is what lets one body serve both index homes. Path A
    /// (<see cref="ScanPerArchetypeBTreeSelective{TSink}"/>) does range-scan the tree and stays persistent-home-only for now; the planner in
    /// <see cref="ScanAllArchetypes{TSink}"/> routes every Transient home here.
    /// </remarks>
    private void ScanClusterSoa<TSink, TPrimary, TData>(FieldEvaluator[] evaluators, ArchetypeClusterState clusterState, ArchetypeMetadata meta,
        ClusterIndexSlot<TData>[] ixSlots, int ixSlotIdx, ref ChunkAccessor<TPrimary> primaryAccessor, ref ChunkAccessor<TData> dataAccessor, ref TSink result)
        where TSink : struct, IEntityIdSink
        where TPrimary : struct, IPageStore
        where TData : struct, IPageStore
    {
        // MVCC born/died gate (04-data.md "Isolation guarantees"). The SoA scan walks CURRENT occupancy and reads the committed HEAD column, neither of which
        // knows about the reader's snapshot — so without it an entity committed AFTER the snapshot is emitted, exactly the phantom read the fixed snapshot is
        // specified to prevent. Only Versioned archetypes promise it (the storage-mode matrix is explicit: snapshot isolation is "no" for both SingleVersion
        // and Transient), so a pure-SV / pure-Transient archetype keeps the lookup-free scan.
        var visGated = meta.VersionedSlotMask != 0;
        var txTsn = _tx.TSN;
        var visState = visGated ? _tx.DBE._archetypeStates[meta.ArchetypeId] : null;
        var visRecordSize = meta._entityRecordSize;
        byte* visBuf = stackalloc byte[visRecordSize];
        var visAccessor = visState != null ? visState.EntityMap.Segment.CreateScanAccessor() : default;

        // Every page the scan holds is a scan accessor's pin: its back-pressure waits may move the epoch (#1230).
        PagedMMF.RefreshableWaitDepth++;
        try
        {
            ref var matchSlot = ref ixSlots[ixSlotIdx];
            var layout = clusterState.Layout;
            var compSlot = matchSlot.Slot;
            var compSize = layout.ComponentSize(compSlot);
            var compOffset = layout.ComponentOffset(compSlot);

            // QENABLE-01: the predicate's component enabled (or disabled and present, when the query names Disabled<T>), and every component the
            // query names enabled or disabled so. The scan evaluated the slot's bytes whatever the component's state, and ignored Enabled<T>/Disabled<T>.
            if (!TryCreateEnabledGate(meta, layout, compSlot, out var gate))
            {
                return;
            }

            // Pre-compute zone map query bounds for each evaluator (zone map pruning).
            // Bounds stored on stack; zone map references accessed via field iteration (no ref-type array allocation).
            var evalCount = evaluators.Length;
            var zoneMapMins = evalCount <= 8 ? stackalloc long[evalCount] : new long[evalCount];
            var zoneMapMaxs = evalCount <= 8 ? stackalloc long[evalCount] : new long[evalCount];
            // Track which evaluators have zone map bounds (bit per evaluator, fits in ulong for ≤64 evaluators)
            ulong zoneMapEvalMask = 0;
            var hasZoneMaps = false;

            for (var e = 0; e < evalCount && e < 64; e++)
            {
                ref var eval = ref evaluators[e];
                for (var fi = 0; fi < matchSlot.Fields.Length; fi++)
                {
                    ref var field = ref matchSlot.Fields[fi];
                    if (field.FieldOffset == eval.FieldOffset && field.FieldSize == eval.FieldSize && field.ZoneMap != null)
                    {
                        if (ZoneMapArray.TryGetQueryBounds(ref eval, out var qMin, out var qMax))
                        {
                            zoneMapMins[e] = qMin;
                            zoneMapMaxs[e] = qMax;
                            zoneMapEvalMask |= 1UL << e;
                            hasZoneMaps = true;
                        }

                        break;
                    }
                }
            }

            // Pre-determine SIMD eligibility for each evaluator (once, before cluster loop)
            var anySimd = false;
            var simdEligible = evalCount <= 8 ? stackalloc bool[8] : new bool[evalCount];
            if (Avx2.IsSupported)
            {
                for (var e = 0; e < evalCount; e++)
                {
                    simdEligible[e] = SimdPredicateEvaluator.IsSimdEligible(evaluators[e].KeyType);
                    anySimd |= simdEligible[e];
                }
            }

            var clusterSize = layout.ClusterSize;

            // VIS-12 (#674): a cluster an update newer than this snapshot rewrote is evaluated on its entities' values AT the snapshot, copied into a column
            // laid out as the HEAD's, so the evaluation below runs unchanged. The HEAD held the newer values: a reader older than an update matched by them.
            var snapshotReads = visGated && (meta.VersionedSlotMask & (1 << compSlot)) != 0;
            byte* snapshotColumn = null;   // the transaction's (FillSnapshotColumn), taken at the first cluster read at the snapshot

            // CLUSTERWALK-02: count first, then the array, through the one reader, hoisted out of the loop. The plain per-iteration loads this replaced
            // are ordered on x64 but not on arm64, where they can pair a grown count with the array it grew from and index past its end. Freezing the
            // list for the scan changes no result: AddToActiveList only APPENDS, and a cluster born mid-scan holds nothing visible at this snapshot.
            var activeIds = clusterState.ReadActiveClusterList(out var activeCount);
            var scanYield = _tx.BeginScanYield();
            for (var c = 0; c < activeCount; c++)
            {
                // #1364: between clusters the scan holds only its accessors' slot-reference pins, so the transaction's epoch may move.
                if ((c & 63) == 63)
                {
                    scanYield();
                }

                var clusterChunkId = activeIds[c];

                // Zone map pruning: skip cluster if any predicate's range doesn't overlap the cluster's [min, max].
                // Iterates fields to find zone maps, then checks matching evaluators — avoids ref-type array allocation.
                if (hasZoneMaps)
                {
                    var skip = false;
                    for (var fi = 0; fi < matchSlot.Fields.Length && !skip; fi++)
                    {
                        ref var field = ref matchSlot.Fields[fi];
                        if (field.ZoneMap == null)
                        {
                            continue;
                        }

                        for (var e = 0; e < evalCount && !skip; e++)
                        {
                            if ((zoneMapEvalMask & (1UL << e)) == 0)
                            {
                                continue;
                            }
                            if (evaluators[e].FieldOffset != field.FieldOffset)
                            {
                                continue;
                            }
                            if (!field.ZoneMap.MayContain(clusterChunkId, zoneMapMins[e], zoneMapMaxs[e]))
                            {
                                skip = true;
                            }
                        }
                    }

                    // VIS-12: the fence re-tightens zone maps from the HEAD, so a cluster an update newer than this snapshot rewrote may hold the snapshot's
                    // value outside them. Read after the zone map, the watermark shows any such update: the HEAD changed before the map was re-tightened.
                    if (skip && snapshotReads)
                    {
                        Volatile.ReadBarrier();   // the zone-map loads before the watermark re-read, on arm64 too (load-load); nothing on x64

                        skip = clusterState.ClusterMaxUpdated(clusterChunkId) <= txTsn;
                    }

                    if (skip)
                    {
                        continue;
                    }
                }

                var clusterBase = primaryAccessor.GetChunkAddress(clusterChunkId);

                // The pending destroys BEFORE the occupancy word (VIS-09): a destroy's publish hides its slot, then lifts the count, so a zero read here means
                // the occupancy read below reflects the hide — read after, a stale bit passed the gate. The claim word, read after the occupancy word, sits
                // beside it: one lookup for both.
                ref var slotWords = ref visGated ? ref ArchetypeClusterState.SlotWords(clusterState.SlotWordTable, clusterChunkId) : ref Unsafe.NullRef<long>();
                var pendingDeaths = ArchetypeClusterState.PendingDeathsOf(ref slotWords);

                // Acquire, not a plain load: AreClusterWatermarksVisibleAt below opens with an acquire of its own, and an acquire does not stop an EARLIER plain
                // load from sinking past it. Plain here would let arm64 pair a fresh occupancy word with a stale watermark — the phantom BIND-04 forbids.
                var occupancy = Volatile.Read(ref *(ulong*)clusterBase);

                // No block at the first lookup — a cluster listed before its words exist (SlotWordTable): resolve them again, after the occupancy word, or a
                // destroy that created the block and hid its slot meanwhile would read here as neither occupied nor claimed, and an older snapshot lose it.
                if (visGated && Unsafe.IsNullRef(ref slotWords))
                {
                    slotWords = ref ArchetypeClusterState.SlotWords(clusterState.SlotWordTable, clusterChunkId);
                }

                // VIS-08/09: a slot claimed and not occupied — a spawn placed in PREPARE, or a destroyed entity hidden until the cleanup. A snapshot scan visits
                // it, the claims read after the occupancy word (a spawn's claim outlives its publish), and decides it through its record and its owner's card.
                // HEAD readers never see one.
                var reserved = ArchetypeClusterState.ClaimOf(ref slotWords) & ~occupancy;
                if ((occupancy | reserved) == 0)
                {
                    continue;
                }

                // The occupied slots the enabled gate admits; a reserved slot is decided by its record, below.
                var admitted = occupancy & gate.ClusterMask(clusterBase, out var gateExact);

                // H1 — cluster-granularity visibility. The per-match EntityMap probe below costs 166-241 ns/entity against ~27 ns for the whole rest of the
                // scan, because the map's full-avalanche hash scatters consecutive keys into unrelated buckets. One sequential read of the cluster's summary
                // answers "was every entity here committed before this snapshot, and has none died?" — and in steady state that is true of almost every
                // cluster, so the probe count collapses to ~0. A cluster that fails the summary keeps the exact per-entity check, so correctness is unchanged.
                // Read AFTER the occupancy word on purpose: the summary is published by the same commit that sets the occupancy bit, so a reader that cannot
                // see the bit cannot be misled by a summary that predates it.
                var clusterVisGated = visGated && (pendingDeaths != 0 || !ArchetypeClusterState.WatermarksVisibleAt(ref slotWords, txTsn));

                // The component column can live in a DIFFERENT segment from the occupancy word — a Transient slot on a mixed archetype. Chunk ids are held in
                // lockstep between the two segments, so the same clusterChunkId addresses both.
                var compBase = dataAccessor.GetChunkAddress(clusterChunkId) + compOffset;

                var snapshotCluster = snapshotReads && ArchetypeClusterState.MaxUpdatedOf(ref slotWords) > txTsn;
                if (snapshotCluster)
                {
                    var present = FillSnapshotColumn(meta, visState, clusterState, clusterBase, compSlot, compSize, admitted | reserved, out snapshotColumn,
                        visBuf, ref visAccessor);
                    admitted &= present;
                    reserved &= present;
                    compBase = snapshotColumn;
                }
                else if (snapshotReads && QueryPathProbe.BeforeHeadEvaluateArmed)
                {
                    QueryPathProbe.BeforeHeadEvaluateHook?.Invoke();
                }

                var matching = MatchingSlots(evaluators, simdEligible, anySimd, compBase, compSize, clusterSize, admitted | reserved, 0, int.MinValue);

                // VIS-12: the HEAD read validated after it, as a seqlock reader validates — an update folds its watermark before it copies its value, so one
                // that copied a value newer than this snapshot into the bytes just read shows here, and the cluster is evaluated again at the snapshot.
                if (snapshotReads && !snapshotCluster)
                {
                    Volatile.ReadBarrier();   // the HEAD loads above are plain: an acquire alone would not keep them before the re-read; nothing on x64

                    if (ArchetypeClusterState.MaxUpdatedOf(ref slotWords) > txTsn)
                    {
                        var present = FillSnapshotColumn(meta, visState, clusterState, clusterBase, compSlot, compSize, admitted | reserved, out snapshotColumn,
                            visBuf, ref visAccessor);
                        admitted &= present;
                        reserved &= present;
                        matching = MatchingSlots(evaluators, simdEligible, anySimd, snapshotColumn, compSize, clusterSize, admitted | reserved, 0,
                            int.MinValue);
                    }
                }

                while (matching != 0)
                {
                    var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(matching);
                    matching &= matching - 1;
                    var rawId = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    var visible = (reserved & (1UL << slotIndex)) != 0
                        ? IsReservedSpawnVisible(rawId, clusterChunkId, slotIndex, visState, visBuf, txTsn, _tx.DBE.CommitCards, ref visAccessor)
                          && gate.AdmitsRecord(rawId, visBuf)
                        : IsVisibleAtSnapshot(rawId, clusterVisGated, visState, visBuf, visRecordSize, txTsn, _tx.DBE.CommitCards, ref visAccessor)
                          && (!gate.NeedsPerSlot(gateExact) || gate.Admits(rawId, clusterBase, slotIndex, gateExact, visBuf, ref visAccessor));
                    if (visible)
                    {
                        result.Add(EntityId.FromRaw(rawId));
                    }
                }
            }

        }
        finally
        {
            PagedMMF.RefreshableWaitDepth--;
            if (visState != null)
            {
                visAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether this archetype should be scanned via Path A (selective B+Tree range scan) rather than Path B (zone map + SoA evaluation).
    /// </summary>
    /// <remarks>
    /// The hard preconditions come first and are not overridable, because they are not preferences: a Transient home has no
    /// <c>BTreeBase&lt;PersistentStore&gt;</c> for the collector to walk, without a primary field there is no range to scan, and a sparse tree holds no
    /// key 0 (IX-09), so a range admitting 0 scanned over it misses every entity holding 0. Only the selectivity judgement — the part that is an estimate —
    /// answers to <see cref="QueryPathProbe.Forced"/>.
    /// </remarks>
    private static bool ChooseSelectivePath(ExecutionPlan plan, ArchetypeClusterState clusterState, ArchetypeEngineState engineState, int ixSlotIdx,
        bool transientHome)
    {
        if (transientHome || !plan.UsesSecondaryIndex || ScanMissesSparseZero(plan, clusterState, ixSlotIdx))
        {
            return false;
        }

        return QueryPathProbe.Forced switch
        {
            ClusterScanPath.Selective => true,
            ClusterScanPath.FullScan => false,
            _ => HasFanOutForSelectiveScan(plan, clusterState, engineState, ixSlotIdx)
        };
    }

    /// <summary>
    /// Whether the plan's primary range admits key 0 and the archetype's tree for that field is sparse: a scan of it would miss the 0s (IX-09).
    /// </summary>
    private static bool ScanMissesSparseZero(ExecutionPlan plan, ArchetypeClusterState clusterState, int ixSlotIdx)
    {
        var ixSlots = clusterState.IndexSlots;
        if (ixSlots == null || (uint)ixSlotIdx >= (uint)ixSlots.Length)
        {
            return false;
        }

        var fields = ixSlots[ixSlotIdx].Fields;
        return fields != null && (uint)plan.PrimaryFieldIndex < (uint)fields.Length && fields[plan.PrimaryFieldIndex].Index.Sparse
               && KeyRange.AdmitsZero(plan.PrimaryKeyType, plan.PrimaryScanMin, plan.PrimaryScanMax);
    }

    /// <summary>
    /// Whether the primary index's <b>fan-out</b> — rows per distinct key — is high enough for the selective scan to be worth taking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fan-out, not selectivity. The two paths pay for different things: Path A pays per KEY in range (a leaf step and a buffer walk each), Path B pays per
    /// CLUSTER it cannot prune (a 64-slot SIMD pass each). Writing <c>keysInRange = matches / fanOut</c> and giving Path B its BEST case — zone maps pruning
    /// to <c>matches / ClusterSize</c> clusters — cancels <c>matches</c> from both sides and leaves <c>fanOut > k * ClusterSize</c>. The match count drops
    /// out entirely, which is why the selectivity estimate this used to consult was measuring the wrong property: at a fixed fan-out the ratio between the
    /// two paths barely moves across three decades of selectivity.
    /// </para>
    /// <para>
    /// Measured over 10 000 rows, both paths forced, as Path B's time divided by Path A's — above 1.00 means Path A won. <c>Strided</c> assigns
    /// <c>key = i % keys</c> so equal keys spread over every cluster and zone maps prune nothing; <c>Clustered</c> assigns <c>key = i / fanOut</c> so equal
    /// keys are adjacent and pruning is perfect. Each cell is the range matching 1 % / 10 % of the key space:
    /// </para>
    /// <code>
    ///   fan-out      1       8      40     200    1000
    ///   Strided    0.36    0.76    1.29    1.66    1.23     (1 % of keys)
    ///              0.13    0.63    1.18    1.22    1.38     (10 % of keys)
    ///   Clustered  0.36    0.75    1.06    0.93    1.04     (1 % of keys)
    ///              0.14    0.53    0.87    0.94    0.98     (10 % of keys)
    /// </code>
    /// <para>
    /// The honest reading of the <c>Clustered</c> rows: against perfectly-pruned data Path A is a WASH at high fan-out, never a win. Selecting it is a bet
    /// that key values and insert order are decorrelated — which pays 20-66 % when they are and costs about 6 % when they are not. The bet is only good
    /// above the crossover; at fan-out 8 and below Path B wins on BOTH layouts by 1.3x to 7x, which is the band the threshold exists to exclude.
    /// </para>
    /// </remarks>
    private static bool HasFanOutForSelectiveScan(ExecutionPlan plan, ArchetypeClusterState clusterState, ArchetypeEngineState engineState, int ixSlotIdx)
    {
        // Structural, not a preference, and first because it is the cheapest: unless the scan range exactly implements every predicate on the primary field,
        // Path A must re-evaluate that predicate over all 64 slots of every cluster it touched — which IS Path B's per-cluster work, leaving Path A as Path B
        // plus a tree scan. It cannot win at ANY fan-out, so no threshold rescues it. This is what the table above measures on its skip-eligible side.
        if (!plan.PrimaryRangeAdmitsOnlyMatches)
        {
            return false;
        }

        var ixSlots = clusterState.IndexSlots;
        if (ixSlots == null || (uint)ixSlotIdx >= (uint)ixSlots.Length)
        {
            return false;
        }

        var fields = ixSlots[ixSlotIdx].Fields;
        if (fields == null || (uint)plan.PrimaryFieldIndex >= (uint)fields.Length)
        {
            return false;
        }

        ref var primaryField = ref fields[plan.PrimaryFieldIndex];

        // A unique index stores one entry per row: fan-out 1, Path A's worst case per key, which the arithmetic below would reject — and only for
        // AllowMultiple is an entry a DISTINCT KEY rather than a row. What the fan-out rule cannot see is the archetype's SIZE. Path B pays per cluster it
        // cannot prune, and once the keys are scattered over the clusters that is every cluster however few rows match; the tables below were measured
        // over 157 clusters, where a full pass is cheap. A range on a unique index bounds its matches exactly — one row per key — so when that bound is far
        // below the cluster count Path A visits a handful of clusters where Path B reads all of them. MarketHardeningTests made 2 000 point lookups on a
        // 1 000 000-row archetype, and each read the archetype's whole cluster segment from disk.
        if (!primaryField.AllowMultiple)
        {
            clusterState.ReadActiveClusterList(out var activeClusters);
            return KeyRange.MaxKeysInRange(plan.PrimaryKeyType, plan.PrimaryScanMin, plan.PrimaryScanMax)
                   <= activeClusters / MinClustersPerUniqueKeyForSelectiveScan;
        }

        var distinctKeys = primaryField.Index?.EntryCount ?? 0;
        if (distinctKeys <= 0)
        {
            return false;
        }

        // The archetype's live row count, read from the EntityMap in O(1). NOT `ActiveClusterCount * ClusterSize`: that is an upper bound, and dividing an
        // upper bound by the key count inflates fan-out by the reciprocal of cluster occupancy — an archetype left 10 % full by destroys would read as ten
        // times the fan-out it has and take Path A into the band where Path B wins outright.
        var rows = engineState?.EntityMap?.EntryCount ?? 0;
        if (rows <= 0)
        {
            return false;
        }

        // rows / distinctKeys >= MinFanOutClusters * ClusterSize, kept as a multiply so no division and no float enters a per-archetype decision.
        return rows >= (long)distinctKeys * MinFanOutClustersForSelectiveScan * clusterState.Layout.ClusterSize;
    }

    /// <summary>
    /// Estimated selectivity below which the planner prefers Path A. <b>Zero — the planner does not select Path A</b>, because it is not faster at any
    /// selectivity on any distribution measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was <c>0.05f</c>, and nothing had measured it. Forcing each path over 10 000 entities, four value distributions × four selectivities
    /// (Path B time as a fraction of Path A's — below 1.00 means Path B won):
    /// </para>
    /// <code>
    ///                 0.1%    1%      5%      20%
    ///   Sequential    0.84    0.37    0.17    0.12
    ///   Random        0.85    0.45    0.22    0.13
    ///   Banded        0.85    0.50    0.25    0.14
    ///   LowCard       1.07    1.08    0.99    0.93
    /// </code>
    /// <para>
    /// The old threshold selected Path A across a band where Path B is 15–63 % faster. Only low-cardinality data favours Path A, by 7–8 %, and its two
    /// leftmost cells are one measurement rather than two — with 50 distinct keys, "top 10" and "top 100" resolve to the same cut-off value. Scattered data
    /// was expected to favour Path A, on the theory that zone maps cannot prune it; measured, it does not.
    /// </para>
    /// <para>
    /// The reason is structural rather than a matter of tuning. Path A range-scans the tree to narrow the candidate set, then <b>throws that narrowing away</b>:
    /// its verification loop evaluates EVERY evaluator — including the primary one the tree just answered exactly — with
    /// <c>SimdPredicateEvaluator.EvaluateCluster</c> over all 64 slots of each cluster it touched. That is precisely Path B's per-cluster work, so Path A is
    /// Path B plus a tree scan whenever the two visit the same clusters, and the tree scan is what the ratios above are measuring.
    /// </para>
    /// <para>
    /// Making Path A worth selecting means skipping the primary evaluator during verification, which needs the planner to report whether the scan range
    /// <i>exactly</i> implements the primary predicate — true for ordered comparisons, false for <c>!=</c>, which <c>KeyRange.Intersect</c> folds into a
    /// superset. Until that exists the path stays reachable through <see cref="QueryPathProbe.Forced"/>, so <c>QueryPathEquivalenceTests</c> keeps proving the
    /// two paths agree; what changes here is only that production stops paying for the slower one.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How many whole clusters one distinct key's rows must be able to fill before the planner will take the selective scan — the threshold of
    /// <see cref="HasFanOutForSelectiveScan"/>, expressed in clusters rather than rows so it tracks the archetype's actual geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two, and the band it excludes is the reason.</b> Measured at <c>ClusterSize</c> 64 (so a fan-out threshold of 128), Path B's time over Path A's:
    /// </para>
    /// <code>
    ///   fan-out    1        8         40                  80          125         200                 1000
    ///   Strided    .13-.36  .63-.76   1.01 1.18 1.29 1.34  0.85 1.26  1.20 1.84  1.18 1.22 1.66 1.67  1.23 1.38
    ///   Clustered  .14-.36  .53-.75   0.87 0.97 1.02 1.06  0.94 0.96  0.94 0.98  0.93 0.94 0.97 0.98  0.98 1.04
    /// </code>
    /// <para>
    /// Fan-out 40-80 is MARGINAL — two runs of the same cell disagreed in sign (0.85 against 1.26) — while every cell at 125 and above wins on decorrelated
    /// data and is a wash on correlated data. The threshold sits above the marginal band rather than inside it, which deliberately forgoes the measured
    /// 1.20-1.84 at fan-out 125 for being a hair under. Lowering it to one cluster is the obvious next experiment and wants evidence, not intuition: the
    /// constant it replaced was <c>0.05f</c>, chosen by nobody and measured by nobody, and it cost a 15-63 % regression across the band it selected.
    /// </para>
    /// <para>
    /// Scaling by <c>ClusterSize</c> rather than hard-coding 128 follows the cost model — Path B's per-cluster SIMD pass covers <c>ClusterSize</c> slots, so
    /// break-even fan-out is proportional to it — but that proportionality is predicted, not measured: every cell above was taken at the maximum
    /// <c>ClusterSize</c> of 64 (<see cref="ClusterLocation.MaxClusterSize"/>).
    /// </para>
    /// </remarks>
    private const int MinFanOutClustersForSelectiveScan = 2;

    /// <summary>
    /// For a range on a unique index: how many of the archetype's clusters each key the range can hold must stand against before the planner takes the
    /// selective scan (<see cref="HasFanOutForSelectiveScan"/>).
    /// </summary>
    /// <remarks>
    /// The fan-out-1 cells above set it. Over 157 clusters, a range matching 1 % of the rows (100 keys, strided so zone maps prune nothing) took Path A
    /// 2.8 times Path B's full pass: about 4.4 cluster passes per key. Eight per key doubles that, so where the rule takes Path A it is not the slower path
    /// on data the table measured, and it takes it only where Path B's cost — every cluster — dwarfs Path A's: a point lookup qualifies from 8 clusters up.
    /// Choosing wrong is not symmetric, which is why the margin sits on Path B's side: Path A on a small archetype costs microseconds, Path B on a large
    /// scattered one reads the whole archetype.
    /// </remarks>
    private const int MinClustersPerUniqueKeyForSelectiveScan = 8;

    /// <summary>
    /// Find the per-archetype index slot that owns <see cref="_whereComponentTable"/>, in EITHER index home. Returns an index into
    /// <see cref="ArchetypeClusterState.IndexSlots"/> when <paramref name="transientHome"/> is <c>false</c> and into
    /// <see cref="ArchetypeClusterState.TransientIndexSlots"/> when it is <c>true</c>; -1 when neither home indexes that component.
    /// </summary>
    /// <remarks>
    /// The two arrays are different closed generic types, so a caller has to know which one the returned index addresses — hence the out-parameter rather
    /// than one fused array. A component slot appears in exactly one home (its <see cref="StorageMode"/> decides which), so the search order carries no
    /// meaning; persistent goes first only because it is the common case. Every caller that reads a slot must branch on <paramref name="transientHome"/> —
    /// the search is the ONLY place that knows a second home exists (#655).
    /// </remarks>
    private int FindClusterIndexSlot(ArchetypeClusterState clusterState, ArchetypeMetadata meta, out bool transientHome)
    {
        var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];

        var ixSlots = clusterState.IndexSlots;
        if (ixSlots != null)
        {
            for (var s = 0; s < ixSlots.Length; s++)
            {
                if (engineState.SlotToComponentTable[ixSlots[s].Slot] == _whereComponentTable)
                {
                    transientHome = false;
                    return s;
                }
            }
        }

        var trSlots = clusterState.TransientIndexSlots;
        if (trSlots != null)
        {
            for (var s = 0; s < trSlots.Length; s++)
            {
                if (engineState.SlotToComponentTable[trSlots[s].Slot] == _whereComponentTable)
                {
                    transientHome = true;
                    return s;
                }
            }
        }

        transientHome = false;
        return -1;
    }

    /// <summary>
    /// Path A selective query: scan per-archetype B+Tree for the primary predicate range, collect ClusterLocations,
    /// then verify remaining predicates only on matched entities. Optimal for highly selective queries (&lt;5% match).
    /// </summary>
    private void ScanPerArchetypeBTreeSelective<TSink>(ExecutionPlan plan, FieldEvaluator[] evaluators, ArchetypeClusterState clusterState,
        ArchetypeMetadata meta, ref TSink result) where TSink : struct, IEntityIdSink
    {
        // Persistent home only: the range scan below is typed to BTreeBase<PersistentStore>. ScanAllArchetypes never routes a Transient home here, so this is
        // a guard against a future caller rather than a live branch (#655).
        var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
        if (ixSlotIdx < 0 || transientHome)
        {
            return;
        }

        var ixSlots = clusterState.IndexSlots;

        ref var matchSlot = ref ixSlots[ixSlotIdx];
        var layout = clusterState.Layout;
        var compSlot = matchSlot.Slot;
        var compSize = layout.ComponentSize(compSlot);
        var compOffset = layout.ComponentOffset(compSlot);

        // QENABLE-01, as Path B: the tree holds a disabled component's entry, and an absent Versioned one's under the slot's bytes.
        if (!TryCreateEnabledGate(meta, layout, compSlot, out var gate))
        {
            return;
        }

        // Find the primary field's B+Tree matching the plan's PrimaryFieldIndex
        if (plan.PrimaryFieldIndex < 0 || plan.PrimaryFieldIndex >= matchSlot.Fields.Length)
        {
            // Fall back to Path B (full scan) if primary field not found
            ScanPerArchetypeBTree(evaluators, clusterState, meta, ref result);
            return;
        }

        ref var primaryField = ref matchSlot.Fields[plan.PrimaryFieldIndex];
        var primaryIndex = primaryField.Index;

        // Step 1: Range scan B+Tree → collect ClusterLocations grouped by clusterChunkId.
        // Use a flat array indexed by clusterChunkId (bounded by segment ChunkCapacity, typically small).
        var chunkCapacity = clusterState.ClusterSegment.ChunkCapacity;
        var matchBitsArr = ArrayPool<ulong>.Shared.Rent(chunkCapacity);
        UpdatedClusterSet updatedClusters = default;
        try
        {
            Array.Clear(matchBitsArr);   // whole: a cluster created since the capacity read indexes past it, into what the last renter left
            var hasAny = false;

            CollectClusterLocationsFromBTree(primaryIndex, plan.PrimaryKeyType, plan.PrimaryScanMin, plan.PrimaryScanMax, primaryField.AllowMultiple,
                matchBitsArr, ref hasAny);

            // MVCC born/died gate — the same one Path B applies in ScanClusterSoa, for the same reason: the B+Tree leaf names the committed HEAD's slot and
            // the occupancy word is CURRENT, so neither knows the reader's snapshot. Path A carried NO gate while it was unreachable, so un-pinning stream
            // selection without this would have re-opened on Path A exactly the phantom read #674 closed on Path B — and silently, since the two paths answer
            // the same query and the planner picks between them on an estimate (#675).
            var visGated = meta.VersionedSlotMask != 0;
            var txTsn = _tx.TSN;

            // #1347: a destroy removes its entity's tree entries at its publish, while a snapshot older than it still sees the entity, hidden in its slot
            // (VIS-09). Read AFTER the tree scan: a destroy folds its stamp before it removes the entries, so a scan that missed them reads it here above
            // this snapshot, and each cluster a death newer than the snapshot touched is then read whole, as the SoA scan reads it. The barrier keeps the
            // tree's leaf loads from sinking below the stamp's acquire on arm64 (an acquire orders only what follows it); JIT-folded on x64.
            if (!X86Base.IsSupported)
            {
                Interlocked.MemoryBarrier();
            }

            var walkDeaths = visGated && clusterState.MaxDestroyStamp > txTsn;
            QueryPathProbe.SelectiveAfterTreeScanHook?.Invoke();

            // VIS-12 (#674): the clusters an update newer than this snapshot touched — read after the tree scan, as the destroy stamp is: an update folds its
            // cluster's watermark and its archetype's stamp before it moves the entity's entries. The tree holds those entities at keys newer than this
            // snapshot, and their HEAD the newer values, so each such cluster is read whole at the snapshot instead.
            var snapshotReads = visGated && (meta.VersionedSlotMask & (1 << compSlot)) != 0;
            updatedClusters = snapshotReads ? ClustersUpdatedAfterSnapshot(clusterState) : default;
            if (!hasAny && !walkDeaths && updatedClusters.IsEmpty)
            {
                return;
            }

            // Predicates the RANGE has already enforced. The tree scan above yielded exactly the rows satisfying every predicate on the primary field, so
            // testing them again on those rows is duplicated work — and it was the whole reason this path could not win: re-evaluating them means a full
            // 64-slot pass over every cluster the scan touched, which is precisely Path B's per-cluster cost, leaving Path A as Path B plus a tree scan.
            //
            // Gated on the planner vouching for the range rather than assumed from the op, because ComputeBounds widens in four cases and each would turn a
            // skipped evaluator into wrong rows: NotEqual, strict inequalities on floating types, integer inequalities saturating at the type extent, and NaN
            // thresholds. int.MinValue is the "matches nothing" sentinel — FieldIndex is never negative.
            //
            // QFENCE-01: the tree vouches only for entities it is current for. A slot whose index the tick fence maintains keeps its pre-write key until the
            // fence moves it, so an entity written since the last fence — its bit set in the shadow bitmap before the write lands — may sit under a key its
            // value has left. For those entities (fenceDirty below) the skipped evaluator runs after all, so a stale key never returns a row whose value no
            // longer matches. An entity whose NEW value matches is not looked for: the tree cannot find it until the fence, and checking every entity
            // written since would cost each query in proportion to the tick's writes. The bitmap is skipped whole unless this component was written since.
            var enforcedByScanFieldIndex = plan.PrimaryRangeAdmitsOnlyMatches ? plan.PrimaryFieldIndex : int.MinValue;
            var shadowBitmap = enforcedByScanFieldIndex >= 0 && clusterState.MayHaveFenceStaleKeys(compSlot) ? clusterState.ClusterShadowBitmap : null;

            // Pre-determine SIMD eligibility for each evaluator (once, before cluster loop)
            var evalCount = evaluators.Length;
            var anySimd = false;
            var simdEligible = evalCount <= 8 ? stackalloc bool[8] : new bool[evalCount];
            if (Avx2.IsSupported)
            {
                for (var e = 0; e < evalCount; e++)
                {
                    simdEligible[e] = evaluators[e].FieldIndex != enforcedByScanFieldIndex
                                      && SimdPredicateEvaluator.IsSimdEligible(evaluators[e].KeyType);
                    anySimd |= simdEligible[e];
                }
            }

            var clusterSize = layout.ClusterSize;
            byte* snapshotColumn = null;   // the transaction's (FillSnapshotColumn), taken at the first cluster read at the snapshot

            var visState = visGated ? _tx.DBE._archetypeStates[meta.ArchetypeId] : null;
            var visRecordSize = meta._entityRecordSize;
            byte* visBuf = stackalloc byte[visRecordSize];
            var visAccessor = visState != null ? visState.EntityMap.Segment.CreateScanAccessor() : default;

            // Step 2: For each active cluster with matches, verify ALL evaluators on matched entities
            // CLUSTERWALK-02: one count-first read through the one reader, hoisted out of the loop — see ScanClusterSoa for why the per-iteration
            // plain loads this replaced are safe on x64 and not on arm64.
            var activeIds = clusterState.ReadActiveClusterList(out var activeCount);
            var clusterAccessor = clusterState.ClusterSegment.CreateScanAccessor();
            try
            {
                for (var c = 0; c < activeCount; c++)
                {
                    var clusterChunkId = activeIds[c];
                    var candidateBits = (uint)clusterChunkId < (uint)matchBitsArr.Length ? matchBitsArr[clusterChunkId] : 0;
                    var snapshotCluster = updatedClusters.Contains(clusterChunkId);
                    if (candidateBits == 0 && !walkDeaths && !snapshotCluster)
                    {
                        continue;   // before the slot words: a point lookup touches only the clusters the tree named
                    }

                    ref var slotWords = ref visGated
                        ? ref ArchetypeClusterState.SlotWords(clusterState.SlotWordTable, clusterChunkId)
                        : ref Unsafe.NullRef<long>();
                    var pendingDeaths = ArchetypeClusterState.PendingDeathsOf(ref slotWords);   // before the occupancy word — see ScanClusterSoa

                    // A cluster a death newer than this snapshot touched, or one pending, is read whole: its tree entries may be gone (#1347).
                    var wholeCluster = (walkDeaths && (pendingDeaths != 0 || ArchetypeClusterState.MaxDiedOf(ref slotWords) > txTsn)) || snapshotCluster;
                    if (candidateBits == 0 && !wholeCluster)
                    {
                        continue;
                    }

                    var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                    var occupancy = Volatile.Read(ref *(ulong*)clusterBase);   // acquire — see ScanClusterSoa for why plain is wrong here
                    if (visGated && Unsafe.IsNullRef(ref slotWords))
                    {
                        slotWords = ref ArchetypeClusterState.SlotWords(clusterState.SlotWordTable, clusterChunkId);   // see ScanClusterSoa
                    }

                    // VIS-09: a slot claimed and not occupied, read after the occupancy word — a destroyed entity hidden until the cleanup, or a spawn not yet
                    // published. A candidate in one was hidden after the tree read; a cluster read whole visits each. Its record decides it, below.
                    var pend = visGated ? ArchetypeClusterState.ClaimOf(ref slotWords) & ~occupancy : 0;
                    var hidden = wholeCluster ? pend : candidateBits & pend;

                    // Live entities the enabled gate admits: the tree's candidates, or every occupied slot of a cluster read whole — whose predicates the
                    // range has not enforced.
                    var remaining = (wholeCluster ? occupancy : candidateBits & occupancy) & gate.ClusterMask(clusterBase, out var gateExact);
                    var clusterEnforced = wholeCluster ? int.MinValue : enforcedByScanFieldIndex;

                    if (remaining == 0 && hidden == 0)
                    {
                        continue;
                    }

                    // H1 — same cluster-granularity visibility as the SoA scan (see ScanClusterSoa). Path A raised the stake rather than lowering it: the
                    // tree has already narrowed the candidate set, so the per-match probe is a LARGER share of what remains than it is on a full scan.
                    var clusterVisGated = visGated && (pendingDeaths != 0 || !clusterState.AreClusterWatermarksVisibleAt(clusterChunkId, txTsn));

                    // The cluster's entities written since the last fence: one word, as the shadow bitmap indexes entities by chunkId * 64 + slot.
                    var fenceDirty = shadowBitmap?.ReadWord(clusterChunkId) ?? 0;

                    var compBase = clusterBase + compOffset;
                    if (snapshotCluster)
                    {
                        var present = FillSnapshotColumn(meta, visState, clusterState, clusterBase, compSlot, compSize, remaining | hidden, out snapshotColumn,
                            visBuf, ref visAccessor);
                        remaining &= present;
                        hidden &= present;
                        compBase = snapshotColumn;
                    }
                    else if (snapshotReads && QueryPathProbe.BeforeHeadEvaluateArmed)
                    {
                        QueryPathProbe.BeforeHeadEvaluateHook?.Invoke();
                    }

                    // The range the tree enforced is skipped, except for an entity written since the fence — its entry may not have moved yet. The hidden
                    // slots are matched on every predicate: their tree entries say nothing.
                    var matching = MatchingSlots(evaluators, simdEligible, anySimd, compBase, compSize, clusterSize, remaining, fenceDirty, clusterEnforced);
                    var hiddenMatching = MatchingSlots(evaluators, simdEligible, anySimd, compBase, compSize, clusterSize, hidden, ~0UL, int.MinValue);

                    // VIS-12: the HEAD read validated after it — see ScanClusterSoa. The tree read came before the archetype's stamp read, so the candidates
                    // stand; only the bytes they are verified on are taken at the snapshot.
                    if (snapshotReads && !snapshotCluster)
                    {
                        Volatile.ReadBarrier();   // see ScanClusterSoa

                        if (ArchetypeClusterState.MaxUpdatedOf(ref slotWords) > txTsn)
                        {
                            var present = FillSnapshotColumn(meta, visState, clusterState, clusterBase, compSlot, compSize, remaining | hidden,
                                out snapshotColumn, visBuf, ref visAccessor);
                            remaining &= present;
                            hidden &= present;
                            compBase = snapshotColumn;
                            matching = MatchingSlots(evaluators, simdEligible, anySimd, compBase, compSize, clusterSize, remaining, fenceDirty,
                                clusterEnforced);
                            hiddenMatching = MatchingSlots(evaluators, simdEligible, anySimd, compBase, compSize, clusterSize, hidden, ~0UL, int.MinValue);
                        }
                    }

                    while (matching != 0)
                    {
                        var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(matching);
                        matching &= matching - 1;
                        var rawId = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                        if (IsVisibleAtSnapshot(rawId, clusterVisGated, visState, visBuf, visRecordSize, txTsn, _tx.DBE.CommitCards, ref visAccessor)
                            && (!gate.NeedsPerSlot(gateExact) || gate.Admits(rawId, clusterBase, slotIndex, gateExact, visBuf, ref visAccessor)))
                        {
                            result.Add(EntityId.FromRaw(rawId));
                        }
                    }

                    // The hidden slots, matched on the bytes the slot kept: each decided by its record, as the SoA scan decides it.
                    while (hiddenMatching != 0)
                    {
                        var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(hiddenMatching);
                        hiddenMatching &= hiddenMatching - 1;
                        var rawId = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                        if (IsReservedSpawnVisible(rawId, clusterChunkId, slotIndex, visState, visBuf, txTsn, _tx.DBE.CommitCards, ref visAccessor)
                            && gate.AdmitsRecord(rawId, visBuf))
                        {
                            result.Add(EntityId.FromRaw(rawId));
                        }
                    }
                }
            }
            finally
            {
                clusterAccessor.Dispose();
                if (visState != null)
                {
                    visAccessor.Dispose();
                }
            }
        }
        finally
        {
            updatedClusters.Return();
            ArrayPool<ulong>.Shared.Return(matchBitsArr);
        }
    }

    /// <summary>
    /// Range scan a per-archetype B+Tree, collecting ClusterLocation values grouped by clusterChunkId into per-cluster bitmasks.
    /// Dispatches on <see cref="KeyType"/> to call the typed B+Tree range scan API.
    /// </summary>
    /// <remarks>
    /// Scan bounds are stored as raw <c>long</c> in <see cref="ExecutionPlan"/>. For float/double, the lower 32/64 bits
    /// hold the IEEE 754 bit pattern. Use <see cref="BitConverter"/> (JIT intrinsic, zero overhead) for safe reinterpretation
    /// instead of <c>Unsafe.As</c> on temporaries (which creates dangling refs to stack values).
    /// ULong is stored as a <see cref="BTree{TKey,TStore}"/> keyed by <c>long</c> (same convention as <see cref="PipelineExecutor"/>).
    /// </remarks>
    private static void CollectClusterLocationsFromBTree(BTreeBase<PersistentStore> index, KeyType keyType, long scanMin, long scanMax,
        bool allowMultiple, ulong[] matchBitsArr, ref bool hasAny)
    {
        switch (keyType)
        {
            case KeyType.Int:
                CollectTyped((BTree<int, PersistentStore>)index, (int)scanMin, (int)scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.Long:
                CollectTyped((BTree<long, PersistentStore>)index, scanMin, scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.Float:
                CollectTyped((BTree<float, PersistentStore>)index, BitConverter.Int32BitsToSingle((int)scanMin), BitConverter.Int32BitsToSingle((int)scanMax),
                    allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.Double:
                CollectTyped((BTree<double, PersistentStore>)index, BitConverter.Int64BitsToDouble(scanMin), BitConverter.Int64BitsToDouble(scanMax),
                    allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.Short:
                CollectTyped((BTree<short, PersistentStore>)index, (short)scanMin, (short)scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.Byte:
                CollectTyped((BTree<byte, PersistentStore>)index, (byte)scanMin, (byte)scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.SByte:
                CollectTyped((BTree<sbyte, PersistentStore>)index, (sbyte)scanMin, (sbyte)scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.UShort:
                CollectTyped((BTree<ushort, PersistentStore>)index, (ushort)scanMin, (ushort)scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.UInt:
                CollectTyped((BTree<uint, PersistentStore>)index, (uint)scanMin, (uint)scanMax, allowMultiple, matchBitsArr, ref hasAny);
                break;
            case KeyType.ULong:
                // #676: the ULong trees are genuinely ulong-keyed, so this must cast to the ulong tree and reinterpret the bounds unsigned. Casting to
                // BTree<long> was correct only while the tree was declared L64BTree<long>, which is the storage-level defect #676 fixed; leaving it would
                // now throw InvalidCastException on every ulong-indexed query instead of silently mis-ordering.
                CollectTyped((BTree<ulong, PersistentStore>)index, unchecked((ulong)scanMin), unchecked((ulong)scanMax), allowMultiple, matchBitsArr,
                    ref hasAny);
                break;
            default:
                // Bool and String64 have no typed tree here. Falling out of the switch would leave `hasAny` false and the query would answer EMPTY — the #663
                // shape, and undetectable from the outside. PlanBuilder must not propose them (KeyRange.IsStreamable); this is the assertion that the two
                // stay in agreement.
                ThrowHelper.ThrowInvalidOp(
                    $"Query plan selected key type {keyType} as its primary scan stream, but no B+Tree range scan exists for it. KeyRange.IsStreamable must "
                    + "reject this type so the query takes the SoA scan instead.");
                break;
        }
    }

    /// <summary>
    /// Typed B+Tree range scan that collects ClusterLocations into per-cluster bitmasks.
    /// </summary>
    private static void CollectTyped<TKey>(BTree<TKey, PersistentStore> tree, TKey minKey, TKey maxKey, bool allowMultiple, ulong[] matchBitsArr, 
        ref bool hasAny) where TKey : unmanaged
    {
        if (allowMultiple)
        {
            using var enumerator = tree.EnumerateRangeMultiple(minKey, maxKey);
            while (enumerator.MoveNextKey())
            {
                do
                {
                    var values = enumerator.CurrentValues;
                    for (var i = 0; i < values.Length; i++)
                    {
                        var clusterLocation = values[i];
                        var chunkId = clusterLocation >> 6;
                        var slotIdx = clusterLocation & 0x3F;
                        if ((uint)chunkId < (uint)matchBitsArr.Length)
                        {
                            matchBitsArr[chunkId] |= 1UL << slotIdx;
                            hasAny = true;
                        }
                    }
                } while (enumerator.NextChunk());
            }
        }
        else
        {
            using var enumerator = tree.EnumerateRange(minKey, maxKey);
            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                var clusterLocation = item.Value;
                var chunkId = clusterLocation >> 6;
                var slotIdx = clusterLocation & 0x3F;
                if ((uint)chunkId < (uint)matchBitsArr.Length)
                {
                    matchBitsArr[chunkId] |= 1UL << slotIdx;
                    hasAny = true;
                }
            }
        }
    }

    /// <summary>
    /// Execute a spatial-driven query: spatial index produces candidate EntityIds, filtered by archetype mask, visibility, and WHERE.
    /// </summary>
    private HashSet<EntityId> ExecuteSpatial()
    {
        var state = _spatialTable.SpatialIndex;
        var result = new HashSet<EntityId>(_take > 0 ? _take : 64);
        var tx = _tx;

        // Fan out to the per-archetype cluster spatial index — the only index home there is. Before #872 step 13 an entity-level R-Tree was traversed first
        // and its results unioned in; that tree had had no writer since #666, so the traversal was a full descent of an empty structure on every spatial
        // query. Every shape now resolves here: AABB and Radius since #230 Phase 3 Option B, Ray and Frustum since step 13 wired in the step-9 walks.
        // The SpatialGrid is guaranteed non-null for cluster spatial archetypes (enforced at DatabaseEngine.InitializeArchetypes).
        if (state.ClusterArchetypes != null)
        {
            var grid = _tx.DBE.RealmGridForQuery(_spatialRealm);

            // MVCC born/died gate, the same one the SoA scan applies (04-data.md "Isolation guarantees").
            //
            // The spatial index walks CURRENT occupancy and knows nothing about the reader's snapshot, so without this an entity committed AFTER the
            // snapshot is returned - the phantom read the fixed snapshot is specified to prevent. The SAME transaction's scan path already hides it, so the
            // two predicates disagreed about which entities exist, and the spatial one was the wrong side of the disagreement (#899).
            //
            // Only Versioned archetypes promise isolation - the storage-mode matrix is explicit that SingleVersion and Transient offer none - so a
            // non-Versioned spatial archetype keeps the lookup-free collect it always had.
            //
            // The record buffer is sized ONCE, to the widest record among the gated archetypes, and reused across them: a stackalloc inside the loop below
            // would grow the frame once per archetype rather than once per query.
            var txTsn = _tx.TSN;
            var visRecordSize = 0;
            foreach (var cs in state.ClusterArchetypes)
            {
                if (!cs.SpatialSlot.HasSpatialIndex)
                {
                    continue;
                }

                var probe = ArchetypeRegistry.GetMetadata((ushort)cs.ArchetypeId);
                if (probe.VersionedSlotMask != 0 && probe._entityRecordSize > visRecordSize)
                {
                    visRecordSize = probe._entityRecordSize;
                }
            }

            byte* visBuf = stackalloc byte[visRecordSize > 0 ? visRecordSize : 1];

            foreach (var cs in state.ClusterArchetypes)
            {
                if (!cs.SpatialSlot.HasSpatialIndex)
                {
                    continue;
                }

                var meta = ArchetypeRegistry.GetMetadata((ushort)cs.ArchetypeId);
                var visGated = meta.VersionedSlotMask != 0;
                var visState = visGated ? _tx.DBE._archetypeStates[meta.ArchetypeId] : null;
                var visAccessor = visState != null ? visState.EntityMap.Segment.CreateScanAccessor() : default;
                try
                {
                    if (_spatialQueryType == SpatialQueryType.AABB)
                    {
                        // The max corner is ALWAYS at [3]/[4], for both dimensions. Only Z varies.
                        //
                        // This block used to read qMaxX from [2] and qMaxY from [3] for a 2D component, taking the caller's minZ as maxX and their maxX as
                        // maxY. WhereInAABB documents and packs six doubles as (minX, minY, minZ, maxX, maxY, maxZ) whatever the dimension, so a 2D query
                        // got a garbage box and a silently empty answer: an SQ-01 false negative with no exception. It survived because every EcsQuery
                        // spatial test uses a 3D component, and because the Workbench's QuerySpecCompiler re-packed its arguments to compensate - a
                        // workaround at a call site three projects away, which is how a defect gets mistaken for a convention.
                        // No narrowing: _spatialParams has always been doubles and QueryAabb takes doubles since #914, so the (float) casts that used to sit
                        // here were a lossy round trip through a width neither end asked for.
                        var qMinX = _spatialParams[0];
                        var qMinY = _spatialParams[1];
                        var qMaxX = _spatialParams[3];
                        var qMaxY = _spatialParams[4];

                        // A 2D archetype stores its clusters on a flat Z slab, so an unbounded Z accepts them whatever the caller passed.
                        var is3D = state.Descriptor.CoordCount == 6;
                        var qMinZ = is3D ? _spatialParams[2] : double.NegativeInfinity;
                        var qMaxZ = is3D ? _spatialParams[5] : double.PositiveInfinity;

                        // One-entry memo over the cluster summary. The enumerator drains a whole cluster's occupancy bits before advancing, so consecutive
                        // hits share a chunk id — without this the four acquire loads inside IsClusterFullyVisibleAt would be paid per HIT where the SoA
                        // scan pays them per CLUSTER, 256 against 4 for a full cluster.
                        var memoChunkId = -1;
                        var memoFullyVisible = false;

                        using var guard = EpochGuard.Enter(_tx.DBE.EpochManager);
                        foreach (var hit in cs.QueryAabb(grid, qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ))
                        {
                            var entityId = hit.Entity;
                            if (!MaskTestByRouting(entityId.ArchetypeId))
                            {
                                continue;
                            }

                            // The per-cluster summary first, exactly as the SoA scan uses it: a cluster every one of whose entities was born at or before
                            // this snapshot, with every death already visible to it, needs no per-entity EntityMap probe. The hit carries its
                            // ClusterChunkId, so the memo above answers most hits for nothing and a miss costs four acquire loads against the ~80-166 ns
                            // point lookup it replaces.
                            //
                            // The occupancy word those loads are ordered against is read with an acquire by the enumerator (OpenOccupancy), which is
                            // required rather than incidental: an acquire inside IsClusterFullyVisibleAt does not stop an EARLIER plain load from sinking
                            // past it, so a plain read there would let arm64 pair a fresh occupancy word with a stale born watermark — a phantom, and the
                            // exact hazard the SoA scan's own comment records.
                            if (visGated && hit.ClusterChunkId != memoChunkId)
                            {
                                memoChunkId = hit.ClusterChunkId;
                                memoFullyVisible = cs.IsClusterFullyVisibleAt(memoChunkId, txTsn);
                            }

                            var hitGated = visGated && !memoFullyVisible;
                            if (IsVisibleAtSnapshot(unchecked((long)hit.Entity.RawValue), hitGated, visState, visBuf, meta._entityRecordSize, txTsn, _tx.DBE.CommitCards,
                                    ref visAccessor))
                            {
                                result.Add(entityId);
                            }
                        }
                    }
                    else if (_spatialQueryType == SpatialQueryType.Radius)
                    {
                        // Per-cell cluster index Radius query (issue #230 Phase 3). Parameter layout matches QuerySingleTree's Radius case:
                        // _spatialParams[0..halfCoord] is the center, _spatialParams[3] is the radius (regardless of dimension - a quirk of the existing
                        // parameter packing for the per-entity tree).
                        var cX = _spatialParams[0];
                        var cY = _spatialParams[1];
                        var cZ = state.Descriptor.CoordCount == 6 ? _spatialParams[2] : 0d;
                        var radius = _spatialParams[3];

                        // See the AABB branch: consecutive hits share a cluster, so the summary is consulted once per cluster rather than once per hit.
                        var memoChunkId = -1;
                        var memoFullyVisible = false;

                        using var guard = EpochGuard.Enter(_tx.DBE.EpochManager);
                        foreach (var hit in cs.QueryRadius(grid, cX, cY, cZ, radius))
                        {
                            var entityId = hit.Entity;
                            if (!MaskTestByRouting(entityId.ArchetypeId))
                            {
                                continue;
                            }

                            if (visGated && hit.ClusterChunkId != memoChunkId)
                            {
                                memoChunkId = hit.ClusterChunkId;
                                memoFullyVisible = cs.IsClusterFullyVisibleAt(memoChunkId, txTsn);
                            }

                            var hitGated = visGated && !memoFullyVisible;
                            if (IsVisibleAtSnapshot(unchecked((long)hit.Entity.RawValue), hitGated, visState, visBuf, meta._entityRecordSize, txTsn, _tx.DBE.CommitCards,
                                    ref visAccessor))
                            {
                                result.Add(entityId);
                            }
                        }
                    }
                    else if (_spatialQueryType == SpatialQueryType.Ray)
                    {
                        CollectClusterRay(cs, grid, result, visGated, visState, visBuf, meta._entityRecordSize, txTsn, ref visAccessor);
                    }
                    else if (_spatialQueryType == SpatialQueryType.Frustum)
                    {
                        CollectClusterFrustum(cs, grid, state, result, visGated, visState, visBuf, meta._entityRecordSize, txTsn, ref visAccessor);
                    }
                    else
                    {
                        // Every shape the builder can set is handled above. This is the guard for a shape ADDED to SpatialQueryType without a cluster branch,
                        // the alternative being a query that silently returns nothing, which is an SQ-01 false negative that no differential test would catch
                        // because the shape would have no oracle either.
                        throw new NotSupportedException($"Cluster spatial queries for shape '{_spatialQueryType}' have no implementation.");
                    }
                }
                finally
                {
                    if (visState != null)
                    {
                        visAccessor.Dispose();
                    }
                }
            }
        }

        // An entity this transaction destroyed is gone for it: the spatial index still holds it.
        if (tx.PendingDestroys is { Count: > 0 } destroyedHere)
        {
            result.ExceptWith(destroyedHere);
        }

        // QENABLE-01: the spatial index knows nothing of enabled bits, so the query's Enabled<U>() / Disabled<U>() are applied per candidate.
        if (HasT2 && result.Count > 0)
        {
            List<EntityId> refused = null;
            foreach (var id in result)
            {
                if (!AdmitsT2(id))
                {
                    (refused ??= []).Add(id);
                }
            }

            if (refused != null)
            {
                foreach (var id in refused)
                {
                    result.Remove(id);
                }
            }
        }

        // Opaque WHERE post-filter (.Where(lambda))
        var readDisabled = DisabledNames;
        var filter = _whereFilter;
        if (filter != null)
        {
            result.RemoveWhere(id => !filter(id, tx, readDisabled));
        }

        // WhereField composition: when a field predicate is combined with a spatial predicate, the spatial index drives the candidate set and the compiled
        // field predicate is verified per-candidate (AND semantics). _pendingSpawnFieldFilter is the compiled form of every WhereField call chained with
        // AND — reused here as a general per-entity field verifier.
        var fieldFilter = _pendingSpawnFieldFilter;
        if (fieldFilter != null)
        {
            // The verifier is a point read, which sees this transaction's own supply; to its field queries that component is absent until it commits.
            var predicateTypeId = ArchetypeRegistry.GetComponentTypeId(_whereComponentTable.Definition.POCOType);
            result.RemoveWhere(id => tx.FirstSuppliedHere((long)id.RawValue, predicateTypeId) || !fieldFilter(id, tx, readDisabled));
        }

        return result;
    }

    /// <summary>
    /// Ray query against one cluster archetype, collected into <paramref name="result"/>.
    /// </summary>
    /// <remarks>
    /// <b>Grown until it is not truncated</b> — see <see cref="InitialClusterResultCapacity"/> for why a fixed buffer would be an <c>SQ-01</c> false negative.
    /// The cluster API returns hits front-to-back; that ordering is discarded here because <c>ExecuteSpatial</c>'s contract is a set, and honouring it would
    /// mean merging across archetypes on a distance this method no longer carries.
    /// </remarks>
    /// <remarks>
    /// Takes the MVCC gate by parameter rather than rebuilding it: the caller has already resolved it once for this archetype, and the record buffer is
    /// sized for the whole query (SQ-07). Unlike the AABB and radius shapes this one has no per-cluster shortcut available — <c>QueryRay</c> yields bare
    /// entity ids with no <c>ClusterChunkId</c> — so a gated archetype pays the EntityMap probe on every hit. That is a property of the result type, not a
    /// choice.
    /// </remarks>
    private void CollectClusterRay(ArchetypeClusterState cs, SpatialGrid grid, HashSet<EntityId> result, bool visGated, ArchetypeEngineState visState,
        byte* visBuf, int visRecordSize, long txTsn, ref ChunkAccessor<PersistentStore> visAccessor)
    {
        // No narrowing: WhereRay takes doubles and QueryRay takes doubles since #919 F2. The casts that used to sit here quantised BOTH the origin and the
        // direction — and a quantised direction is the worse of the two, because the error grows with distance along the ray rather than staying bounded.
        var originX = _spatialParams[0];
        var originY = _spatialParams[1];
        var originZ = _spatialParams[2];
        var dirX = _spatialParams[3];
        var dirY = _spatialParams[4];
        var dirZ = _spatialParams[5];
        var maxDist = _spatialParams[6];

        var capacity = InitialClusterResultCapacity;
        while (true)
        {
            var pooled = capacity <= MaxPooledResultCapacity;
            var buffer = pooled
                ? ArrayPool<(long entityId, double distance)>.Shared.Rent(capacity)
                : new (long entityId, double distance)[capacity];
            try
            {
                var span = buffer.AsSpan(0, capacity);
                int hits;
                using (EpochGuard.Enter(_tx.DBE.EpochManager))
                {
                    // ordered: false — the sort's output goes straight into a HashSet below. See QueryRay's remarks.
                    hits = cs.QueryRay(grid, originX, originY, originZ, dirX, dirY, dirZ, maxDist, span, ordered: false);
                }

                if (hits == capacity)
                {
                    if (capacity >= MaxClusterResultCapacity)
                    {
                        throw new InvalidOperationException(
                            $"A cluster spatial query filled its {capacity:N0}-entry ceiling, so the result may be truncated. Narrow the query region "
                            + "rather than accepting a silently partial answer.");
                    }

                    capacity <<= 1;
                    continue;
                }

                for (var i = 0; i < hits; i++)
                {
                    var entityId = EntityId.FromRaw(span[i].entityId);
                    if (MaskTestByRouting(entityId.ArchetypeId)
                        && IsVisibleAtSnapshot(span[i].entityId, visGated, visState, visBuf, visRecordSize, txTsn, _tx.DBE.CommitCards, ref visAccessor))
                    {
                        result.Add(entityId);
                    }
                }

                return;
            }
            finally
            {
                if (pooled)
                {
                    ArrayPool<(long entityId, double distance)>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <summary>Frustum query against one cluster archetype, collected into <paramref name="result"/>.</summary>
    /// <remarks><inheritdoc cref="CollectClusterRay" path="/remarks"/></remarks>
    private void CollectClusterFrustum(ArchetypeClusterState cs, SpatialGrid grid, SpatialIndexState state, HashSet<EntityId> result, bool visGated,
        ArchetypeEngineState visState, byte* visBuf, int visRecordSize, long txTsn, ref ChunkAccessor<PersistentStore> visAccessor)
    {
        // The caller packs planes for the component's dimension; a 2D component takes 3 doubles per plane and a 3D one takes 4. Checking here rather than
        // letting the cluster query throw keeps the message in terms of the API the user called.
        var stride = state.Descriptor.CoordCount == 6 ? 4 : 3;
        var needed = _frustumPlaneCount * stride;
        if (_frustumPlanes == null || _frustumPlanes.Length < needed)
        {
            throw new ArgumentException(
                $"A {(stride == 4 ? "3D" : "2D")} frustum query needs {needed} doubles for {_frustumPlaneCount} planes ({stride} each), got "
                + $"{_frustumPlanes?.Length ?? 0}.");
        }

        // No narrowing: WhereFrustum takes doubles, the planes below are doubles, and the cell range they resolve to is computed in the f64 world frame
        // (#919 F2). The (float) casts that used to sit here quantised the caller's bounding box to ~128-unit steps at 10^9 — which for a frustum shows up
        // as the CELL RANGE being wrong, so entire cells of the view drop out rather than individual entities.
        var boundsMin = new Vector3Like(_spatialParams[0], _spatialParams[1], _spatialParams[2]);
        var boundsMax = new Vector3Like(_spatialParams[3], _spatialParams[4], _spatialParams[5]);
        var planes = _frustumPlanes.AsSpan(0, needed);

        var capacity = InitialClusterResultCapacity;
        while (true)
        {
            var pooled = capacity <= MaxPooledResultCapacity;
            var buffer = pooled ? ArrayPool<long>.Shared.Rent(capacity) : new long[capacity];
            try
            {
                var span = buffer.AsSpan(0, capacity);
                int hits;
                using (EpochGuard.Enter(_tx.DBE.EpochManager))
                {
                    hits = cs.QueryFrustum(grid, planes, _frustumPlaneCount, boundsMin, boundsMax, span);
                }

                if (hits == capacity)
                {
                    if (capacity >= MaxClusterResultCapacity)
                    {
                        throw new InvalidOperationException(
                            $"A cluster spatial query filled its {capacity:N0}-entry ceiling, so the result may be truncated. Narrow the query region "
                            + "rather than accepting a silently partial answer.");
                    }

                    capacity <<= 1;
                    continue;
                }

                for (var i = 0; i < hits; i++)
                {
                    var entityId = EntityId.FromRaw(span[i]);
                    if (MaskTestByRouting(entityId.ArchetypeId)
                        && IsVisibleAtSnapshot(span[i], visGated, visState, visBuf, visRecordSize, txTsn, _tx.DBE.CommitCards, ref visAccessor))
                    {
                        result.Add(entityId);
                    }
                }

                return;
            }
            finally
            {
                if (pooled)
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <summary>Evaluate pending spawns against the compiled WhereField predicate.</summary>
    private void CollectPendingSpawnsWithFieldFilter(HashSet<EntityId> result)
    {
        var tx = _tx;
        var pendingFieldFilter = _pendingSpawnFieldFilter;
        if (pendingFieldFilter == null)
        {
            return;
        }

        var pending = tx.PendingSpawns;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var destroys = tx.PendingDestroys;
        var enableDisable = tx.PendingEnableDisable;
        var readDisabled = DisabledNames;
        for (var i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];
            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }
            if (!MaskTestByRouting(entry.Id.ArchetypeId))
            {
                continue;
            }

            // QENABLE-01: the query's Enabled<U>() / Disabled<U>() against the spawn's bits, this transaction's enables and disables over them — as
            // CollectPendingSpawns applies them for a query without a field predicate.
            if (HasT2)
            {
                var bits = enableDisable != null && enableDisable.TryGetValue(entry.Id, out var overrideBits) ? overrideBits : entry.EnabledBits;
                if (!ResolveT2Masks(tx.DBE.GetMetaByRouting(entry.Id.ArchetypeId), out var requiredEnabled, out var requiredDisabled)
                    || (bits & requiredEnabled) != requiredEnabled || (bits & requiredDisabled) != 0)
                {
                    continue;
                }
            }

            if (pendingFieldFilter(entry.Id, tx, readDisabled))
            {
                result.Add(entry.Id);
            }
        }
    }

    /// <summary>Count matching entities.</summary>
    public int Count(
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site; consumed by trace emission in P2 (issue #335).
        _ = callerFile; _ = callerLine; _ = callerMethod;
        CheckRealmScope();
        if (_skip > 0 || _take > 0)
        {
            throw new InvalidOperationException(
                "Count() ignores Skip / Take — it reports the full match count. Remove Skip / Take, or use ExecuteOrdered().Count for a paged count.");
        }
        var scope = TyphonEvent.BeginEcsQueryCount(0);
        try
        {
            if (MaskIsEmpty)
            {
                scope.ScanMode = EcsQueryScanMode.Empty;
                scope.ResultCount = 0;
                return 0;
            }

            // Spatial-driven count: the spatial index drives the candidate set; ExecuteSpatial ANDs any field/Where predicate per-candidate. Checked before
            // the field-only path so spatial + WhereField composes.
            if (_spatialQueryType != SpatialQueryType.None)
            {
                var spatialCount = ExecuteSpatial().Count;
                scope.ScanMode = EcsQueryScanMode.Spatial;
                scope.ResultCount = spatialCount;
                return spatialCount;
            }

            // Targeted count through the per-archetype scan. The PipelineExecutor alternative that used to sit here read the shared ComponentTable index and is
            // gone with it (#629); ScanAllArchetypes raises if an archetype has no per-archetype index, so this no longer needs a fallback of its own.
            if (HasFieldPredicates)
            {
                var targetedCount = ExecuteTargeted().Count;
                scope.ScanMode = EcsQueryScanMode.TargetedCluster;
                scope.ResultCount = targetedCount;
                return targetedCount;
            }

            // If WHERE filter, use Execute (which applies post-filter) then count
            if (_whereFilter != null)
            {
                var executeCount = Execute().Count;
                scope.ScanMode = EcsQueryScanMode.Broad;
                scope.ResultCount = executeCount;
                return executeCount;
            }

            // Aggregation broad scan: count matches in place via the map's optimistic, copy-free CountEntries (no per-entity snapshot copy, no delegate) —
            // recovers the pre-#374 scan speed while keeping the OLC concurrency guarantee. Pending spawns (read-your-own-writes) are folded in by CountMatching.
            var count = CountMatching();
            scope.ScanMode = EcsQueryScanMode.Broad;
            scope.ResultCount = count;
            return count;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Test if any entity matches. Short-circuits on first match.</summary>
    public bool Any(
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site; consumed by trace emission in P2 (issue #335).
        _ = callerFile; _ = callerLine; _ = callerMethod;
        CheckRealmScope();
        if (_skip > 0 || _take > 0)
        {
            throw new InvalidOperationException("Any() ignores Skip / Take — it reports whether any entity matches. Remove Skip / Take.");
        }
        var scope = TyphonEvent.BeginEcsQueryAny(0);
        try
        {
            if (MaskIsEmpty)
            {
                scope.ScanMode = EcsQueryScanMode.Empty;
                scope.Found = false;
                return false;
            }

            // Spatial-driven existence check: spatial drives the candidate set; ExecuteSpatial ANDs any field/Where predicate. Checked before the field-only
            // path so spatial + WhereField composes.
            if (_spatialQueryType != SpatialQueryType.None)
            {
                var spatialFound = ExecuteSpatial().Count > 0;
                scope.ScanMode = EcsQueryScanMode.Spatial;
                scope.Found = spatialFound;
                return spatialFound;
            }

            if (HasFieldPredicates)
            {
                // Was the one terminal missing Count()'s cluster guard, so it answered FALSE for every cluster-backed archetype (#629). Now that the shared
                // ComponentTable home is gone there is nothing left to guard against — the per-archetype scan is the only path.
                var targetedFound = ExecuteTargeted().Count > 0;
                scope.ScanMode = EcsQueryScanMode.TargetedCluster;
                scope.Found = targetedFound;
                return targetedFound;
            }

            if (_whereFilter != null)
            {
                var hasMatch = Execute().Count > 0;
                scope.ScanMode = EcsQueryScanMode.Broad;
                scope.Found = hasMatch;
                return hasMatch;
            }

            // Existence broad scan: short-circuit via the map's optimistic, copy-free AnyEntry (mirrors CountMatching; folds in pending spawns).
            var found = AnyMatching();
            scope.ScanMode = EcsQueryScanMode.Broad;
            scope.Found = found;
            return found;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Get an enumerator for foreach support. Pre-collects matching entities then iterates.
    /// <para>
    /// Caller-attribute capture is NOT available here — the C# foreach pattern requires <c>GetEnumerator</c> to have zero parameters (optional parameters don't
    /// satisfy the pattern). Execution-site attribution for foreach loops falls back to the query's construction site (captured at <c>tx.Query&lt;T&gt;()</c>).
    /// For explicit execution-site capture, call <see cref="Execute"/>, <see cref="Count"/>, etc., instead.
    /// </para>
    /// </summary>
    public EcsQueryEnumerator GetEnumerator()
    {
        CheckRealmScope();
        // foreach runs a broad archetype scan + the .Where(lambda) post-filter only. It does NOT apply WhereField, spatial, or OrderBy/Skip/Take — guard
        // against silently iterating the wrong set.
        if (HasFieldPredicates)
        {
            throw new InvalidOperationException("foreach does not apply WhereField predicates — call .Execute() to materialise the matches (or .ToView()).");
        }
        if (_spatialQueryType != SpatialQueryType.None)
        {
            throw new InvalidOperationException(
                "foreach does not apply spatial predicates (WhereNearby / WhereInAABB / WhereRay / WhereFrustum) — call .Execute().");
        }
        if (_orderBy.HasValue)
        {
            throw new InvalidOperationException("foreach does not apply OrderBy / Skip / Take — call .ExecuteOrdered().");
        }

        var entities = new List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, EntityLocations Locations)>();
        if (!MaskIsEmpty)
        {
            CollectMatchingFull(entities);
        }
        return new EcsQueryEnumerator(_tx, entities, _whereFilter, DisabledNames);
    }

    /// <summary>
    /// Core broad scan: iterate matching archetypes, then all entities in each LinearHash.
    /// Dispatches to the generic core once — the JIT fully specializes per TMask type.
    /// Also includes pending spawns for read-your-own-writes support.
    /// </summary>
    private void CollectMatching(Action<EntityId, ushort> onMatch, bool stopOnFirst = false)
    {
        if (_useLargeMask)
        {
            CollectMatchingCore(_maskLarge, onMatch, stopOnFirst);
            CollectPendingSpawns(_maskLarge, onMatch, stopOnFirst);
        }
        else
        {
            CollectMatchingCore(_mask256, onMatch, stopOnFirst);
            CollectPendingSpawns(_mask256, onMatch, stopOnFirst);
        }
    }

    /// <summary>
    /// Aggregation broad scan for <see cref="Count"/>: counts matching entities via the map's copy-free <c>CountEntries</c>, then folds in matching pending
    /// spawns (read-your-own-writes). The EntityMap walk allocates nothing and uses no delegate; the pending-spawn closure is only created when the current
    /// transaction actually has uncommitted spawns (empty for read-only queries — the common case).
    /// </summary>
    private int CountMatching()
    {
        var total = _useLargeMask ? CountMatchingCore(_maskLarge) : CountMatchingCore(_mask256);

        var pending = _tx.PendingSpawns;
        if (pending != null && pending.Count > 0)
        {
            var pendingCount = 0;
            if (_useLargeMask)
            {
                CollectPendingSpawns(_maskLarge, (_, _) => pendingCount++, false);
            }
            else
            {
                CollectPendingSpawns(_mask256, (_, _) => pendingCount++, false);
            }
            total += pendingCount;
        }

        return total;
    }

    /// <summary>
    /// Existence broad scan for <see cref="Any"/>: short-circuits via the map's copy-free <c>AnyEntry</c>, then checks matching pending spawns if the EntityMap
    /// had no match.
    /// </summary>
    private bool AnyMatching()
    {
        if (_useLargeMask ? AnyMatchingCore(_maskLarge) : AnyMatchingCore(_mask256))
        {
            return true;
        }

        var pending = _tx.PendingSpawns;
        if (pending != null && pending.Count > 0)
        {
            var found = false;
            if (_useLargeMask)
            {
                CollectPendingSpawns(_maskLarge, (_, _) => found = true, true);
            }
            else
            {
                CollectPendingSpawns(_mask256, (_, _) => found = true, true);
            }
            return found;
        }

        return false;
    }

    /// <summary>Collect full entity data for foreach enumeration. Dispatches to generic core.</summary>
    private void CollectMatchingFull(List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results)
    {
        if (_useLargeMask)
        {
            CollectMatchingFullCore(_maskLarge, results);
            CollectPendingSpawnsFull(_maskLarge, results);
        }
        else
        {
            CollectMatchingFullCore(_mask256, results);
            CollectPendingSpawnsFull(_mask256, results);
        }
    }

    /// <summary>
    /// Scan the transaction's pending spawns for entities matching the query (read-your-own-writes).
    /// Pending spawns are not yet in the EntityMap — without this, Query().Execute() would miss them.
    /// </summary>
    private void CollectPendingSpawns<TMask>(TMask mask, Action<EntityId, ushort> onMatch, bool stopOnFirst) where TMask : struct, IArchetypeMask<TMask>
    {
        var pending = _tx.PendingSpawns;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var destroys = _tx.PendingDestroys;
        var enableDisable = _tx.PendingEnableDisable;
        var hasT2 = HasT2;

        for (var i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];

            // Skip if pending destroy
            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }

            // T1: archetype mask
            if (!mask.Test(_tx.DBE.GetMetaByRouting(entry.Id.ArchetypeId).ArchetypeId))
            {
                continue;
            }

            // Resolve EnabledBits (may have been overridden by Enable/Disable in same tx)
            var enabledBits = entry.EnabledBits;
            if (enableDisable != null && enableDisable.TryGetValue(entry.Id, out var overrideBits))
            {
                enabledBits = overrideBits;
            }

            // T2: check enabled/disabled constraints
            if (hasT2)
            {
                var meta = _tx.DBE.GetMetaByRouting(entry.Id.ArchetypeId);
                if (meta == null || !ResolveT2Masks(meta, out var reqEnabled, out var reqDisabled))
                {
                    continue;
                }
                if ((enabledBits & reqEnabled) != reqEnabled)
                {
                    continue;
                }
                if ((enabledBits & reqDisabled) != 0)
                {
                    continue;
                }
            }

            onMatch(entry.Id, enabledBits);

            if (stopOnFirst)
            {
                return;
            }
        }
    }

    /// <summary>Pending spawn collection for foreach enumeration (includes EntityLocations).</summary>
    private void CollectPendingSpawnsFull<TMask>(TMask mask, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results) where TMask : struct, IArchetypeMask<TMask>
    {
        var pending = _tx.PendingSpawns;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var destroys = _tx.PendingDestroys;
        var enableDisable = _tx.PendingEnableDisable;
        var hasT2 = HasT2;

        for (var i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];

            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }

            if (!mask.Test(_tx.DBE.GetMetaByRouting(entry.Id.ArchetypeId).ArchetypeId))
            {
                continue;
            }

            var enabledBits = entry.EnabledBits;
            if (enableDisable != null && enableDisable.TryGetValue(entry.Id, out var overrideBits))
            {
                enabledBits = overrideBits;
            }

            var meta = _tx.DBE.GetMetaByRouting(entry.Id.ArchetypeId);
            if (meta == null)
            {
                continue;
            }

            if (hasT2)
            {
                if (!ResolveT2Masks(meta, out var reqEnabled, out var reqDisabled))
                {
                    continue;
                }
                if ((enabledBits & reqEnabled) != reqEnabled)
                {
                    continue;
                }
                if ((enabledBits & reqDisabled) != 0)
                {
                    continue;
                }
            }

            // Copy locations from SpawnEntry into EntityLocations. Since #839 a slot's location means one of two things while the entity is unpublished: a
            // real content chunk id for Versioned (that chunk is the first revision's payload) or a spawn-arena handle for SingleVersion and Transient. The
            // EntityRef built from these is an own-spawn ref, and its read path disambiguates on that flag — see EntityAccessor.ResolveSpawnAwarePayload.
            var locs = new EntityLocations();
            var slotTables = _tx.SlotTablesFor(meta);
            for (var s = 0; s < meta.ComponentCount; s++)
            {
                locs.Values[s] = Transaction.SpawnSlotLocation(in entry, slotTables[s], s);
            }

            results.Add((entry.Id, meta, enabledBits, locs));
        }
    }

    /// <summary>
    /// JIT-specialized broad scan. TMask.Test() is inlined — zero virtual dispatch, zero branch per entity.
    /// Two native code paths emitted: one for ArchetypeMask256 (fixed ulong[4]), one for ArchetypeMaskLarge (ulong[]).
    /// </summary>
    private void CollectMatchingCore<TMask>(TMask mask, Action<EntityId, ushort> onMatch, bool stopOnFirst) where TMask : struct, IArchetypeMask<TMask>
    {
        var txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        var hasT2 = HasT2;

        for (var archBit = 0; archBit <= mask.MaxId; archBit++)
        {
            if (!mask.Test((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null || engineState.SlotToComponentTable == null)
            {
                continue;
            }

            ushort reqEnabled = 0, reqDisabled = 0;
            if (hasT2 && !ResolveT2Masks(meta, out reqEnabled, out reqDisabled))
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateScanAccessor();
            var action = new BroadScanAction
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                Cards = dbe.CommitCards,
                Map = engineState.EntityMap,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                OnMatch = onMatch,
                StopOnFirst = stopOnFirst,
                Found = false,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            try
            {
                engineState.EntityMap.ForEachEntry(ref accessor, ref action, _tx.BeginScanYield());
            }
            finally
            {
                accessor.Dispose();
            }

            if (stopOnFirst && action.Found)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Aggregation counterpart of <see cref="CollectMatchingCore"/>: iterate matching archetypes and count visible/enabled-filtered entities via the map's
    /// optimistic, copy-free <c>CountEntries</c>. The JIT fully specializes per TMask type. EntityMap only (pending spawns handled by the caller).
    /// </summary>
    private int CountMatchingCore<TMask>(TMask mask) where TMask : struct, IArchetypeMask<TMask>
    {
        var txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        var hasT2 = HasT2;
        var total = 0;

        for (var archBit = 0; archBit <= mask.MaxId; archBit++)
        {
            if (!mask.Test((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null || engineState.SlotToComponentTable == null)
            {
                continue;
            }

            ushort reqEnabled = 0, reqDisabled = 0;
            if (hasT2 && !ResolveT2Masks(meta, out reqEnabled, out reqDisabled))
            {
                continue;
            }

            if (TryCountViaOccupancy(engineState, hasT2, txTsn, out var occupancyCount))
            {
                total += occupancyCount;
                continue;
            }

            QueryPathProbe.MapProbeCounts++;

            var accessor = engineState.EntityMap.Segment.CreateScanAccessor();
            var pred = new BroadScanPredicate
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                Cards = dbe.CommitCards,
                Map = engineState.EntityMap,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            try
            {
                total += engineState.EntityMap.CountEntries(ref accessor, ref pred, _tx.BeginScanYield());
            }
            finally
            {
                accessor.Dispose();
            }
        }

        return total;
    }

    /// <summary>
    /// Count one archetype's snapshot-visible entities by summing <c>PopCount</c> over its clusters' occupancy words. Returns false — having counted nothing —
    /// when the archetype does not qualify, leaving the caller on the per-entity EntityMap probe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map probe evaluates <see cref="BroadScanPredicate"/> per entity over a hash map: ~8 ns each with random access, so counting 10 000 entities costs
    /// ~88 µs. Since #629 every archetype is cluster-backed, and a cluster already carries the answer in its header — one 64-bit occupancy word per up-to-64
    /// entities. For 10 000 entities that is ~157 popcounts instead of 10 000 probes.
    /// </para>
    /// <para>
    /// What makes the substitution legal is exactly the four conditions the predicate tests, and each is answered here rather than assumed:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Born after the snapshot / died before it.</b> The occupancy word is CURRENT — it knows nothing about the reader's snapshot.
    /// <see cref="ArchetypeClusterState.IsClusterFullyVisibleAt(int, long)"/> is the same per-cluster summary the SoA scan uses (H1): it is true only when
    /// every entity in the cluster was born at or before <paramref name="txTsn"/> and every death recorded there is already visible to this reader. It is
    /// conservative by construction — a cluster without words or an unestablished maximum answers false — so a bail is always safe and only ever costs
    /// performance.</item>
    /// <item><b>Enabled/disabled (T2) predicates.</b> Occupancy is liveness, not enabled bits, so any T2 requirement disqualifies the archetype outright.</item>
    /// <item><b>Entities pending destroy in this transaction.</b> Their occupancy bit is still set, so a non-empty set disqualifies the archetype.</item>
    /// <item><b>Pending spawns.</b> A spawn still pending in this transaction owns no slot; one placed by a committing transaction holds a reserved slot,
    /// which bails to the map probe (VIS-08).</item>
    /// </list>
    /// <para>
    /// All-or-nothing per archetype, on purpose. A cluster that fails the summary would have to be counted through the EntityMap by entity id, and a point
    /// lookup there costs ~80 ns against the ~8 ns of the sequential scan the fallback already does — so a hybrid would be slower than the path it replaces on
    /// exactly the clusters it was meant to rescue. The cost of bailing is the cluster headers touched before the first failure, which the fallback scan reads
    /// anyway. Since #722 the died side is a watermark rather than a sticky flag, so a churned archetype returns to this path as soon as the reader's snapshot
    /// passes its last death — the ceiling that used to settle a long-lived archetype permanently onto the probe is gone.
    /// </para>
    /// </remarks>
    private bool TryCountViaOccupancy(ArchetypeEngineState engineState, bool hasT2, long txTsn, out int count)
    {
        count = 0;

        if (QueryPathProbe.ForcedCount == ClusterCountPath.MapProbe)
        {
            return false;
        }

        if (hasT2 || _tx.PendingDestroys is { Count: > 0 })
        {
            return false;
        }

        var clusterState = engineState.ClusterState;
        if (clusterState?.ClusterSegment == null || clusterState.ActiveClusterIds == null)
        {
            return false;
        }

        // The whole archetype, through a scan accessor's pins only: a long walk yields its epoch every 64 clusters (#1364), and a back-pressure wait in it
        // may move it (#1230). Without either, a broad count that faulted waited under the pin it began with, the oldest in the engine (a 64 MiB market).
        // Only a long one: a count of a few hundred clusters is microseconds, and arming both cost it a measured 7 %.
        var accessor = clusterState.ClusterSegment.CreateScanAccessor();
        var longWalk = false;
        try
        {
            // CLUSTERWALK-02: the (count, array) pair goes through the one reader, never read directly here. This used to be a plain load of both, which
            // faults on a plain interleaving — old 16-length array, concurrent resize, count read as 17, index 16.
            var activeIds = clusterState.ReadActiveClusterList(out var activeCount);
            if (activeIds == null)
            {
                return false;
            }

            longWalk = activeCount > EntityAccessor.LongWalkClusters;
            if (longWalk)
            {
                PagedMMF.RefreshableWaitDepth++;
            }

            // Read once: a cluster whose block is missing from it reads as unestablished and bails, which is conservative (SlotWordTable).
            var slotWords = clusterState.SlotWordTable;
            var total = 0;
            var scanYield = longWalk ? _tx.BeginScanYield() : null;
            for (var c = 0; c < activeCount; c++)
            {
                if ((c & 63) == 63 && longWalk)
                {
                    scanYield();
                }

                var clusterChunkId = activeIds[c];

                // Occupancy BEFORE the summary, and with acquire ordering: NoteClusterBorn stores the maximum plainly, on the premise that the reader reaches
                // it only after an acquire-ordered read of this word. Reading them the other way round could pair a fresh maximum with a stale occupancy word.
                // The pending-death count before the occupancy word, the claim word after it — see ScanClusterSoa; both in one lookup.
                var clusterBase = accessor.GetChunkAddress(clusterChunkId);
                ref var words = ref ArchetypeClusterState.SlotWords(slotWords, clusterChunkId);
                var pendingDeaths = ArchetypeClusterState.PendingDeathsOf(ref words);
                var occupancy = Volatile.Read(ref *(ulong*)clusterBase);

                // A slot claimed and not occupied — a spawn placed in PREPARE (VIS-08), a destroy hidden until the cleanup (VIS-09) — is decided by its
                // owner's stamp or its died, which a popcount cannot tell: the map probe decides it.
                if (pendingDeaths != 0 || !ArchetypeClusterState.WatermarksVisibleAt(ref words, txTsn)
                    || (ArchetypeClusterState.ClaimOf(ref words) & ~occupancy) != 0)
                {
                    return false;
                }

                total += System.Numerics.BitOperations.PopCount(occupancy);
            }

            count = total;
            QueryPathProbe.OccupancyCounts++;
            return true;
        }
        finally
        {
            if (longWalk)
            {
                PagedMMF.RefreshableWaitDepth--;
            }

            accessor.Dispose();
        }
    }

    /// <summary>
    /// Existence counterpart of <see cref="CollectMatchingCore"/>: short-circuits on the first matching entity in any matching archetype via the map's
    /// optimistic, copy-free <c>AnyEntry</c>. The JIT fully specializes per TMask type. EntityMap only (pending spawns handled by the caller).
    /// </summary>
    private bool AnyMatchingCore<TMask>(TMask mask) where TMask : struct, IArchetypeMask<TMask>
    {
        var txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        var hasT2 = HasT2;

        for (var archBit = 0; archBit <= mask.MaxId; archBit++)
        {
            if (!mask.Test((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null || engineState.SlotToComponentTable == null)
            {
                continue;
            }

            ushort reqEnabled = 0, reqDisabled = 0;
            if (hasT2 && !ResolveT2Masks(meta, out reqEnabled, out reqDisabled))
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateScanAccessor();
            var pred = new BroadScanPredicate
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                Cards = dbe.CommitCards,
                Map = engineState.EntityMap,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            bool found;
            try
            {
                found = engineState.EntityMap.AnyEntry(ref accessor, ref pred, _tx.BeginScanYield());
            }
            finally
            {
                accessor.Dispose();
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>JIT-specialized variant for full entity data collection (foreach enumeration).</summary>
    private void CollectMatchingFullCore<TMask>(TMask mask, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results) where TMask : struct, IArchetypeMask<TMask>
    {
        var txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        var hasT2 = HasT2;

        for (var archBit = 0; archBit <= mask.MaxId; archBit++)
        {
            if (!mask.Test((ushort)archBit))
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archBit);
            if (meta == null)
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null || engineState.SlotToComponentTable == null)
            {
                continue;
            }

            ushort reqEnabled = 0, reqDisabled = 0;
            if (hasT2 && !ResolveT2Masks(meta, out reqEnabled, out reqDisabled))
            {
                continue;
            }

            var accessor = engineState.EntityMap.Segment.CreateScanAccessor();
            var action = new BroadScanCollectAction
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                Cards = dbe.CommitCards,
                Map = engineState.EntityMap,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                Results = results,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            try
            {
                engineState.EntityMap.ForEachEntry(ref accessor, ref action, _tx.BeginScanYield());
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Broad scan action structs (JIT-specialized callbacks for ForEachEntry)
    // ═══════════════════════════════════════════════════════════════════════

    private struct BroadScanAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ArchetypeMetadata Meta;
        public ushort RoutingId;
        public long TxTsn;
        public EnabledBitsOverrides EnabledBitsOverrides;
        public bool HasT2;
        public CommitCardTable Cards;
        public RawValuePagedHashMap<long, PersistentStore> Map;
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public Action<EntityId, ushort> OnMatch;
        public bool StopOnFirst;
        public bool Found;
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            // Visibility check
            if (!EntityVisibility.IsVisibleInScan(value, TxTsn, Cards, Map, key))
            {
                return true; // Not yet born, or dead — skip, continue (a pending born through its owner's card, VIS-08)
            }

            var entityId = new EntityId(key, RoutingId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            var bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn, Cards);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out var pendingBits))
            {
                bits = pendingBits;
            }

            // T2 check
            if (HasT2)
            {
                if ((bits & RequiredEnabled) != RequiredEnabled)
                {
                    return true;
                }
                if ((bits & RequiredDisabled) != 0)
                {
                    return true;
                }
            }

            OnMatch(entityId, bits);

            if (StopOnFirst)
            {
                Found = true;
                return false; // Stop iteration
            }
            return true;
        }
    }

    /// <summary>
    /// Side-effect-free match test for the optimistic, copy-free aggregation paths (<see cref="CountMatchingCore"/> / <see cref="AnyMatchingCore"/>). Mirrors
    /// the filter half of <see cref="BroadScanAction"/> (MVCC visibility + pending-destroy + resolved EnabledBits + T2) with no callback, so it is pure and safe
    /// to re-evaluate when an optimistic bucket read is rejected. Reads only the EntityRecord header and lookup dictionaries — never dereferences past the value
    /// pointer — so a torn read produces a wrong-but-harmless bool that the map's version validation discards.
    /// </summary>
    private struct BroadScanPredicate : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public ArchetypeMetadata Meta;
        public ushort RoutingId;
        public long TxTsn;
        public EnabledBitsOverrides EnabledBitsOverrides;
        public bool HasT2;
        public CommitCardTable Cards;
        public RawValuePagedHashMap<long, PersistentStore> Map;
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

        public bool Matches(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            // Visibility check (MVCC)
            if (!EntityVisibility.IsVisibleInScan(value, TxTsn, Cards, Map, key))
            {
                return false; // Not yet born, or dead (a pending born through its owner's card, VIS-08)
            }

            var entityId = new EntityId(key, RoutingId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return false;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            var bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn, Cards);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out var pendingBits))
            {
                bits = pendingBits;
            }

            // T2 check
            if (HasT2)
            {
                if ((bits & RequiredEnabled) != RequiredEnabled)
                {
                    return false;
                }
                if ((bits & RequiredDisabled) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private struct BroadScanCollectAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ArchetypeMetadata Meta;
        public ushort RoutingId;
        public long TxTsn;
        public EnabledBitsOverrides EnabledBitsOverrides;
        public bool HasT2;
        public CommitCardTable Cards;
        public RawValuePagedHashMap<long, PersistentStore> Map;
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> Results;
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            if (!EntityVisibility.IsVisibleInScan(value, TxTsn, Cards, Map, key))
            {
                return true;
            }

            var entityId = new EntityId(key, RoutingId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            var bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn, Cards);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out var pendingBits))
            {
                bits = pendingBits;
            }

            if (HasT2)
            {
                if ((bits & RequiredEnabled) != RequiredEnabled)
                {
                    return true;
                }
                if ((bits & RequiredDisabled) != 0)
                {
                    return true;
                }
            }

            // Copy component locations inline — no heap allocation.
            // For cluster archetypes, locations are meaningless (record has ClusterChunkId+SlotIndex, not per-component ChunkIds).
            // Store a zeroed EntityLocations — the enumerator will resolve via Transaction.Open for cluster archetypes.
            var locs = new EntityLocations();
            if (!Meta.IsClusterEligible)
            {
                EntityRecordAccessor.CopyLocationsTo(value, ref locs, Meta.ComponentCount);
            }

            Results.Add((entityId, Meta, bits, locs));
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumerator (iterates pre-collected results)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Iterates pre-collected query results, yielding read-only <see cref="EntityRef"/>s; their component reads return copies.
    /// To write, open the entity with <see cref="EntityAccessor.OpenMut"/> or <see cref="EntityAccessor.TryOpenMut"/>.
    /// </summary>
    [PublicAPI]
    public ref struct EcsQueryEnumerator
    {
        private readonly Transaction _tx;
        private readonly List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, EntityLocations Locations)> _entities;
        private readonly EntityFilter _whereFilter;
        private readonly DisabledTypeIds _readDisabled;
        private int _index;
        private EntityRef _current;

        internal EcsQueryEnumerator(Transaction tx, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> entities, EntityFilter whereFilter,
            DisabledTypeIds readDisabled)
        {
            _tx = tx;
            _entities = entities;
            _whereFilter = whereFilter;
            _readDisabled = readDisabled;
            _index = -1;
        }

        /// <summary>The <see cref="EntityRef"/> resolved at the current position.</summary>
        public EntityRef Current => _current;

        /// <summary>Advances to the next matching entity, applying the WHERE post-filter; returns <see langword="false"/> at the end.</summary>
        public bool MoveNext()
        {
            while (true)
            {
                _index++;
                if (_index >= _entities.Count)
                {
                    return false;
                }

                var (id, meta, enabledBits, locations) = _entities[_index];

                // T3 post-filter: evaluate WHERE via Transaction.Open
                if (_whereFilter != null && !_whereFilter(id, _tx, _readDisabled))
                {
                    continue;
                }

                if (meta.IsClusterEligible)
                {
                    // Cluster archetype: resolve via Transaction.Open which handles cluster path correctly
                    _current = _tx.Open(id);
                }
                else
                {
                    var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];

                    // The captured EntityLocations are raw EntityMap record values. For SV/Transient slots the raw value
                    // IS the content chunk (correct). For a Versioned slot it is the revision-chain HEAD
                    // (compRevFirstChunkId) and must be walked to the snapshot-visible content chunk before EntityRef.Read
                    // reads _locations[slot] directly on this non-cluster path — otherwise the read returns a stale,
                    // pre-mutation value (#504). VersionedSlotMask is zeroed for non-cluster archetypes, so detect via the
                    // component table StorageMode. When any Versioned slot is present, delegate to Transaction.Open, whose
                    // non-cluster path performs that per-slot MVCC revision-chain walk; pure SV/Transient non-cluster
                    // archetypes keep the zero-lookup captured-locations fast path.
                    if (ArchetypeHasVersionedSlot(engineState, meta.ComponentCount))
                    {
                        _current = _tx.Open(id);
                    }
                    else
                    {
                        _current = new EntityRef(id, meta, engineState, _tx, enabledBits);
                        _current.CopyLocationsFrom(in locations, meta.ComponentCount);
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// True if any slot of the archetype uses <see cref="StorageMode.Versioned"/>. Routes the non-cluster enumerator
        /// path: Versioned slots need an MVCC revision-chain walk (performed by <see cref="EntityAccessor.Open(EntityId)"/>) that the raw
        /// captured locations skip. <c>ArchetypeMetadata.VersionedSlotMask</c> is zeroed for non-cluster archetypes, so this
        /// scans the component tables directly rather than testing the mask.
        /// </summary>
        private static bool ArchetypeHasVersionedSlot(ArchetypeEngineState engineState, int componentCount)
        {
            var tables = engineState.SlotToComponentTable;
            for (var slot = 0; slot < componentCount; slot++)
            {
                if (tables[slot].StorageMode == StorageMode.Versioned)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>No-op; the enumerator holds no resources requiring release.</summary>
        public void Dispose() { }
    }
}
#pragma warning restore TYPHON005
