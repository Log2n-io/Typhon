using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using JetBrains.Annotations;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

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
/// ECS query builder with three-tier evaluation: T1 (ArchetypeMask), T2 (EnabledBits), T3 (WHERE — future).
/// Supports polymorphic queries (archetype + descendants) and exact queries (single archetype).
/// </summary>
[PublicAPI]
#pragma warning disable TYPHON005 // EcsQuery borrows Transaction, doesn't own it
public unsafe struct EcsQuery<TArchetype> where TArchetype : class
{
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

    internal EcsQuery(Transaction tx, bool polymorphic)
    {
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
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
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
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
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
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component {typeof(T).Name} not registered");
        // Phase 7: ECS:Query:Constraint:Enabled instant.
        TyphonEvent.EmitEcsQueryConstraintEnabled((ushort)Math.Min(typeId, ushort.MaxValue), 1);
        AddEnabledTypeId(typeId);
        return this;
    }

    /// <summary>Include only entities where <typeparamref name="T"/> is disabled.</summary>
    public EcsQuery<TArchetype> Disabled<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component {typeof(T).Name} not registered");
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

    private readonly int MaskMaxId => _useLargeMask ? _maskLarge.MaxId : _mask256.MaxId;

    // ═══════════════════════════════════════════════════════════════════════
    // Tier 3 constraints — WHERE predicates (broad scan evaluation)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Filter entities by a component field predicate. Evaluated per-entity during broad scan via <see cref="Transaction.Open"/> + <see cref="EntityRef.TryRead{T}"/>.
    /// Multiple Where calls chain as AND (each must pass).
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

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial predicates
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Filter by radius (sphere) around a center point. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereNearby<T>(double centerX, double centerY, double centerZ, double radius) where T : unmanaged
    {
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        Debug.Assert(_spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
        _spatialQueryType = SpatialQueryType.Radius;
        _spatialParams[0] = centerX; _spatialParams[1] = centerY; _spatialParams[2] = centerZ; _spatialParams[3] = radius;
        // Phase 7: ECS:Query:Spatial:Attach instant. queryBox encodes the bounding box of the radius sphere.
        TyphonEvent.EmitEcsQuerySpatialAttach((byte)SpatialQueryType.Radius, (float)(centerX - radius), (float)(centerY - radius), (float)(centerX + radius), (float)(centerY + radius));
        return this;
    }

    /// <summary>Filter by AABB overlap. Component <typeparamref name="T"/> must have <c>[SpatialIndex]</c>.</summary>
    public EcsQuery<TArchetype> WhereInAABB<T>(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) where T : unmanaged
    {
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        Debug.Assert(_spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
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
        _spatialTable = _tx.DBE.GetComponentTable<T>();
        Debug.Assert(_spatialTable?.SpatialIndex != null, $"Component {typeof(T).Name} has no [SpatialIndex]");
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
    public readonly EcsNavigationQueryBuilder<TArchetype, TSource, TTarget> NavigateField<TSource, TTarget>(Expression<Func<TSource, long>> fkSelector)
        where TSource : unmanaged where TTarget : unmanaged
    {
        var fkFieldName = ExpressionParser.ExtractFieldName(fkSelector);
        return new EcsNavigationQueryBuilder<TArchetype, TSource, TTarget>(this, _tx, fkFieldName);
    }

    /// <summary>Test if an archetype ID matches the query mask. Used by EcsView to filter delta entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool MaskTestPublic(ushort archetypeId) => MaskTest(archetypeId);

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

        for (int i = 0; i < _enabledTypeIdCount; i++)
        {
            int typeId = i switch { 0 => _enabledTypeId0, 1 => _enabledTypeId1, 2 => _enabledTypeId2, _ => _enabledTypeId3 };
            if (!meta.TryGetSlot(typeId, out byte slot))
            {
                return false;
            }
            requiredEnabled |= (ushort)(1 << slot);
        }

        for (int i = 0; i < _disabledTypeIdCount; i++)
        {
            int typeId = i switch { 0 => _disabledTypeId0, 1 => _disabledTypeId1, 2 => _disabledTypeId2, _ => _disabledTypeId3 };
            if (!meta.TryGetSlot(typeId, out byte slot))
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
    public EcsView<TArchetype> ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (HasFieldPredicates)
        {
            return ToIncrementalView(bufferCapacity);
        }

        // Pull mode: no field evaluators
        return ToPullView(bufferCapacity);
    }

    private EcsView<TArchetype> ToPullView(int bufferCapacity)
    {
        var initialSet = Execute();
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
        var firstTable = engineState.SlotToComponentTable[0];

        var view = new EcsView<TArchetype>(this, firstTable.DBE.MemoryAllocator, firstTable, bufferCapacity, _tx.TSN);

        // Populate initial entity set
        foreach (var id in initialSet)
        {
            view.AddEntityDirect((long)id.RawValue);
        }

        return view;
    }

    private EcsView<TArchetype> ToIncrementalView(int bufferCapacity)
    {
        var ct = _whereComponentTable;
        var branches = _fieldPredicateBranches;

        if (branches.Length > 1)
        {
            // OR path: create EcsOrView
            return ToOrView(ct, branches, bufferCapacity);
        }

        // Single AND branch
        var evaluators = QueryResolverHelper.ResolveEvaluators(branches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);

        var view = new EcsView<TArchetype>(this, evaluators, ct, _whereFieldReader, plan, bufferCapacity, _tx.TSN);

        // Register with ViewRegistry for delta notifications
        ct.ViewRegistry.RegisterView(view);

        // Initial population via PipelineExecutor (uses secondary index if plan selects one)
        _whereFieldReader.ExecuteFullScan(plan, plan.OrderedEvaluators, ct, _tx, view.EntityIdsInternal);

        // Process any deltas that arrived during population
        view.Refresh(_tx);
        view.ClearDelta();

        return view;
    }

    private EcsView<TArchetype> ToOrView(ComponentTable ct, FieldPredicate[][] branches, int bufferCapacity)
    {
        var branchEvaluators = new FieldEvaluator[branches.Length][];
        var plans = new ExecutionPlan[branches.Length];
        for (var b = 0; b < branches.Length; b++)
        {
            branchEvaluators[b] = QueryResolverHelper.ResolveEvaluators(branches[b], ct, 0, (byte)b);
            plans[b] = PlanBuilder.Instance.BuildPlan(branchEvaluators[b], ct, AdvancedSelectivityEstimator.Instance);
        }

        var view = new EcsView<TArchetype>(this, branchEvaluators, plans, ct, _whereFieldReader, bufferCapacity, _tx.TSN);
        ct.ViewRegistry.RegisterView(view);

        view.PopulateInitialOr(_tx);
        view.Refresh(_tx);
        view.ClearDelta();

        return view;
    }

    /// <summary>Rebind this query to a different transaction (different TSN → different visibility).</summary>
    internal void UpdateTransaction(Transaction tx) => _tx = tx;

    /// <summary>Execute the query and collect matching entity IDs into a HashSet.</summary>
    public HashSet<EntityId> Execute()
    {
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

            // Targeted scan via PipelineExecutor when field predicates are present
            if (HasFieldPredicates)
            {
                var targeted = ExecuteTargeted();
                scope.ScanMode = EcsQueryScanMode.Targeted;
                scope.ResultCount = targeted.Count;
                return targeted;
            }

            // Spatial-driven scan: spatial index produces candidates, filtered by archetype mask + visibility
            if (_spatialQueryType != SpatialQueryType.None)
            {
                var spatial = ExecuteSpatial();
                scope.ScanMode = EcsQueryScanMode.Spatial;
                scope.ResultCount = spatial.Count;
                return spatial;
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
    public List<EntityId> ExecuteOrdered()
    {
        if (!_orderBy.HasValue)
        {
            throw new InvalidOperationException("ExecuteOrdered requires OrderByField.");
        }
        if (!HasFieldPredicates)
        {
            throw new InvalidOperationException("ExecuteOrdered requires WhereField to identify the component table.");
        }
        if (MaskIsEmpty)
        {
            return [];
        }

        var ct = _whereComponentTable;
        var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance, _orderBy.Value);

        // Detect cluster vs non-cluster archetypes in the mask
        bool hasClusterArchetypes = false;
        bool hasNonClusterArchetypes = false;
        var dbe = _tx.DBE;
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!MaskTest(meta.ArchetypeId))
            {
                continue;
            }
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState?.IndexSlots != null && meta.HasClusterIndexes)
            {
                hasClusterArchetypes = true;
            }
            else
            {
                hasNonClusterArchetypes = true;
            }
        }

        if (!hasClusterArchetypes)
        {
            // Pure non-cluster path: existing PipelineExecutor ordered scan (unchanged)
            return ExecuteOrderedNonCluster(plan);
        }

        if (!hasNonClusterArchetypes)
        {
            // Pure cluster path: K-way merge over per-archetype B+Trees
            return ExecuteOrderedClustered(plan, evaluators);
        }

        // Mixed path: sort fallback handles true global ordering across cluster + non-cluster archetypes
        return ExecuteOrderedViaSortFallback(evaluators, plan);
    }

    /// <summary>Original non-cluster ordered execution path via PipelineExecutor.</summary>
    private List<EntityId> ExecuteOrderedNonCluster(ExecutionPlan plan)
    {
        var ct = _whereComponentTable;
        var pkResult = new List<long>();
        _whereFieldReader.ExecuteOrderedScan(plan, plan.OrderedEvaluators, ct, _tx, pkResult);

        var result = new List<EntityId>(_take > 0 ? _take : Math.Min(pkResult.Count, 256));
        int skipped = 0;
        int taken = 0;
        int take = _take > 0 ? _take : int.MaxValue;

        for (var i = 0; i < pkResult.Count; i++)
        {
            var entityId = EntityId.FromRaw(pkResult[i]);
            if (!MaskTest(entityId.ArchetypeId))
            {
                continue;
            }
            if (skipped < _skip)
            {
                skipped++;
                continue;
            }
            result.Add(entityId);
            taken++;
            if (taken >= take)
            {
                break;
            }
        }

        return result;
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
        int streamCount = 0;

        // Early termination: each per-archetype stream only needs skip+take entries at most.
        // The B+Tree enumerator yields in sort order, so stopping early is correct.
        int maxPerStream = _take > 0 ? _skip + _take : 0;

        // The plan's PrimaryFieldIndex may be -1 when the shared B+Tree has 0 entries (cluster archetypes store entries in per-archetype B+Trees,
        // not the shared one). In that case, use the OrderBy field index directly and full type range for scan bounds.
        Debug.Assert(_orderBy.HasValue, "ExecuteOrderedClustered requires OrderBy to be set");
        int orderByFieldIdx = _orderBy.Value.FieldIndex;
        bool descending = plan.Descending;
        int primaryFieldIdx = plan.PrimaryFieldIndex >= 0 ? plan.PrimaryFieldIndex : orderByFieldIdx;

        // If there are evaluators on fields OTHER than the scan field, the B+Tree scan won't filter them.
        // Fall back to ExecuteTargeted (which verifies all evaluators) + sort for correctness.
        for (int e = 0; e < evaluators.Length; e++)
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

                int ixSlotIdx = FindClusterIndexSlot(clusterState, meta);
                if (ixSlotIdx < 0)
                {
                    continue;
                }

                ref var matchSlot = ref clusterState.IndexSlots[ixSlotIdx];

                // Determine which field's B+Tree to scan for ordering.
                // If the plan selected a secondary index (PrimaryFieldIndex >= 0), use it.
                // Otherwise, use the OrderBy field index directly (the shared B+Tree had 0 entries).
                int fieldIdx = plan.PrimaryFieldIndex >= 0 ? plan.PrimaryFieldIndex : orderByFieldIdx;
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
                    for (int e = 0; e < evaluators.Length; e++)
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
            for (int i = 0; i < streamCount; i++)
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
        int skipped = 0;
        int taken = 0;
        int take = _take > 0 ? _take : int.MaxValue;

        while (merge.MoveNext(out long entityPK))
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
        int skipped = 0;
        int taken = 0;
        int take = _take > 0 ? _take : int.MaxValue;

        for (int i = 0; i < pks.Count; i++)
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
        int orderByFieldIdx = _orderBy.Value.FieldIndex;
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

            int ixSlotIdx = FindClusterIndexSlot(clusterState, meta);
            if (ixSlotIdx < 0 || orderByFieldIdx < 0 || orderByFieldIdx >= clusterState.IndexSlots[ixSlotIdx].Fields.Length)
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
            long pk = (long)id.RawValue;
            long orderedKey = entityKeyMap.GetValueOrDefault(pk, id.EntityKey);
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
        int skipped = 0;
        int taken = 0;
        int take = _take > 0 ? _take : int.MaxValue;
        for (int i = 0; i < withKeys.Count; i++)
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
    private HashSet<EntityId> ExecuteTargeted()
    {
        var ct = _whereComponentTable;

        var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
        var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);

        // Scan for matching entities across all matching archetypes.
        // Cluster archetypes: direct cluster scan with evaluator predicates (bypasses shared B+Tree).
        // Non-cluster archetypes: shared ComponentTable B+Tree via PipelineExecutor.
        var result = new HashSet<EntityId>(_take > 0 ? _take : 64);
        bool hasNonClusterArchetypes = false;

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
                if (clusterState?.IndexSlots == null)
                {
                    hasNonClusterArchetypes = true;
                    continue;
                }

                // Query planner: choose Path A (B+Tree selective) vs Path B (zone map + eval) based on selectivity
                if (plan.UsesSecondaryIndex && EstimateClusterSelectivity(plan, clusterState) < 0.05f)
                {
                    ScanPerArchetypeBTreeSelective(plan, evaluators, clusterState, meta, result);
                }
                else
                {
                    ScanPerArchetypeBTree(plan, evaluators, clusterState, meta, result);
                }
            }
        }

