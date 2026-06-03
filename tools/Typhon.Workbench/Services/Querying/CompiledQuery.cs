using Typhon.Engine;
using Typhon.Workbench.Dtos.Query;

namespace Typhon.Workbench.Services.Querying;

/// <summary>
/// The output of <see cref="QuerySpecCompiler.Compile"/>. Wraps a fully-built <c>EcsQuery&lt;TArchetype&gt;</c> (held as
/// <see cref="object"/> because <c>TArchetype</c> is only known at runtime) plus the metadata needed to (a) execute,
/// (b) materialize result rows, and (c) compute Phase-1 plan estimates without going through <c>PlanBuilder</c>.
/// </summary>
/// <remarks>
/// The wrapped <see cref="EcsQuery"/> is consumed via reflection — the type is built dynamically by the compiler. We
/// don't expose it directly to controller code; <see cref="Execute"/> + <see cref="Estimate"/> are the public surface.
/// </remarks>
public sealed class CompiledQuery
{
    private readonly object _ecsQuery;                  // EcsQuery<TArchetype> — type-erased
    private readonly Type _archetypeType;               // TArchetype concrete type
    private readonly Type _ecsQueryType;                // EcsQuery<TArchetype> concrete type (cached for reflection)
    private readonly bool _hasOrderBy;
    private readonly QuerySpecDto _spec;
    private readonly DatabaseEngine _engine;

    /// <summary>Components whose fields the query projects into the result grid — derived from the WHERE clause. Used
    /// by the row materializer to pick which components to read per entity (the implicit <c>entityId</c> is always
    /// included as the first column).</summary>
    public IReadOnlyList<ComponentTable> ProjectedComponents { get; }

    /// <summary>Archetype IDs in the polymorphic subtree of the FROM archetype, post-Mask. Used by
    /// <see cref="Estimate"/> for the archetype-count badge.</summary>
    public IReadOnlyList<ushort> ScannedArchetypes { get; }

    internal CompiledQuery(
        object ecsQuery,
        Type archetypeType,
        bool hasOrderBy,
        QuerySpecDto spec,
        DatabaseEngine engine,
        IReadOnlyList<ComponentTable> projectedComponents,
        IReadOnlyList<ushort> scannedArchetypes)
    {
        _ecsQuery = ecsQuery;
        _archetypeType = archetypeType;
        _ecsQueryType = ecsQuery.GetType();
        _hasOrderBy = hasOrderBy;
        _spec = spec;
        _engine = engine;
        ProjectedComponents = projectedComponents;
        ScannedArchetypes = scannedArchetypes;
    }

    /// <summary>
    /// Run the query. When <see cref="QuerySpecDto.OrderBy"/> is present (and <c>SKIP</c>/<c>TAKE</c> apply), routes
    /// through <c>EcsQuery.ExecuteOrdered()</c> (returns <see cref="List{T}"/> of <see cref="long"/>); otherwise
    /// <c>EcsQuery.Execute()</c> (returns <see cref="HashSet{T}"/> of <see cref="EntityId"/>).
    /// </summary>
    /// <param name="ct">Cancellation token — passed through to engine where supported; in Phase 1 the engine's scan
    /// loops don't accept a token, so cancellation is effectively a request-abort signal at the boundary.</param>
    /// <returns>The matching entity IDs as <c>long</c> values (the raw packed entity-id bits).</returns>
    public List<long> Execute(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Find Execute / ExecuteOrdered by name. Both carry 3 optional [CallerFilePath/LineNumber/MemberName] params
        // (used for trace source attribution); we pass nulls for all of them.
        var methodName = _hasOrderBy ? "ExecuteOrdered" : "Execute";
        var method = FindParameterizedMethod(_ecsQueryType, methodName);
        var args = new object[method.GetParameters().Length];
        object raw;
        try
        {
            raw = method.Invoke(_ecsQuery, args);
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Reflection wraps engine exceptions in TargetInvocationException. Rethrow the real one (preserving its
            // stack) so callers can catch by concrete type — e.g. QueryConsoleService maps the engine's
            // NotSupportedException (RAY on the cluster tier) to a clean 400. Without this, callers only ever see the
            // opaque reflection wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            throw;      // unreachable — Throw always throws
        }
        return CoerceEntityList(raw);
    }

    private static System.Reflection.MethodInfo FindParameterizedMethod(Type type, string name)
    {
        // Match by name only — Execute/ExecuteOrdered are non-generic, single overload on EcsQuery<T>.
        foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (m.Name == name && !m.IsGenericMethodDefinition)
            {
                return m;
            }
        }
        throw new InvalidOperationException($"{name} not found on EcsQuery — engine version mismatch?");
    }

