using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using JetBrains.Annotations;
using Typhon.Profiler;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>Type of spatial predicate attached to an EcsQuery.</summary>
internal enum SpatialQueryType : byte
{
    None = 0,
    AABB = 1,
    Radius = 2,
    Ray = 3,
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
    private Func<EntityId, Transaction, bool> _whereFilter;
    private Func<EntityId, Transaction, bool> _pendingSpawnFieldFilter;

    // Spatial query predicate (at most one per query)
    private ComponentTable _spatialTable;
    private SpatialQueryType _spatialQueryType;
    // Inline query parameters: meaning depends on _spatialQueryType
    // AABB: [min0..max0..] in [0]..[5]. Radius: center in [0]..[2], radius in [3]. Ray: origin in [0]..[2], dir in [3]..[5], maxDist in [6].
    private fixed double _spatialParams[7];

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

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!MaskTest(meta.ArchetypeId))
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
    /// <see cref="EntityRef.TryRead{T}"/>. Multiple Where calls chain as AND (each must pass).
    /// </summary>
    /// <remarks>Targeted scan (index-first) is not yet available — always uses broad scan.</remarks>
    public EcsQuery<TArchetype> Where<T>(Func<T, bool> predicate) where T : unmanaged
    {
        var prevFilter = _whereFilter;
        _whereFilter = prevFilter == null ? (id, tx) =>
            {
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && predicate(value);
            } : (id, tx) =>
            {
                if (!prevFilter(id, tx))
                {
                    return false;
                }
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && predicate(value);
            };
        return this;
    }

    /// <summary>
    /// Filter entities by an indexed-field predicate, enabling incremental view refresh via <see cref="ViewDeltaRingBuffer"/>.
    /// The expression is parsed into <see cref="FieldEvaluator"/> for boundary crossing detection. Requires indexed fields.
    /// </summary>
    public EcsQuery<TArchetype> WhereField<T>(Expression<Func<T, bool>> predicate) where T : unmanaged
    {
        var ct = _tx.DBE.GetComponentTable<T>();
        if (ct == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T).Name} is not registered.");
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
        var prevPendingFilter = _pendingSpawnFieldFilter;
        _pendingSpawnFieldFilter = prevPendingFilter == null ? (id, tx) =>
            {
                compiledPredicate ??= ((Expression<Func<T, bool>>)predicateExpr).Compile();
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && compiledPredicate(value);
            } : (id, tx) =>
            {
                if (!prevPendingFilter(id, tx))
                {
                    return false;
                }
                compiledPredicate ??= ((Expression<Func<T, bool>>)predicateExpr).Compile();
                var entity = tx.Open(id);
                return entity.TryRead<T>(out var value) && compiledPredicate(value);
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

    /// <summary>Guard: at most one spatial predicate per query — a second call would silently overwrite the first.</summary>
    private readonly void ThrowIfSpatialAlreadySet()
    {
        if (_spatialQueryType != SpatialQueryType.None)
        {
            throw new InvalidOperationException(
                "Only one spatial predicate is allowed per query (WhereNearby / WhereInAABB / WhereRay). Run separate queries for multiple regions.");
        }
    }

    /// <summary>Filter by radius (sphere) around a center point. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereNearby<T>(double centerX, double centerY, double centerZ, double radius) where T : unmanaged
    {
        ThrowIfSpatialAlreadySet();
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        CheckConfig.Require(CheckConfig.Enabled, _spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
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
        CheckConfig.Require(CheckConfig.Enabled, _spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
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
        CheckConfig.Require(CheckConfig.Enabled, _spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
        _spatialQueryType = SpatialQueryType.Ray;
        _spatialParams[0] = originX; _spatialParams[1] = originY; _spatialParams[2] = originZ;
        _spatialParams[3] = dirX; _spatialParams[4] = dirY; _spatialParams[5] = dirZ; _spatialParams[6] = maxDist;
        // Phase 7: ECS:Query:Spatial:Attach instant — origin + endpoint XY projection.
        TyphonEvent.EmitEcsQuerySpatialAttach((byte)SpatialQueryType.Ray, (float)originX, (float)originY, (float)(originX + dirX * maxDist), (float)(originY + dirY * maxDist));
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

    /// <summary>Public routing-id variant of <see cref="MaskTestPublic"/> for callers that hold an EntityId's routing id (e.g. <c>EcsView</c>).</summary>
    internal readonly bool MaskTestPublicByRouting(ushort routingId) => MaskTestByRouting(routingId);

    // ═══════════════════════════════════════════════════════════════════════
    // OrderBy / Skip / Take
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Order results by an indexed field. Requires <see cref="WhereField{T}"/> to identify the component.</summary>
    public EcsQuery<TArchetype> OrderByField<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        _orderBy = new OrderByField(ResolveOrderByFieldIndex(keySelector));
        return this;
    }

    /// <summary>Order results descending by an indexed field.</summary>
    public EcsQuery<TArchetype> OrderByFieldDescending<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        _orderBy = new OrderByField(ResolveOrderByFieldIndex(keySelector), descending: true);
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

    private int ResolveOrderByFieldIndex<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
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
        return QueryResolverHelper.FindFieldIndex(_whereComponentTable.Definition, field);
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
                throw new InvalidOperationException("A view cannot combine WhereField with a spatial predicate (WhereNearby / WhereInAABB / WhereRay).");
            }
            return ToIncrementalView(bufferCapacity, callerFile, callerLine, callerMethod);
        }

        // Pull mode: no field evaluators (spatial / Where(lambda) are honoured by Execute() inside ToPullView).
        return ToPullView(bufferCapacity, callerFile, callerLine, callerMethod);
    }

    private EcsView<TArchetype> ToPullView(int bufferCapacity, string callerFile, int callerLine, string callerMethod)
    {
        var initialSet = Execute();
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
        var firstTable = engineState.SlotToComponentTable[0];

        var view = new EcsView<TArchetype>(this, firstTable.DBE.MemoryAllocator, firstTable, bufferCapacity, _tx.TSN, callerFile, callerLine, callerMethod);

        // Pre-size the entity-id set to the exact final count: initialSet is a HashSet (all keys distinct) and the View's map is fresh, so every key is a
        // genuine add. This collapses the ~log2(count/64) incremental resizes of the populate loop into a single right-sized POH allocation.
        view.EntityIdsInternal.EnsureCapacity(initialSet.Count);

        // Populate initial entity set
        foreach (var id in initialSet)
        {
            view.AddEntityDirect((long)id.RawValue);
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
            queryInstanceKind: 1, queryInstanceLocalId: (uint)EcsQueryId,
            definitionSourceFile: SourceFile, definitionSourceLine: SourceLine, definitionSourceMethod: SourceMethod,
            executionSourceFile: callerFile, executionSourceLine: callerLine, executionSourceMethod: callerMethod);

        var view = new EcsView<TArchetype>(this, evaluators, ct, _whereFieldReader, plan, bufferCapacity, _tx.TSN, callerFile, callerLine, callerMethod);

        // Register with ViewRegistry for delta notifications
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        // Initial population. Must go through the cross-archetype scan, not IFieldReader.ExecuteFullScan directly: a cluster-backed archetype keeps its
        // indexes on the archetype, so the ComponentTable tree this plan targets is empty and the view would populate to nothing (#663).
        ExecuteFullScanAcrossArchetypes(plan, plan.OrderedEvaluators, ct, view.EntityIdsInternal);

        // Process any deltas that arrived during population
        view.RefreshFromScheduler(_tx);
        view.ClearDelta();

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
                queryInstanceKind: 1, queryInstanceLocalId: (uint)EcsQueryId,
                definitionSourceFile: SourceFile, definitionSourceLine: SourceLine, definitionSourceMethod: SourceMethod,
                executionSourceFile: callerFile, executionSourceLine: callerLine, executionSourceMethod: callerMethod);
        }

        var view = new EcsView<TArchetype>(this, branchEvaluators, plans, ct, _whereFieldReader, bufferCapacity, _tx.TSN, callerFile, callerLine, callerMethod);
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        view.PopulateInitialOr(_tx);
        view.RefreshFromScheduler(_tx);
        view.ClearDelta();

        return view;
    }

    /// <summary>Rebind this query to a different transaction (different TSN → different visibility).</summary>
    internal void UpdateTransaction(Transaction tx) => _tx = tx;

    /// <summary>Execute the query and collect matching entity IDs into a HashSet.</summary>
    public HashSet<EntityId> Execute(
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
    {
        // callerFile/Line/Method captured at user call site; consumed by trace emission in P2 (issue #335).
        _ = callerFile; _ = callerLine; _ = callerMethod;
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
            if (filter != null)
            {
                result.RemoveWhere(id => !filter(id, tx));
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
            queryInstanceKind: 1, queryInstanceLocalId: (uint)EcsQueryId,
            definitionSourceFile: SourceFile, definitionSourceLine: SourceLine, definitionSourceMethod: SourceMethod,
            executionSourceFile: callerFile, executionSourceLine: callerLine, executionSourceMethod: callerMethod);

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
        var dbe = _tx.DBE;
        // Use rented array instead of List + ToArray to avoid redundant allocations.
        // Typical K is 1-3 archetypes; rent 8 to avoid resize in common cases.
        var streams = System.Buffers.ArrayPool<ArchetypeSortedStream>.Shared.Rent(8);
        var streamCount = 0;

        // Early termination: each per-archetype stream only needs skip+take entries at most.
        // The B+Tree enumerator yields in sort order, so stopping early is correct.
        var maxPerStream = _take > 0 ? _skip + _take : 0;

        // The plan's PrimaryFieldIndex may be -1 when the shared B+Tree has 0 entries (cluster archetypes store entries in per-archetype B+Trees,
        // not the shared one). In that case, use the OrderBy field index directly and full type range for scan bounds.
        Debug.Assert(_orderBy.HasValue, "ExecuteOrderedClustered requires OrderBy to be set");
        var orderByFieldIdx = _orderBy.Value.FieldIndex;
        var descending = plan.Descending;
        var primaryFieldIdx = plan.PrimaryFieldIndex >= 0 ? plan.PrimaryFieldIndex : orderByFieldIdx;

        // If there are evaluators on fields OTHER than the scan field, the B+Tree scan won't filter them.
        // Fall back to ExecuteTargeted (which verifies all evaluators) + sort for correctness.
        for (var e = 0; e < evaluators.Length; e++)
        {
            if (evaluators[e].FieldIndex != primaryFieldIdx && evaluators[e].CompareOp != CompareOp.NotEqual)
            {
                return ExecuteOrderedViaSortFallback(evaluators, plan);
            }
        }

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
                    // Plan fell back to PK scan — compute bounds from evaluators for this field.
                    keyType = KeyType.Int;
                    scanMin = long.MinValue;
                    scanMax = long.MaxValue;
                    for (var e = 0; e < evaluators.Length; e++)
                    {
                        if (evaluators[e].FieldIndex == fieldIdx)
                        {
                            keyType = evaluators[e].KeyType;
                            scanMin = GetTypeMinAsLong(keyType);
                            scanMax = GetTypeMaxAsLong(keyType);
                            break;
                        }
                    }

                    // Intersect bounds with all evaluators on this field (e.g., Score >= 50 narrows scanMin)
                    IntersectEvaluatorBounds(evaluators, fieldIdx, keyType, ref scanMin, ref scanMax);
                }

                // Grow rented array if needed (rare — most queries match 1-3 archetypes)
                if (streamCount >= streams.Length)
                {
                    var newStreams = System.Buffers.ArrayPool<ArchetypeSortedStream>.Shared.Rent(streams.Length * 2);
                    Array.Copy(streams, newStreams, streamCount);
                    System.Buffers.ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
                    streams = newStreams;
                }

                streams[streamCount++] = ArchetypeSortedStream.Create(field.Index, keyType, scanMin, scanMax, field.AllowMultiple, descending,
                    clusterState, clusterState.Layout, maxPerStream);
            }

            if (streamCount == 0)
            {
                System.Buffers.ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
                return [];
            }

            // KWayMergeState takes ownership of the streams array (ownsArray: true → returns to pool on Dispose)
            var merge = KWayMergeState.Create(streams, streamCount, descending, true);
            try
            {
                return CollectMergedResults(ref merge, evaluators);
            }
            finally
            {
                merge.Dispose();
            }
        }
        catch
        {
            // Dispose streams on failure path
            for (var i = 0; i < streamCount; i++)
            {
                streams[i].Dispose();
            }
            System.Buffers.ArrayPool<ArchetypeSortedStream>.Shared.Return(streams, true);
            throw;
        }
    }

    /// <summary>Collect results from a K-way merge, applying Skip/Take.</summary>
    private List<EntityId> CollectMergedResults(ref KWayMergeState merge, FieldEvaluator[] evaluators)
    {
        var result = new List<EntityId>(_take > 0 ? _take : 64);
        var skipped = 0;
        var taken = 0;
        var take = _take > 0 ? _take : int.MaxValue;

        while (merge.MoveNext(out var entityPK))
        {
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

        // Build entity→sortKey mapping by scanning per-archetype B+Trees.
        // Each B+Tree entry is (key, ClusterLocation) — we reverse-resolve ClusterLocation → EntityPK
        // to match against our result set.
        var entityKeyMap = new Dictionary<long, long>(unordered.Count); // entityPK → orderedKey
        Debug.Assert(_orderBy != null, nameof(_orderBy) + " != null");
        var orderByFieldIdx = _orderBy.Value.FieldIndex;
        var dbe = _tx.DBE;

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

            // Persistent home only — see the note in ExecuteOrderedClustered. A Transient home leaves this archetype's entities out of entityKeyMap, so they
            // sort by EntityKey rather than by the ordered field (#655 follow-up, tracked in #665).
            var ixSlotIdx = FindClusterIndexSlot(clusterState, meta, out var transientHome);
            if (ixSlotIdx < 0 || transientHome || orderByFieldIdx < 0 || orderByFieldIdx >= clusterState.IndexSlots[ixSlotIdx].Fields.Length)
            {
                continue;
            }

            ref var field = ref clusterState.IndexSlots[ixSlotIdx].Fields[orderByFieldIdx];

            // Scan the full B+Tree to build PK→key mapping for entities in our result set
            var stream = ArchetypeSortedStream.Create(field.Index, plan.PrimaryKeyType, GetTypeMinAsLong(evaluators[0].KeyType), 
                GetTypeMaxAsLong(evaluators[0].KeyType),
                field.AllowMultiple, false, clusterState, clusterState.Layout);
            try
            {
                while (stream.HasCurrent)
                {
                    entityKeyMap.TryAdd(stream.CurrentEntityPK, stream.CurrentKey);
                    stream.Advance();
                }
            }
            finally
            {
                stream.Dispose();
            }
        }

        // Build sorted list from unordered results
        var withKeys = new List<(long orderedKey, EntityId id)>(unordered.Count);
        foreach (var id in unordered)
        {
            var pk = (long)id.RawValue;
            var orderedKey = entityKeyMap.GetValueOrDefault(pk, id.EntityKey);
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

    /// <summary>Execute targeted scan via PipelineExecutor with archetype mask post-filter.</summary>
    private HashSet<EntityId> ExecuteTargeted(string callerFile = null, int callerLine = 0, string callerMethod = null)
    {
        var ct = _whereComponentTable;

        var evaluators = QueryResolverHelper.ResolveEvaluators(SingleBranchOrThrow("One-shot execution"), ct, 0);
        var plan = PlanBuilder.Instance.BuildPlanAttributed(evaluators, ct, PlannerStats(ct), AdvancedSelectivityEstimator.Instance, null,
            queryInstanceKind: 1, queryInstanceLocalId: (uint)EcsQueryId,
            definitionSourceFile: SourceFile, definitionSourceLine: SourceLine, definitionSourceMethod: SourceMethod,
            executionSourceFile: callerFile, executionSourceLine: callerLine, executionSourceMethod: callerMethod);

        // Scan for matching entities across all matching archetypes.
        var result = new HashSet<EntityId>(_take > 0 ? _take : 64);
        var sink = new EntityIdSetSink(result);
        ScanAllArchetypes(plan, evaluators, ct, ref sink);

        // Read-your-own-writes: pending spawns have no secondary index entries, so the targeted scan above can't find them. Evaluate them via compiled
        // predicate fallback.
        CollectPendingSpawnsWithFieldFilter(result);

        // Opaque WHERE post-filter (from .Where<T>(Func), separate from WhereField)
        var filter = _whereFilter;
        if (filter != null)
        {
            var tx = _tx;
            result.RemoveWhere(id => !filter(id, tx));
        }

        return result;
    }

    /// <summary>
    /// The one place that knows secondary indexes live in two homes: per-archetype for cluster-backed archetypes (values are packed
    /// <c>ClusterLocation</c>s) and per-ComponentTable for the rest (values are chunk ids). Scans every archetype this query's mask admits, from whichever
    /// home owns it, and deposits the matches in <paramref name="sink"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared deliberately. This loop used to exist only inside <see cref="ExecuteTargeted"/>, while the four view-population call sites passed the
    /// ComponentTable straight to <c>PipelineExecutor</c> — scanning a tree that is empty for a cluster-backed archetype, so <c>ToView()</c> came back
    /// permanently empty while <c>Execute()</c> on the same query was correct (#663). One copy cannot drift from the other.
    /// </para>
    /// <para>
    /// The two homes filter by archetype at different points: the cluster loop tests the mask per ARCHETYPE before scanning, while the ComponentTable is
    /// shared across every archetype holding that component, so its results are filtered per ENTITY by routing id.
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
                if (!meta.HasClusterIndexes)
                {
                    hasNonClusterArchetypes = true;
                    continue;
                }

                var engineState = dbe._archetypeStates[meta.ArchetypeId];
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

                // Query planner: choose Path A (B+Tree selective) vs Path B (zone map + eval) based on selectivity.
                // A Transient home always takes Path B. Path A range-scans the tree, and the collector is typed to BTreeBase<PersistentStore>; Path B never
                // touches a tree at all, so it is correct for either home. Selecting it here costs a full SoA scan instead of a selective one — a performance
                // gap, not a correctness one, and one that #665 revisits when it unfreezes cluster selectivity statistics (#655).
                if (!transientHome && plan.UsesSecondaryIndex && EstimateClusterSelectivity(plan, clusterState) < 0.05f)
                {
                    ScanPerArchetypeBTreeSelective(plan, evaluators, clusterState, meta, ref sink);
                }
                else
                {
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
                $"Query on '{ct?.Definition?.Name}' matched an archetype with no per-archetype index for the where-component. There is no longer a shared "
                + "index home to fall back to, and answering from the cluster scan alone would silently omit that archetype's entities.");
        }
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
            var svAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                ScanClusterSoa(evaluators, clusterState, clusterState.IndexSlots, ixSlotIdx, ref svAccessor, ref svAccessor, ref result);
            }
            finally
            {
                svAccessor.Dispose();
            }

            return;
        }

        var transientAccessor = clusterState.TransientSegment.CreateChunkAccessor();
        try
        {
            if (clusterState.ClusterSegment == null)
            {
                ScanClusterSoa(evaluators, clusterState, clusterState.TransientIndexSlots, ixSlotIdx, ref transientAccessor, ref transientAccessor,
                    ref result);
            }
            else
            {
                var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
                try
                {
                    ScanClusterSoa(evaluators, clusterState, clusterState.TransientIndexSlots, ixSlotIdx, ref clusterAccessor, ref transientAccessor,
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
    private void ScanClusterSoa<TSink, TPrimary, TData>(FieldEvaluator[] evaluators, ArchetypeClusterState clusterState, ClusterIndexSlot<TData>[] ixSlots,
        int ixSlotIdx, ref ChunkAccessor<TPrimary> primaryAccessor, ref ChunkAccessor<TData> dataAccessor, ref TSink result)
        where TSink : struct, IEntityIdSink
        where TPrimary : struct, IPageStore
        where TData : struct, IPageStore
    {
        ref var matchSlot = ref ixSlots[ixSlotIdx];
        var layout = clusterState.Layout;
        var compSlot = matchSlot.Slot;
        var compSize = layout.ComponentSize(compSlot);
        var compOffset = layout.ComponentOffset(compSlot);

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

        for (var c = 0; c < clusterState.ActiveClusterCount; c++)
        {
            var clusterChunkId = clusterState.ActiveClusterIds[c];

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

                if (skip)
                {
                    continue;
                }
            }

            var clusterBase = primaryAccessor.GetChunkAddress(clusterChunkId);
            var occupancy = *(ulong*)clusterBase;
            if (occupancy == 0)
            {
                continue;
            }

            // The component column can live in a DIFFERENT segment from the occupancy word — a Transient slot on a mixed archetype. Chunk ids are held in
            // lockstep between the two segments, so the same clusterChunkId addresses both.
            var compBase = dataAccessor.GetChunkAddress(clusterChunkId) + compOffset;

            if (anySimd)
            {
                // SIMD path: batch-evaluate SIMD-eligible evaluators, then scalar-verify the rest
                var matchBits = occupancy;

                // Phase 1: SIMD evaluators narrow the match set
                for (var e = 0; e < evalCount; e++)
                {
                    if (!simdEligible[e])
                    {
                        continue;
                    }

                    matchBits &= SimdPredicateEvaluator.EvaluateCluster(ref evaluators[e], compBase, compSize, clusterSize);
                    if (matchBits == 0)
                    {
                        break;
                    }
                }

                // Phase 2: scalar-verify non-SIMD evaluators on remaining matches
                while (matchBits != 0)
                {
                    var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(matchBits);
                    matchBits &= matchBits - 1;

                    var entityComp = compBase + slotIndex * compSize;
                    var pass = true;
                    for (var e = 0; e < evalCount; e++)
                    {
                        if (simdEligible[e])
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
                        result.Add(EntityId.FromRaw(*(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8)));
                    }
                }
            }
            else
            {
                // Scalar path (unchanged): evaluate each occupied entity against all field predicates
                while (occupancy != 0)
                {
                    var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    var entityComp = compBase + slotIndex * compSize;
                    var allMatch = true;
                    for (var e = 0; e < evaluators.Length; e++)
                    {
                        ref var eval = ref evaluators[e];
                        if (!FieldEvaluator.Evaluate(ref eval, entityComp + eval.FieldOffset))
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                    {
                        result.Add(EntityId.FromRaw(*(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8)));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Estimate selectivity for the primary predicate of a cluster query. Returns a value in [0, 1] where lower = more selective.
    /// Uses the plan's EstimatedCounts (from selectivity estimator) divided by total entity count in this archetype's clusters.
    /// Falls back to 0.5 (moderate selectivity → Path B) when estimates are unavailable.
    /// </summary>
    private static float EstimateClusterSelectivity(ExecutionPlan plan, ArchetypeClusterState clusterState)
    {
        if (plan.EstimatedCounts == null || plan.EstimatedCounts.Length == 0)
        {
            return 0.5f;
        }

        // EstimatedCounts[0] = estimated match count for the most selective predicate.
        // This estimate comes from the shared per-ComponentTable B+Tree, which may have 0 entries
        // for cluster archetypes (all entities in per-archetype B+Trees). Treat 0 as "unknown" → Path B.
        var estimated = plan.EstimatedCounts[0];
        if (estimated <= 0)
        {
            return 0.5f;
        }

        // Total entity estimate: ActiveClusterCount * ClusterSize (upper bound)
        var total = (long)clusterState.ActiveClusterCount * clusterState.Layout.ClusterSize;
        if (total <= 0)
        {
            return 0.5f;
        }

        return (float)estimated / total;
    }

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

    /// <summary>Get the maximum value for a KeyType encoded as a long (same encoding as PlanBuilder scan bounds).</summary>
    private static long GetTypeMaxAsLong(KeyType keyType) =>
        keyType switch
        {
            KeyType.SByte => sbyte.MaxValue,
            KeyType.Byte => byte.MaxValue,
            KeyType.Short => short.MaxValue,
            KeyType.UShort => ushort.MaxValue,
            KeyType.Int => int.MaxValue,
            KeyType.UInt => uint.MaxValue,
            KeyType.Long => long.MaxValue,
            KeyType.ULong => unchecked((long)ulong.MaxValue),
            KeyType.Float => BitConverter.SingleToInt32Bits(float.MaxValue),
            KeyType.Double => BitConverter.DoubleToInt64Bits(double.MaxValue),
            _ => long.MaxValue
        };

    /// <summary>Get the minimum value for a KeyType encoded as a long.</summary>
    private static long GetTypeMinAsLong(KeyType keyType) =>
        keyType switch
        {
            KeyType.SByte => sbyte.MinValue,
            KeyType.Byte => 0L,
            KeyType.Short => short.MinValue,
            KeyType.UShort => 0L,
            KeyType.Int => int.MinValue,
            KeyType.UInt => 0L,
            KeyType.Long => long.MinValue,
            KeyType.ULong => 0L,
            KeyType.Float => BitConverter.SingleToInt32Bits(float.MinValue),
            KeyType.Double => BitConverter.DoubleToInt64Bits(double.MinValue),
            _ => long.MinValue
        };

    /// <summary>
    /// Intersect scan bounds with evaluator predicates on a specific field.
    /// For float/double: converts to typed values for correct comparison (IEEE 754 bit patterns don't sort as signed longs for negatives).
    /// For integers: uses direct long comparison (preserves ordering).
    /// </summary>
    private static void IntersectEvaluatorBounds(FieldEvaluator[] evaluators, int fieldIdx, KeyType keyType, ref long scanMin, ref long scanMax)
    {
        for (var e = 0; e < evaluators.Length; e++)
        {
            if (evaluators[e].FieldIndex != fieldIdx || evaluators[e].CompareOp == CompareOp.NotEqual)
            {
                continue;
            }

            var thr = evaluators[e].Threshold;

            if (keyType == KeyType.Float)
            {
                // Float: convert bit patterns to float, compare, convert back.
                // Math.Max/Min on signed long bit patterns gives wrong results for negative floats.
                var fMin = BitConverter.Int32BitsToSingle((int)scanMin);
                var fMax = BitConverter.Int32BitsToSingle((int)scanMax);
                var fThr = BitConverter.Int32BitsToSingle((int)thr);
                switch (evaluators[e].CompareOp)
                {
                    case CompareOp.Equal:
                        fMin = Math.Max(fMin, fThr);
                        fMax = Math.Min(fMax, fThr);
                        break;
                    case CompareOp.GreaterThan:
                    case CompareOp.GreaterThanOrEqual:
                        fMin = Math.Max(fMin, fThr);
                        break;
                    case CompareOp.LessThan:
                    case CompareOp.LessThanOrEqual:
                        fMax = Math.Min(fMax, fThr);
                        break;
                }

                scanMin = BitConverter.SingleToInt32Bits(fMin);
                scanMax = BitConverter.SingleToInt32Bits(fMax);
            }
            else if (keyType == KeyType.Double)
            {
                var dMin = BitConverter.Int64BitsToDouble(scanMin);
                var dMax = BitConverter.Int64BitsToDouble(scanMax);
                var dThr = BitConverter.Int64BitsToDouble(thr);
                switch (evaluators[e].CompareOp)
                {
                    case CompareOp.Equal:
                        dMin = Math.Max(dMin, dThr);
                        dMax = Math.Min(dMax, dThr);
                        break;
                    case CompareOp.GreaterThan:
                    case CompareOp.GreaterThanOrEqual:
                        dMin = Math.Max(dMin, dThr);
                        break;
                    case CompareOp.LessThan:
                    case CompareOp.LessThanOrEqual:
                        dMax = Math.Min(dMax, dThr);
                        break;
                }

                scanMin = BitConverter.DoubleToInt64Bits(dMin);
                scanMax = BitConverter.DoubleToInt64Bits(dMax);
            }
            else if (keyType is KeyType.UInt or KeyType.ULong or KeyType.UShort or KeyType.Byte)
            {
                // Unsigned types: compare as ulong to avoid sign issues.
                // Threshold values are stored as signed long but represent unsigned values —
                // e.g. ulong.MaxValue is stored as -1L. Math.Min/Max on signed longs gives wrong results.
                var uMin = (ulong)scanMin;
                var uMax = (ulong)scanMax;
                var uThr = (ulong)thr;
                switch (evaluators[e].CompareOp)
                {
                    case CompareOp.Equal:
                        uMin = Math.Max(uMin, uThr);
                        uMax = Math.Min(uMax, uThr);
                        break;
                    case CompareOp.GreaterThan:
                        uMin = Math.Max(uMin, uThr + 1);
                        break;
                    case CompareOp.GreaterThanOrEqual:
                        uMin = Math.Max(uMin, uThr);
                        break;
                    case CompareOp.LessThan:
                        uMax = Math.Min(uMax, uThr - 1);
                        break;
                    case CompareOp.LessThanOrEqual:
                        uMax = Math.Min(uMax, uThr);
                        break;
                }
                scanMin = (long)uMin;
                scanMax = (long)uMax;
            }
            else
            {
                // Signed integer types: direct long comparison preserves ordering.
                switch (evaluators[e].CompareOp)
                {
                    case CompareOp.Equal:
                        scanMin = Math.Max(scanMin, thr);
                        scanMax = Math.Min(scanMax, thr);
                        break;
                    case CompareOp.GreaterThan:
                        scanMin = Math.Max(scanMin, thr + 1);
                        break;
                    case CompareOp.GreaterThanOrEqual:
                        scanMin = Math.Max(scanMin, thr);
                        break;
                    case CompareOp.LessThan:
                        scanMax = Math.Min(scanMax, thr - 1);
                        break;
                    case CompareOp.LessThanOrEqual:
                        scanMax = Math.Min(scanMax, thr);
                        break;
                }
            }
        }
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
        var matchBitsArr = System.Buffers.ArrayPool<ulong>.Shared.Rent(chunkCapacity);
        try
        {
            Array.Clear(matchBitsArr, 0, chunkCapacity);
            var hasAny = false;

            CollectClusterLocationsFromBTree(primaryIndex, plan.PrimaryKeyType, plan.PrimaryScanMin, plan.PrimaryScanMax, primaryField.AllowMultiple, 
                matchBitsArr, ref hasAny);

            if (!hasAny)
            {
                return;
            }

            // Pre-determine SIMD eligibility for each evaluator (once, before cluster loop)
            var evalCount = evaluators.Length;
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

            // Step 2: For each active cluster with matches, verify ALL evaluators on matched entities
            var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                for (var c = 0; c < clusterState.ActiveClusterCount; c++)
                {
                    var clusterChunkId = clusterState.ActiveClusterIds[c];
                    var candidateBits = matchBitsArr[clusterChunkId];
                    if (candidateBits == 0)
                    {
                        continue;
                    }

                    var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                    var occupancy = *(ulong*)clusterBase;
                    var remaining = candidateBits & occupancy; // intersection with live entities

                    if (remaining == 0)
                    {
                        continue;
                    }

                    var compBase = clusterBase + compOffset;

                    if (anySimd)
                    {
                        // SIMD path: batch-evaluate SIMD-eligible evaluators, then scalar-verify the rest
                        var matchBits = remaining;

                        for (var e = 0; e < evalCount; e++)
                        {
                            if (!simdEligible[e])
                            {
                                continue;
                            }

                            matchBits &= SimdPredicateEvaluator.EvaluateCluster(ref evaluators[e], compBase, compSize, clusterSize);
                            if (matchBits == 0)
                            {
                                break;
                            }
                        }

                        while (matchBits != 0)
                        {
                            var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(matchBits);
                            matchBits &= matchBits - 1;

                            var entityComp = compBase + slotIndex * compSize;
                            var pass = true;
                            for (var e = 0; e < evalCount; e++)
                            {
                                if (simdEligible[e])
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
                                result.Add(EntityId.FromRaw(*(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8)));
                            }
                        }
                    }
                    else
                    {
                        // Scalar path (unchanged)
                        while (remaining != 0)
                        {
                            var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                            remaining &= remaining - 1;

                            var entityComp = compBase + slotIndex * compSize;
                            var allMatch = true;
                            for (var e = 0; e < evaluators.Length; e++)
                            {
                                ref var eval = ref evaluators[e];
                                if (!FieldEvaluator.Evaluate(ref eval, entityComp + eval.FieldOffset))
                                {
                                    allMatch = false;
                                    break;
                                }
                            }

                            if (allMatch)
                            {
                                result.Add(EntityId.FromRaw(*(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8)));
                            }
                        }
                    }
                }
            }
            finally
            {
                clusterAccessor.Dispose();
            }
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(matchBitsArr);
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
                CollectTyped((BTree<long, PersistentStore>)index, scanMin, scanMax, allowMultiple, matchBitsArr, ref hasAny);
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
                        matchBitsArr[chunkId] |= 1UL << slotIdx;
                        hasAny = true;
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
                matchBitsArr[chunkId] |= 1UL << slotIdx;
                hasAny = true;
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

        // Fan out to both trees (SD1 guarantees no overlap). With per-component-type mode, only one is non-null.
        if (state.StaticTree != null)
        {
            QuerySingleTree(state.StaticTree, state, result);
        }
        if (state.DynamicTree != null)
        {
            QuerySingleTree(state.DynamicTree, state, result);
        }

        // Fan out to per-archetype cluster spatial index (issue #230 Phase 3 Option B).
        // Cluster entities are NOT in the shared per-table R-Tree — they have per-archetype indexes. AABB and Radius queries route to the per-cell cluster
        // index via ArchetypeClusterState.QueryAabb / QueryRadius. Under Option B, the SpatialGrid is guaranteed non-null for cluster spatial archetypes
        // (enforced at DatabaseEngine.InitializeArchetypes). Any other query shape on the cluster tier (e.g. Ray, Frustum) throws NotSupportedException —
        // adding Ray/Frustum on cluster archetypes is tracked as a follow-up sub-issue of #228.
        if (state.ClusterArchetypes != null)
        {
            var grid = _tx.DBE.SpatialGrid;
            foreach (var cs in state.ClusterArchetypes)
            {
                if (!cs.SpatialSlot.HasSpatialIndex)
                {
                    continue;
                }
                if (_spatialQueryType == SpatialQueryType.AABB)
                {
                    // New path: per-cell cluster index AABB query. Extract the 2D/3D query bounds from _spatialParams (same layout as in QuerySingleTree's
                    // AABB case: 6 doubles stored as [minX, minY, minZ, maxX, maxY, maxZ], with the 3D slots ignored for 2D archetypes).
                    var qMinX = (float)_spatialParams[0];
                    var qMinY = (float)_spatialParams[1];
                    float qMinZ;
                    float qMaxX;
                    float qMaxY;
                    float qMaxZ;
                    if (state.Descriptor.CoordCount == 4)
                    {
                        qMinZ = float.NegativeInfinity;
                        qMaxX = (float)_spatialParams[2];
                        qMaxY = (float)_spatialParams[3];
                        qMaxZ = float.PositiveInfinity;
                    }
                    else
                    {
                        qMinZ = (float)_spatialParams[2];
                        qMaxX = (float)_spatialParams[3];
                        qMaxY = (float)_spatialParams[4];
                        qMaxZ = (float)_spatialParams[5];
                    }

                    using var guard = EpochGuard.Enter(_tx.DBE.EpochManager);
                    foreach (var hit in cs.QueryAabb(grid, qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ))
                    {
                        var entityId = EntityId.FromRaw(hit.EntityId);
                        if (MaskTestByRouting(entityId.ArchetypeId))
                        {
                            result.Add(entityId);
                        }
                    }
                }
                else if (_spatialQueryType == SpatialQueryType.Radius)
                {
                    // Per-cell cluster index Radius query (issue #230 Phase 3). Parameter layout matches QuerySingleTree's Radius case:
                    // _spatialParams[0..halfCoord] is the center, _spatialParams[3] is the radius (regardless of dimension — a quirk of the existing
                    // parameter packing for the per-entity tree).
                    var cX = (float)_spatialParams[0];
                    var cY = (float)_spatialParams[1];
                    var cZ = state.Descriptor.CoordCount == 6 ? (float)_spatialParams[2] : 0f;
                    var radius = (float)_spatialParams[3];

                    using var guard = EpochGuard.Enter(_tx.DBE.EpochManager);
                    foreach (var hit in cs.QueryRadius(grid, cX, cY, cZ, radius))
                    {
                        var entityId = EntityId.FromRaw(hit.EntityId);
                        if (MaskTestByRouting(entityId.ArchetypeId))
                        {
                            result.Add(entityId);
                        }
                    }
                }
                else
                {
                    // Option B: any query shape beyond AABB / Radius is unsupported on the cluster tier. No silent fallback — surface the limitation.
                    throw new NotSupportedException(
                        $"Cluster spatial queries for shape '{_spatialQueryType}' are not implemented in issue #230 Phase 3 Option B. " +
                        $"Supported shapes on the cluster tier are AABB and Radius. See follow-up sub-issues of #228 for Ray/Frustum support.");
                }
            }
        }

        // Opaque WHERE post-filter (.Where(lambda))
        var filter = _whereFilter;
        if (filter != null)
        {
            result.RemoveWhere(id => !filter(id, tx));
        }

        // WhereField composition: when a field predicate is combined with a spatial predicate, the spatial index drives the candidate set and the compiled
        // field predicate is verified per-candidate (AND semantics). _pendingSpawnFieldFilter is the compiled form of every WhereField call chained with
        // AND — reused here as a general per-entity field verifier.
        var fieldFilter = _pendingSpawnFieldFilter;
        if (fieldFilter != null)
        {
            result.RemoveWhere(id => !fieldFilter(id, tx));
        }

        return result;
    }

    /// <summary>Query a single R-Tree and collect matching EntityIds into the result set.</summary>
    private void QuerySingleTree(SpatialRTree<PersistentStore> tree, SpatialIndexState state, HashSet<EntityId> result)
    {
        var tx = _tx;
        switch (_spatialQueryType)
        {
            case SpatialQueryType.AABB:
            {
                Span<double> coords = stackalloc double[6];
                for (var i = 0; i < 6; i++) coords[i] = _spatialParams[i];
                var coordSlice = coords[..state.Descriptor.CoordCount];

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryAABB(coordSlice))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTestByRouting(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
            case SpatialQueryType.Radius:
            {
                var halfCoord = state.Descriptor.CoordCount / 2;
                Span<double> center = stackalloc double[halfCoord];
                for (var i = 0; i < halfCoord; i++) center[i] = _spatialParams[i];

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryRadius(center, _spatialParams[3]))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTestByRouting(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
            case SpatialQueryType.Ray:
            {
                var halfCoord = state.Descriptor.CoordCount / 2;
                Span<double> origin = stackalloc double[halfCoord];
                Span<double> dir = stackalloc double[halfCoord];
                for (var i = 0; i < halfCoord; i++) { origin[i] = _spatialParams[i]; dir[i] = _spatialParams[3 + i]; }

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryRay(origin, dir, _spatialParams[6]))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTestByRouting(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
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
            if (pendingFieldFilter(entry.Id, tx))
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
        // foreach runs a broad archetype scan + the .Where(lambda) post-filter only. It does NOT apply WhereField, spatial, or OrderBy/Skip/Take — guard
        // against silently iterating the wrong set.
        if (HasFieldPredicates)
        {
            throw new InvalidOperationException("foreach does not apply WhereField predicates — call .Execute() to materialise the matches (or .ToView()).");
        }
        if (_spatialQueryType != SpatialQueryType.None)
        {
            throw new InvalidOperationException("foreach does not apply spatial predicates (WhereNearby / WhereInAABB / WhereRay) — call .Execute().");
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
        return new EcsQueryEnumerator(_tx, entities, _whereFilter);
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

            // Copy locations from SpawnEntry into EntityLocations
            var locs = new EntityLocations();
            for (var s = 0; s < meta.ComponentCount; s++)
            {
                locs.Values[s] = entry.Loc[s];
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

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var action = new BroadScanAction
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                OnMatch = onMatch,
                StopOnFirst = stopOnFirst,
                Found = false,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref action);
            accessor.Dispose();

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

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var pred = new BroadScanPredicate
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            total += engineState.EntityMap.CountEntries(ref accessor, ref pred);
            accessor.Dispose();
        }

        return total;
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

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var pred = new BroadScanPredicate
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            var found = engineState.EntityMap.AnyEntry(ref accessor, ref pred);
            accessor.Dispose();

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

            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var action = new BroadScanCollectAction
            {
                Meta = meta,
                RoutingId = _tx.DBE.RoutingIdOf(meta),
                TxTsn = txTsn,
                EnabledBitsOverrides = dbe.EnabledBitsOverrides,
                HasT2 = hasT2,
                RequiredEnabled = reqEnabled,
                RequiredDisabled = reqDisabled,
                Results = results,
                PendingEnableDisable = _tx.PendingEnableDisable,
                PendingDestroys = _tx.PendingDestroys,
            };
            engineState.EntityMap.ForEachEntry(ref accessor, ref action);
            accessor.Dispose();
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
            if (header.BornTSN != 0 && header.BornTSN > TxTsn)
            {
                return true; // Not yet born — skip, continue
            }
            if (header.DiedTSN != 0 && header.DiedTSN <= TxTsn)
            {
                return true; // Dead — skip, continue
            }

            var entityId = new EntityId(key, RoutingId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            var bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
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
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

        public bool Matches(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            // Visibility check (MVCC)
            if (header.BornTSN != 0 && header.BornTSN > TxTsn)
            {
                return false; // Not yet born
            }
            if (header.DiedTSN != 0 && header.DiedTSN <= TxTsn)
            {
                return false; // Dead
            }

            var entityId = new EntityId(key, RoutingId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return false;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            var bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
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
        public ushort RequiredEnabled;
        public ushort RequiredDisabled;
        public List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> Results;
        public Dictionary<EntityId, ushort> PendingEnableDisable;
        public HashSet<EntityId> PendingDestroys;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);

            if (header.BornTSN != 0 && header.BornTSN > TxTsn)
            {
                return true;
            }
            if (header.DiedTSN != 0 && header.DiedTSN <= TxTsn)
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
            var bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
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
    /// Iterates pre-collected query results, yielding read-only EntityRefs with zero-copy component access.
    /// Entities returned by query enumeration are opened as read-only — use <see cref="Transaction.OpenMut"/> for writes.
    /// </summary>
    [PublicAPI]
    public ref struct EcsQueryEnumerator
    {
        private readonly Transaction _tx;
        private readonly List<(EntityId Id, ArchetypeMetadata Meta, ushort EnabledBits, EntityLocations Locations)> _entities;
        private readonly Func<EntityId, Transaction, bool> _whereFilter;
        private int _index;
        private EntityRef _current;

        internal EcsQueryEnumerator(Transaction tx, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> entities, Func<EntityId, Transaction, bool> whereFilter)
        {
            _tx = tx;
            _entities = entities;
            _whereFilter = whereFilter;
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
                if (_whereFilter != null && !_whereFilter(id, _tx))
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
                        _current = new EntityRef(id, meta, engineState, _tx, enabledBits, false);
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