        // Fall through to shared pipeline for non-cluster archetypes
        if (hasNonClusterArchetypes)
        {
            var pkResult = new HashMap<long>();
            _whereFieldReader.ExecuteFullScan(plan, plan.OrderedEvaluators, ct, _tx, pkResult);

            foreach (var pk in pkResult)
            {
                var entityId = EntityId.FromRaw(pk);
                if (MaskTest(entityId.ArchetypeId))
                {
                    result.Add(entityId);
                }
            }
        }

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
    /// Scan cluster entities for a per-archetype indexed archetype using direct cluster evaluation (Path B).
    /// Evaluates all field predicates on cluster SoA data, resolving EntityKeys from the cluster.
    /// </summary>
    private void ScanPerArchetypeBTree(ExecutionPlan plan, FieldEvaluator[] evaluators, ArchetypeClusterState clusterState, ArchetypeMetadata meta,
        HashSet<EntityId> result)
    {
        int ixSlotIdx = FindClusterIndexSlot(clusterState, meta);
        if (ixSlotIdx < 0)
        {
            return;
        }

        var ixSlots = clusterState.IndexSlots;
        ref var matchSlot = ref ixSlots[ixSlotIdx];
        var layout = clusterState.Layout;
        int compSlot = matchSlot.Slot;
        int compSize = layout.ComponentSize(compSlot);
        int compOffset = layout.ComponentOffset(compSlot);

        // Pre-compute zone map query bounds for each evaluator (zone map pruning).
        // Bounds stored on stack; zone map references accessed via field iteration (no ref-type array allocation).
        int evalCount = evaluators.Length;
        var zoneMapMins = evalCount <= 8 ? stackalloc long[evalCount] : new long[evalCount];
        var zoneMapMaxs = evalCount <= 8 ? stackalloc long[evalCount] : new long[evalCount];
        // Track which evaluators have zone map bounds (bit per evaluator, fits in ulong for ≤64 evaluators)
        ulong zoneMapEvalMask = 0;
        bool hasZoneMaps = false;

        for (int e = 0; e < evalCount && e < 64; e++)
        {
            ref var eval = ref evaluators[e];
            for (int fi = 0; fi < matchSlot.Fields.Length; fi++)
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
        bool anySimd = false;
        Span<bool> simdEligible = evalCount <= 8 ? stackalloc bool[8] : new bool[evalCount];
        if (Avx2.IsSupported)
        {
            for (int e = 0; e < evalCount; e++)
            {
                simdEligible[e] = SimdPredicateEvaluator.IsSimdEligible(evaluators[e].KeyType);
                anySimd |= simdEligible[e];
            }
        }

        int clusterSize = layout.ClusterSize;

        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int c = 0; c < clusterState.ActiveClusterCount; c++)
            {
                int clusterChunkId = clusterState.ActiveClusterIds[c];

                // Zone map pruning: skip cluster if any predicate's range doesn't overlap the cluster's [min, max].
                // Iterates fields to find zone maps, then checks matching evaluators — avoids ref-type array allocation.
                if (hasZoneMaps)
                {
                    bool skip = false;
                    for (int fi = 0; fi < matchSlot.Fields.Length && !skip; fi++)
                    {
                        ref var field = ref matchSlot.Fields[fi];
                        if (field.ZoneMap == null)
                        {
                            continue;
                        }

                        for (int e = 0; e < evalCount && !skip; e++)
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

                byte* clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                ulong occupancy = *(ulong*)clusterBase;
                if (occupancy == 0)
                {
                    continue;
                }

                byte* compBase = clusterBase + compOffset;

                if (anySimd)
                {
                    // SIMD path: batch-evaluate SIMD-eligible evaluators, then scalar-verify the rest
                    ulong matchBits = occupancy;

                    // Phase 1: SIMD evaluators narrow the match set
                    for (int e = 0; e < evalCount; e++)
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
                        int slotIndex = System.Numerics.BitOperations.TrailingZeroCount(matchBits);
                        matchBits &= matchBits - 1;

                        byte* entityComp = compBase + slotIndex * compSize;
                        bool pass = true;
                        for (int e = 0; e < evalCount; e++)
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
                        int slotIndex = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                        occupancy &= occupancy - 1;

                        byte* entityComp = compBase + slotIndex * compSize;
                        bool allMatch = true;
                        for (int e = 0; e < evaluators.Length; e++)
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
        long estimated = plan.EstimatedCounts[0];
        if (estimated <= 0)
        {
            return 0.5f;
        }

        // Total entity estimate: ActiveClusterCount * ClusterSize (upper bound)
        long total = (long)clusterState.ActiveClusterCount * clusterState.Layout.ClusterSize;
        if (total <= 0)
        {
            return 0.5f;
        }

        return (float)estimated / total;
    }

    /// <summary>
    /// Find the cluster index slot that corresponds to <see cref="_whereComponentTable"/>.
    /// Returns the index into <see cref="ArchetypeClusterState.IndexSlots"/>, or -1 if not found.
    /// </summary>
    private int FindClusterIndexSlot(ArchetypeClusterState clusterState, ArchetypeMetadata meta)
    {
        var ixSlots = clusterState.IndexSlots;
        var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
        for (int s = 0; s < ixSlots.Length; s++)
        {
            if (engineState.SlotToComponentTable[ixSlots[s].Slot] == _whereComponentTable)
            {
                return s;
            }
        }

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
        for (int e = 0; e < evaluators.Length; e++)
        {
            if (evaluators[e].FieldIndex != fieldIdx || evaluators[e].CompareOp == CompareOp.NotEqual)
            {
                continue;
            }

            long thr = evaluators[e].Threshold;

            if (keyType == KeyType.Float)
            {
                // Float: convert bit patterns to float, compare, convert back.
                // Math.Max/Min on signed long bit patterns gives wrong results for negative floats.
                float fMin = BitConverter.Int32BitsToSingle((int)scanMin);
                float fMax = BitConverter.Int32BitsToSingle((int)scanMax);
                float fThr = BitConverter.Int32BitsToSingle((int)thr);
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
                double dMin = BitConverter.Int64BitsToDouble(scanMin);
                double dMax = BitConverter.Int64BitsToDouble(scanMax);
                double dThr = BitConverter.Int64BitsToDouble(thr);
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
                ulong uMin = (ulong)scanMin;
                ulong uMax = (ulong)scanMax;
                ulong uThr = (ulong)thr;
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
    private void ScanPerArchetypeBTreeSelective(ExecutionPlan plan, FieldEvaluator[] evaluators, ArchetypeClusterState clusterState,
        ArchetypeMetadata meta, HashSet<EntityId> result)
    {
        int ixSlotIdx = FindClusterIndexSlot(clusterState, meta);
        if (ixSlotIdx < 0)
        {
            return;
        }

        var ixSlots = clusterState.IndexSlots;

        ref var matchSlot = ref ixSlots[ixSlotIdx];
        var layout = clusterState.Layout;
        int compSlot = matchSlot.Slot;
        int compSize = layout.ComponentSize(compSlot);
        int compOffset = layout.ComponentOffset(compSlot);

        // Find the primary field's B+Tree matching the plan's PrimaryFieldIndex
        if (plan.PrimaryFieldIndex < 0 || plan.PrimaryFieldIndex >= matchSlot.Fields.Length)
        {
            // Fall back to Path B (full scan) if primary field not found
            ScanPerArchetypeBTree(plan, evaluators, clusterState, meta, result);
            return;
        }

        ref var primaryField = ref matchSlot.Fields[plan.PrimaryFieldIndex];
        var primaryIndex = primaryField.Index;

        // Step 1: Range scan B+Tree → collect ClusterLocations grouped by clusterChunkId.
        // Use a flat array indexed by clusterChunkId (bounded by segment ChunkCapacity, typically small).
        int chunkCapacity = clusterState.ClusterSegment.ChunkCapacity;
        var matchBitsArr = System.Buffers.ArrayPool<ulong>.Shared.Rent(chunkCapacity);
        try
        {
            Array.Clear(matchBitsArr, 0, chunkCapacity);
            bool hasAny = false;

            CollectClusterLocationsFromBTree(primaryIndex, plan.PrimaryKeyType, plan.PrimaryScanMin, plan.PrimaryScanMax, primaryField.AllowMultiple, 
                matchBitsArr, ref hasAny);

            if (!hasAny)
            {
                return;
            }

            // Pre-determine SIMD eligibility for each evaluator (once, before cluster loop)
            int evalCount = evaluators.Length;
            bool anySimd = false;
            Span<bool> simdEligible = evalCount <= 8 ? stackalloc bool[8] : new bool[evalCount];
            if (Avx2.IsSupported)
            {
                for (int e = 0; e < evalCount; e++)
                {
                    simdEligible[e] = SimdPredicateEvaluator.IsSimdEligible(evaluators[e].KeyType);
                    anySimd |= simdEligible[e];
                }
            }

            int clusterSize = layout.ClusterSize;

            // Step 2: For each active cluster with matches, verify ALL evaluators on matched entities
            var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                for (int c = 0; c < clusterState.ActiveClusterCount; c++)
                {
                    int clusterChunkId = clusterState.ActiveClusterIds[c];
                    ulong candidateBits = matchBitsArr[clusterChunkId];
                    if (candidateBits == 0)
                    {
                        continue;
                    }

                    byte* clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                    ulong occupancy = *(ulong*)clusterBase;
                    ulong remaining = candidateBits & occupancy; // intersection with live entities

                    if (remaining == 0)
                    {
                        continue;
                    }

                    byte* compBase = clusterBase + compOffset;

                    if (anySimd)
                    {
                        // SIMD path: batch-evaluate SIMD-eligible evaluators, then scalar-verify the rest
                        ulong matchBits = remaining;

                        for (int e = 0; e < evalCount; e++)
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
                            int slotIndex = System.Numerics.BitOperations.TrailingZeroCount(matchBits);
                            matchBits &= matchBits - 1;

                            byte* entityComp = compBase + slotIndex * compSize;
                            bool pass = true;
                            for (int e = 0; e < evalCount; e++)
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
                            int slotIndex = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                            remaining &= remaining - 1;

                            byte* entityComp = compBase + slotIndex * compSize;
                            bool allMatch = true;
                            for (int e = 0; e < evaluators.Length; e++)
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
    /// ULong is stored as <c>BTree&lt;long&gt;</c> (same convention as <see cref="PipelineExecutor"/>).
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
                    for (int i = 0; i < values.Length; i++)
                    {
                        int clusterLocation = values[i];
                        int chunkId = clusterLocation >> 6;
                        int slotIdx = clusterLocation & 0x3F;
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
                int clusterLocation = item.Value;
                int chunkId = clusterLocation >> 6;
                int slotIdx = clusterLocation & 0x3F;
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
            SpatialGrid grid = _tx.DBE.SpatialGrid;
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
                    float qMinX = (float)_spatialParams[0];
                    float qMinY = (float)_spatialParams[1];
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
                        if (MaskTest(entityId.ArchetypeId))
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
                    float cX = (float)_spatialParams[0];
                    float cY = (float)_spatialParams[1];
                    float cZ = state.Descriptor.CoordCount == 6 ? (float)_spatialParams[2] : 0f;
                    float radius = (float)_spatialParams[3];

                    using var guard = EpochGuard.Enter(_tx.DBE.EpochManager);
                    foreach (var hit in cs.QueryRadius(grid, cX, cY, cZ, radius))
                    {
                        var entityId = EntityId.FromRaw(hit.EntityId);
                        if (MaskTest(entityId.ArchetypeId))
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

        // Opaque WHERE post-filter
        var filter = _whereFilter;
        if (filter != null)
        {
            result.RemoveWhere(id => !filter(id, tx));
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
                for (int i = 0; i < 6; i++) coords[i] = _spatialParams[i];
                var coordSlice = coords[..state.Descriptor.CoordCount];

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryAABB(coordSlice))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTest(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
            case SpatialQueryType.Radius:
            {
                int halfCoord = state.Descriptor.CoordCount / 2;
                Span<double> center = stackalloc double[halfCoord];
                for (int i = 0; i < halfCoord; i++) center[i] = _spatialParams[i];

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryRadius(center, _spatialParams[3]))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTest(entityId.ArchetypeId))
                    {
                        result.Add(entityId);
                    }
                }
                break;
            }
            case SpatialQueryType.Ray:
            {
                int halfCoord = state.Descriptor.CoordCount / 2;
                Span<double> origin = stackalloc double[halfCoord];
                Span<double> dir = stackalloc double[halfCoord];
                for (int i = 0; i < halfCoord; i++) { origin[i] = _spatialParams[i]; dir[i] = _spatialParams[3 + i]; }

                using var guard = EpochGuard.Enter(tx.DBE.EpochManager);
                foreach (var hit in tree.QueryRay(origin, dir, _spatialParams[6]))
                {
                    var entityId = EntityId.FromRaw(hit.EntityId);
                    if (MaskTest(entityId.ArchetypeId))
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
        for (int i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];
            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }
            if (!MaskTest(entry.Id.ArchetypeId))
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
    public int Count()
    {
        var scope = TyphonEvent.BeginEcsQueryCount(0);
        try
        {
            if (MaskIsEmpty)
            {
                scope.ScanMode = EcsQueryScanMode.Empty;
                scope.ResultCount = 0;
                return 0;
            }

            // Targeted count via PipelineExecutor — avoids allocating result collections
            if (HasFieldPredicates)
            {
                // If any matching archetypes use cluster storage, fall through to Execute().Count (cluster scan path handles counting correctly)
                bool anyCluster = false;
                foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
                {
                    if (MaskTest(meta.ArchetypeId) && meta.HasClusterIndexes)
                    {
                        anyCluster = true;
                        break;
                    }
                }

                if (anyCluster)
                {
                    var targetedCount = ExecuteTargeted().Count;
                    scope.ScanMode = EcsQueryScanMode.TargetedCluster;
                    scope.ResultCount = targetedCount;
                    return targetedCount;
                }

                var ct = _whereComponentTable;
                var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
                var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
                var scanCount = _whereFieldReader.CountScan(plan, plan.OrderedEvaluators, ct, _tx);
                scope.ScanMode = EcsQueryScanMode.Targeted;
                scope.ResultCount = scanCount;
                return scanCount;
            }

            // If WHERE filter, use Execute (which applies post-filter) then count
            if (_whereFilter != null)
            {
                var executeCount = Execute().Count;
                scope.ScanMode = EcsQueryScanMode.Broad;
                scope.ResultCount = executeCount;
                return executeCount;
            }

            int count = 0;
            CollectMatching((_, _) => count++);
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
    public bool Any()
    {
        var scope = TyphonEvent.BeginEcsQueryAny(0);
        try
        {
            if (MaskIsEmpty)
            {
                scope.ScanMode = EcsQueryScanMode.Empty;
                scope.Found = false;
                return false;
            }

            if (HasFieldPredicates)
            {
                var ct = _whereComponentTable;
                var evaluators = QueryResolverHelper.ResolveEvaluators(_fieldPredicateBranches[0], ct, 0);
                var plan = PlanBuilder.Instance.BuildPlan(evaluators, ct, AdvancedSelectivityEstimator.Instance);
                var hasMatch = _whereFieldReader.CountScan(plan, plan.OrderedEvaluators, ct, _tx) > 0;
                scope.ScanMode = EcsQueryScanMode.Targeted;
                scope.Found = hasMatch;
                return hasMatch;
            }

            if (_whereFilter != null)
            {
                var hasMatch = Execute().Count > 0;
                scope.ScanMode = EcsQueryScanMode.Broad;
                scope.Found = hasMatch;
                return hasMatch;
            }

            bool found = false;
            CollectMatching((_, _) => found = true, true);
            scope.ScanMode = EcsQueryScanMode.Broad;
            scope.Found = found;
            return found;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Get an enumerator for foreach support. Pre-collects matching entities then iterates.</summary>
    public EcsQueryEnumerator GetEnumerator()
    {
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
        bool hasT2 = HasT2;

        for (int i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];

            // Skip if pending destroy
            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }

            // T1: archetype mask
            if (!mask.Test(entry.Id.ArchetypeId))
            {
                continue;
            }

            // Resolve EnabledBits (may have been overridden by Enable/Disable in same tx)
            ushort enabledBits = entry.EnabledBits;
            if (enableDisable != null && enableDisable.TryGetValue(entry.Id, out ushort overrideBits))
            {
                enabledBits = overrideBits;
            }

            // T2: check enabled/disabled constraints
            if (hasT2)
            {
                var meta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
                if (meta == null || !ResolveT2Masks(meta, out ushort reqEnabled, out ushort reqDisabled))
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
        bool hasT2 = HasT2;

        for (int i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];

            if (destroys != null && destroys.Contains(entry.Id))
            {
                continue;
            }

            if (!mask.Test(entry.Id.ArchetypeId))
            {
                continue;
            }

            ushort enabledBits = entry.EnabledBits;
            if (enableDisable != null && enableDisable.TryGetValue(entry.Id, out ushort overrideBits))
            {
                enabledBits = overrideBits;
            }

            var meta = ArchetypeRegistry.GetMetadata(entry.Id.ArchetypeId);
            if (meta == null)
            {
                continue;
            }

            if (hasT2)
            {
                if (!ResolveT2Masks(meta, out ushort reqEnabled, out ushort reqDisabled))
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
            for (int s = 0; s < meta.ComponentCount; s++)
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
        long txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        bool hasT2 = HasT2;

        for (int archBit = 0; archBit <= mask.MaxId; archBit++)
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

    /// <summary>JIT-specialized variant for full entity data collection (foreach enumeration).</summary>
    private void CollectMatchingFullCore<TMask>(TMask mask, List<(EntityId, ArchetypeMetadata, ushort, EntityLocations)> results) where TMask : struct, IArchetypeMask<TMask>
    {
        long txTsn = _tx.TSN;
        var dbe = _tx.DBE;
        bool hasT2 = HasT2;

        for (int archBit = 0; archBit <= mask.MaxId; archBit++)
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

            var entityId = new EntityId(key, Meta.ArchetypeId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            ushort bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out ushort pendingBits))
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

    private struct BroadScanCollectAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ArchetypeMetadata Meta;
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

            var entityId = new EntityId(key, Meta.ArchetypeId);

            // Skip entities pending destroy in this transaction
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            // Resolve EnabledBits: MVCC overrides first, then pending enable/disable overlay
            ushort bits = EnabledBitsOverrides.ResolveEnabledBits(key, header.EnabledBits, TxTsn);
            if (PendingEnableDisable != null && PendingEnableDisable.TryGetValue(entityId, out ushort pendingBits))
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

        public EntityRef Current => _current;

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
                    _current = new EntityRef(id, meta, engineState, _tx, enabledBits, false);
                    _current.CopyLocationsFrom(in locations, meta.ComponentCount);
                }
                return true;
            }
        }

        public void Dispose() { }
    }
}
#pragma warning restore TYPHON005