    /// <summary>
    /// Compute the Phase-1 cost-chip estimate without executing. Walks <see cref="ScannedArchetypes"/> + the per-table
    /// <c>GetArchetypeEntityCount</c> for the entity total, then derives page-read estimates from the projected
    /// components' record sizes. <see cref="QueryPlanDto.PerEvaluatorEstimates"/> is empty in Phase 1 (the full
    /// PlanBuilder integration lands in Phase 2 alongside the plan drawer).
    /// </summary>
    public QueryPlanDto Estimate()
    {
        long totalEntities = 0;
        for (var i = 0; i < ScannedArchetypes.Count; i++)
        {
            totalEntities += _engine.GetArchetypeEntityCount(ScannedArchetypes[i]);
        }

        // Apply a coarse selectivity heuristic from the WHERE clause: each indexed equality predicate divides the
        // count by ~10 (we don't have per-field histogram access at this layer in Phase 1 — Phase 2's full PlanBuilder
        // integration will replace this with the real per-evaluator estimates). At minimum 1 entity if a predicate
        // exists, so the cost chip never reads zero when there's a chance of a match.
        var selectivity = EstimatePredicateSelectivity(_spec.Where);
        var estEntities = (long)Math.Max(selectivity * totalEntities, _spec.Where == null ? totalEntities : Math.Min(1, totalEntities));

        // Page estimate: entities × max(component record size) / engine page size, rounded up. The factor uses the
        // largest projected component as a conservative upper bound (the user might project from several components).
        var maxRecordSize = 0;
        for (var i = 0; i < ProjectedComponents.Count; i++)
        {
            var size = ProjectedComponents[i].Definition.ComponentStorageTotalSize;
            if (size > maxRecordSize) maxRecordSize = size;
        }
        if (maxRecordSize == 0) maxRecordSize = 64;     // sane default when no WHERE
        const int PageSize = 4096;                       // typical engine page; exact value isn't worth a config lookup for the cost chip
        var estPages = (estEntities * maxRecordSize + PageSize - 1) / PageSize;

        return new QueryPlanDto(
            EstimatedTotalEntities: estEntities,
            ArchetypesScanned: ScannedArchetypes.Count,
            EstimatedPagesRead: estPages,
            PerEvaluatorEstimates: []);
    }

    private static List<long> CoerceEntityList(object raw)
    {
        // The engine returns HashSet<EntityId> (Execute) or List<EntityId> (ExecuteOrdered). Workbench has
        // InternalsVisibleTo on the engine (AssemblyInfo.cs:12), so we can directly enumerate the strongly-typed
        // collection. EntityId carries an internal ulong RawValue + a 12-bit ArchetypeId; we keep the raw packed
        // 64-bit value (re-decomposed by EntityId.FromRaw on the materialisation path).
        var result = new List<long>();
        if (raw is HashSet<EntityId> hs)
        {
            result.Capacity = hs.Count;
            foreach (var id in hs)
            {
                result.Add((long)id.RawValue);
            }
            return result;
        }
        if (raw is List<EntityId> list)
        {
            result.Capacity = list.Count;
            for (var i = 0; i < list.Count; i++)
            {
                result.Add((long)list[i].RawValue);
            }
            return result;
        }
        // Defensive fallback: shouldn't happen with the current engine API, but if Execute/ExecuteOrdered ever returns
        // something else we surface a clear diagnostic rather than silently dropping rows.
        throw new InvalidOperationException(
            $"EcsQuery.Execute returned an unexpected type {raw?.GetType().FullName ?? "<null>"}. Expected HashSet<EntityId> or List<EntityId>.");
    }

    private static double EstimatePredicateSelectivity(PredicateNodeDto node)
    {
        if (node == null) return 1.0;
        return node.Kind switch
        {
            "cmp" => node.Op switch
            {
                "==" => 0.1,                            // equality on indexed field — coarse 10% guess
                "!=" => 0.9,
                ">" or "<" or ">=" or "<=" => 0.3,      // range — 30% guess
                _ => 0.5,
            },
            "and" => node.Children.Select(EstimatePredicateSelectivity).Aggregate(1.0, (a, b) => a * b),
            "or" => Math.Min(1.0, node.Children.Select(EstimatePredicateSelectivity).Sum()),
            _ => 1.0,
        };
    }
}
