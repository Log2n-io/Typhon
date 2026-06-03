using System.Reflection;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services.Querying;

/// <summary>
/// Compiles a <see cref="QuerySpecDto"/> into a fully-built <c>EcsQuery&lt;TArchetype&gt;</c> ready to execute against a
/// live <see cref="Transaction"/>. The output is wrapped in <see cref="CompiledQuery"/>; the controller never sees the
/// raw type-erased <see cref="object"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reflection-heavy.</b> The archetype CLR type is only known at runtime (chip-mode picks a string typeName), so the
/// builder chain — <c>tx.Query&lt;T&gt;().With&lt;C1&gt;().WhereField&lt;C2&gt;(expr)…</c> — is invoked via
/// <see cref="MethodInfo.MakeGenericMethod"/> at each stage. The cost is paid once per Run; the EcsQuery itself is
/// regular object code from there.
/// </para>
/// <para>
/// <b>Phase-1 scope constraints</b> (rejected with stable error codes):
/// <list type="bullet">
/// <item>NAVIGATE clauses — parsed by the DSL parser but compiler errors with <c>navigate_not_supported</c> (Phase 3).</item>
/// <item>SPATIAL clauses — compiled to <c>WhereNearby</c> / <c>WhereInAABB</c> / <c>WhereRay</c>. A single clause is
/// allowed (engine attaches one spatial predicate) and it cannot combine with ORDER BY; violations surface as
/// <c>spatial_single_clause_only</c> / <c>spatial_orderby_unsupported</c> / <c>spatial_component_not_indexed</c>.</item>
/// <item>WHERE referencing multiple components — error with <c>multi_component_where_not_supported</c>. Engine's
/// <c>WhereField&lt;T&gt;</c> is single-typed; the compiler split-by-component path lands in Phase 2.</item>
/// <item><c>AT REVISION</c> with kind other than <c>head</c> — <c>unsupported_revision_kind</c> (Phase 2's As-of picker).</item>
/// </list>
/// </para>
/// </remarks>
public static class QuerySpecCompiler
{
    /// <summary>
    /// Compile a parsed spec against a live engine + transaction. The returned <see cref="CompiledQuery"/> wraps the
    /// built <c>EcsQuery</c>; call <see cref="CompiledQuery.Execute"/> to run it or <see cref="CompiledQuery.Estimate"/>
    /// for the cost-chip path.
    /// </summary>
    /// <exception cref="WorkbenchException">Stable error codes per the Phase-1 scope rules above.</exception>
    public static CompiledQuery Compile(QuerySpecDto spec, DatabaseEngine engine, Transaction tx)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(tx);

        // ───── Phase-1 scope gates ───────────────────────────────────────────────────────────────────────────────
        if (spec.Navigate is { Length: > 0 })
        {
            throw new WorkbenchException(400, "navigate_not_supported",
                "NAVIGATE clauses are not supported in Phase 1. They land in Phase 3 alongside cross-archetype joins.");
        }
        if (spec.Spatial is { Length: > 1 })
        {
            throw new WorkbenchException(400, "spatial_single_clause_only",
                "Only one SPATIAL clause is supported per query — the engine attaches a single spatial predicate " +
                "(WhereNearby / WhereInAABB / WhereRay). Run separate queries for multiple regions.");
        }
        if (spec.Spatial is { Length: > 0 } && spec.OrderBy != null)
        {
            throw new WorkbenchException(400, "spatial_orderby_unsupported",
                "SPATIAL cannot be combined with ORDER BY — a spatial scan returns an unordered candidate set " +
                "(the engine's Execute() rejects OrderBy on spatial queries). Drop the ORDER BY.");
        }
        if (spec.Revision != null && !string.Equals(spec.Revision.Kind, "head", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchException(400, "unsupported_revision_kind",
                $"AT REVISION '{spec.Revision.Kind}' is not supported in Phase 1. Only HEAD is available; the full As-of picker lands in Phase 2.");
        }

        // ───── 1. Resolve archetype CLR type ─────────────────────────────────────────────────────────────────────
        if (string.IsNullOrEmpty(spec.Archetype))
        {
            throw new WorkbenchException(400, "invalid_query_syntax",
                "FROM <archetype> is required.");
        }
        var archetypeType = ResolveArchetypeType(spec.Archetype);

        // ───── 2. Construct EcsQuery<TArchetype> via tx.Query<T>() / tx.QueryExact<T>() ──────────────────────────
        // The factories take 3 optional [CallerFilePath/LineNumber/MemberName] parameters; we pass nulls (the engine
        // tolerates these — they're only used for source attribution in trace records).
        var factoryName = spec.Polymorphic ? "Query" : "QueryExact";
        var factory = FindEcsFactoryMethod(tx, factoryName);
        var factoryArgs = new object[factory.GetParameters().Length];        // all-null defaults
        var ecsQuery = factory.MakeGenericMethod(archetypeType).Invoke(tx, factoryArgs);
        var ecsQueryType = ecsQuery.GetType();

        // ───── 3. WITH / WITHOUT / EXCLUDE / ENABLED / DISABLED ─────────────────────────────────────────────────
        ApplyComponentList(ecsQuery, ecsQueryType, "With", spec.With, engine, allowMissingComponent: false);
        ApplyComponentList(ecsQuery, ecsQueryType, "Without", spec.Without, engine, allowMissingComponent: false);
        // Exclude takes an archetype type (different generic constraint), not a component.
        ApplyArchetypeList(ecsQuery, ecsQueryType, "Exclude", spec.Exclude);
        ApplyComponentList(ecsQuery, ecsQueryType, "Enabled", spec.Enabled, engine, allowMissingComponent: false);
        ApplyComponentList(ecsQuery, ecsQueryType, "Disabled", spec.Disabled, engine, allowMissingComponent: false);

        // ───── 4. WHERE — single-component only in Phase 1 ──────────────────────────────────────────────────────
        var projectedComponents = new List<ComponentTable>();
        ComponentTable whereComponentTable = null;
        if (spec.Where != null)
        {
            var component = AssertSingleComponent(spec.Where);
            whereComponentTable = ResolveComponentTable(engine, component);
            ValidateWhereFieldsAreIndexed(spec.Where, whereComponentTable);
            ApplyWhere(ecsQuery, ecsQueryType, spec.Where, whereComponentTable);
            projectedComponents.Add(whereComponentTable);
        }

        // ───── 4.5 SPATIAL — single clause, filter-only (composes with WHERE on the Execute() path) ─────────────────
        // The spatial component is intentionally NOT added to projectedComponents: its [SpatialIndex] field is an AABB
        // struct that doesn't render as a flat column. SPATIAL narrows the candidate set; result columns stay driven by
        // WHERE. (Single-clause and no-ORDER-BY were already enforced by the Phase-1 gates above.)
        if (spec.Spatial is { Length: > 0 })
        {
            ApplySpatial(ecsQuery, ecsQueryType, spec.Spatial[0], engine);
        }

        // ───── 4.6 SELECT — explicit projection (component-level), authoritative when present ────────────────────────
        // The engine query produces only an entity-id set; "projection" is purely a Workbench read concern (the row
        // materializer reads each projected component via EntityRef.ReadRaw). So SELECT never touches the EcsQuery — it
        // only redefines projectedComponents. When present it REPLACES the WHERE-inferred set (result columns = the
        // SELECTed components' fields); when absent, the WHERE default above stands untouched, so no-SELECT behaviour
        // is byte-for-byte unchanged. A component absent from a matched (polymorphic) row's archetype is silently
        // skipped by the materializer's slot lookup; a typo'd name fails fast here via ResolveComponentTable.
        if (spec.Select is { Length: > 0 })
        {
            projectedComponents = new List<ComponentTable>();
            foreach (var name in spec.Select)
            {
                var table = ResolveComponentTable(engine, name);                  // unknown_component on a typo
                if (!projectedComponents.Contains(table))
                {
                    projectedComponents.Add(table);
                }
            }
        }

        // ───── 5. ORDER BY + SKIP + TAKE ─────────────────────────────────────────────────────────────────────────
        var hasOrderBy = false;
        if (spec.OrderBy != null)
        {
            var component = spec.OrderBy.Component;
            var componentTable = ResolveComponentTable(engine, component);
            // OrderBy requires a WHERE on the same component (engine constraint at EcsQuery.cs:481-484) — a FILTER
            // prerequisite, independent of projection. Pre-SELECT this was checked against projectedComponents (which
            // equalled the WHERE component); SELECT decouples projection from WHERE, so we check the WHERE component
            // directly. For Phase 1 we surface this as a clean compile error rather than letting it explode at runtime.
            if (componentTable != whereComponentTable)
            {
                throw new WorkbenchException(400, "invalid_query_syntax",
                    $"ORDER BY {spec.OrderBy.Component}.{spec.OrderBy.Field} requires a matching WHERE on '{spec.OrderBy.Component}'.");
            }
            ValidateFieldIsIndexed(componentTable, spec.OrderBy.Field);
            ApplyOrderBy(ecsQuery, ecsQueryType, componentTable, spec.OrderBy.Field, spec.OrderBy.Descending);
            hasOrderBy = true;
        }
        // SKIP/TAKE: engine's EcsQuery.Skip/Take require an ORDER BY (constraint at EcsQuery.cs:460-471). For unordered
        // queries the service paginates the materialised id list server-side using `spec.Take` / `request.PageSize`,
        // so we only push Skip/Take to the engine when an OrderBy is set (where it benefits from the engine's inline
        // skip/take during the ordered scan).
        if (hasOrderBy)
        {
            if (spec.Skip > 0)
            {
                ecsQueryType.GetMethod("Skip", [typeof(int)])?.Invoke(ecsQuery, [spec.Skip]);
            }
            if (spec.Take > 0)
            {
                ecsQueryType.GetMethod("Take", [typeof(int)])?.Invoke(ecsQuery, [spec.Take]);
            }
        }
        else if (spec.Skip > 0)
        {
            // SKIP without ORDER BY is meaningful only with stable ordering — surface a clear error rather than
            // silently dropping the request.
            throw new WorkbenchException(400, "invalid_query_syntax",
                "SKIP without ORDER BY is not supported — the row order would not be stable across runs. Add an ORDER BY clause.");
        }

        // ───── 6. Snapshot the polymorphic subtree for the cost-chip's archetype-count badge ────────────────────
        var scannedArchetypes = SnapshotScannedArchetypes(archetypeType, spec);

        return new CompiledQuery(
            ecsQuery,
            archetypeType,
            hasOrderBy,
            spec,
            engine,
            projectedComponents,
            scannedArchetypes);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Type resolution
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static Type ResolveArchetypeType(string typeName)
    {
        // Three resolution paths:
        //   "#<digits>" — numeric ArchetypeId (the canonical form chip-mode uses; matches /schema/archetypes display).
        //   "<digits>"  — bare ArchetypeId — accepted as a forgiving alternative.
        //   "<name>"    — CLR Type.Name or Type.FullName (legacy / when the user knows the C# class name).
        if (TryParseArchetypeId(typeName, out var archetypeId))
        {
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                if (meta.ArchetypeId == archetypeId && meta.ArchetypeType != null)
                {
                    return meta.ArchetypeType;
                }
            }
            throw new WorkbenchException(400, "unknown_archetype",
                $"Archetype id #{archetypeId} is not registered. Check against /schema/archetypes.");
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var t = meta.ArchetypeType;
            if (t == null) continue;
            if (string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
                string.Equals(t.FullName, typeName, StringComparison.Ordinal))
            {
                return t;
            }
        }
        throw new WorkbenchException(400, "unknown_archetype",
            $"Archetype '{typeName}' is not registered. Check the FROM clause against /schema/archetypes.");
    }

    /// <summary>
    /// Parse a chip-mode archetype reference as a numeric id. Accepts both canonical <c>"#2001"</c> and bare
    /// <c>"2001"</c> forms. Returns false (and a zero <paramref name="id"/>) for anything else — the caller falls
    /// through to name-based resolution.
    /// </summary>
    private static bool TryParseArchetypeId(string typeName, out ushort id)
    {
        id = 0;
        if (string.IsNullOrEmpty(typeName)) return false;
        var s = typeName[0] == '#' ? typeName.AsSpan(1) : typeName.AsSpan();
        if (s.Length == 0) return false;
        return ushort.TryParse(s, out id);
    }

    private static ComponentTable ResolveComponentTable(DatabaseEngine engine, string typeName)
    {
        foreach (var t in engine.GetAllComponentTables())
        {
            if (string.Equals(t.Definition.Name, typeName, StringComparison.Ordinal))
            {
                return t;
            }
            // Also accept POCOType.Name as a forgiving fallback (chip mode might pass the CLR type name).
            if (t.Definition.POCOType != null && string.Equals(t.Definition.POCOType.Name, typeName, StringComparison.Ordinal))
            {
                return t;
            }
        }
        throw new WorkbenchException(400, "unknown_component",
            $"Component '{typeName}' is not registered. Check against /schema/components.");
    }

    private static MethodInfo FindEcsFactoryMethod(Transaction tx, string name)
    {
        // tx.Query<TArchetype>() / tx.QueryExact<TArchetype>() — both have a `where TArchetype : class` generic
        // constraint. They also take 3 optional [Caller*] parameters; we match by name + generic arity 1 instead of
        // filtering on parameter count.
        var methods = tx.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (var m in methods)
        {
            if (m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1)
            {
                return m;
            }
        }
        throw new InvalidOperationException($"Transaction.{name}<T>() not found — engine API mismatch.");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Stage application (reflection-driven generic method dispatch)
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void ApplyComponentList(object ecsQuery, Type ecsQueryType, string methodName, string[] components,
        DatabaseEngine engine, bool allowMissingComponent)
    {
        if (components == null || components.Length == 0) return;
        for (var i = 0; i < components.Length; i++)
        {
            var name = components[i];
            ComponentTable table;
            try
            {
                table = ResolveComponentTable(engine, name);
            }
            catch (WorkbenchException) when (allowMissingComponent)
            {
                continue;
            }
            var pocoType = table.Definition.POCOType;
            var generic = FindGenericMethod(ecsQueryType, methodName, paramCount: 0);
            var args = new object[generic.GetParameters().Length];           // all-null defaults
            generic.MakeGenericMethod(pocoType).Invoke(ecsQuery, args);
        }
    }

    private static void ApplyArchetypeList(object ecsQuery, Type ecsQueryType, string methodName, string[] archetypeNames)
    {
        if (archetypeNames == null || archetypeNames.Length == 0) return;
        for (var i = 0; i < archetypeNames.Length; i++)
        {
            var type = ResolveArchetypeType(archetypeNames[i]);
            var generic = FindGenericMethod(ecsQueryType, methodName, paramCount: 0);
            var args = new object[generic.GetParameters().Length];           // all-null defaults
            generic.MakeGenericMethod(type).Invoke(ecsQuery, args);
        }
    }

    private static MethodInfo FindGenericMethod(Type type, string name, int paramCount)
    {
        // Match by name + generic arity 1 (single-T methods like With/Without/Exclude/Enabled/Disabled/WhereField).
        // paramCount is interpreted as the count of REQUIRED parameters — engine methods may also carry optional
        // [CallerFilePath/LineNumber/MemberName] strings (3 trailing optional params); we tolerate them here.
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1)
            {
                var p = m.GetParameters();
                var required = p.Length;
                foreach (var prm in p)
                {
                    if (prm.HasDefaultValue) required--;
                }
                if (required == paramCount)
                {
                    return m;
                }
            }
        }
        throw new InvalidOperationException($"Generic method '{name}' (T, {paramCount} required params) not found on {type.Name}.");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // WHERE
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Phase 1 constraint: WHERE may only reference a single component. Walks the AST; throws if more than one distinct
    /// component shows up. Returns the (single) component name.
    /// </summary>
    private static string AssertSingleComponent(PredicateNodeDto root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectComponents(root, seen);
        if (seen.Count == 0)
        {
            throw new WorkbenchException(400, "invalid_query_syntax",
                "WHERE clause has no predicates.");
        }
        if (seen.Count > 1)
        {
            throw new WorkbenchException(400, "multi_component_where_not_supported",
                $"WHERE references multiple components ({string.Join(", ", seen)}). Phase 1 supports WHERE on one component at a time; combine via separate queries or wait for Phase 2's multi-component split.");
        }
        return seen.First();
    }

    private static void CollectComponents(PredicateNodeDto node, HashSet<string> sink)
    {
        if (node == null) return;
        if (node.Kind == "cmp")
        {
            if (!string.IsNullOrEmpty(node.Component))
            {
                sink.Add(node.Component);
            }
            return;
        }
        if (node.Children != null)
        {
            foreach (var c in node.Children)
            {
                CollectComponents(c, sink);
            }
        }
    }

    private static void ValidateWhereFieldsAreIndexed(PredicateNodeDto node, ComponentTable table)
    {
        if (node == null) return;
        if (node.Kind == "cmp")
        {
            ValidateFieldIsIndexed(table, node.Field);
            return;
        }
        if (node.Children != null)
        {
            foreach (var c in node.Children) ValidateWhereFieldsAreIndexed(c, table);
        }
    }

    private static void ValidateFieldIsIndexed(ComponentTable table, string fieldName)
    {
        if (!table.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            throw new WorkbenchException(400, "invalid_field",
                $"Component '{table.Definition.Name}' has no field '{fieldName}'.");
        }
        if (!field.HasIndex)
        {
            throw new WorkbenchException(400, "invalid_field",
                $"Field '{table.Definition.Name}.{fieldName}' is not indexed. Only indexed fields can appear in WHERE / ORDER BY. " +
                "Mark the field with [Index] in the component definition, or pick a different field.");
        }
    }

    private static void ApplyWhere(object ecsQuery, Type ecsQueryType, PredicateNodeDto where, ComponentTable table)
    {
        var componentType = table.Definition.POCOType;
        // Build Expression<Func<T,bool>> via reflection on the helper (generic on T).
        var builder = typeof(ExpressionTreeBuilder).GetMethod(nameof(ExpressionTreeBuilder.BuildPredicate),
            BindingFlags.Public | BindingFlags.Static);
        var lambda = builder.MakeGenericMethod(componentType).Invoke(null, [where]);

        // ecsQuery.WhereField<T>(Expression<Func<T,bool>>) — single-typed; pass through.
        var whereField = FindGenericMethod(ecsQueryType, "WhereField", paramCount: 1);
        var whereArgs = new object[whereField.GetParameters().Length];   // first = lambda, rest = null defaults
        whereArgs[0] = lambda;
        try
        {
            whereField.MakeGenericMethod(componentType).Invoke(ecsQuery, whereArgs);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is InvalidOperationException ex && ex.Message.Contains("DNF clauses"))
        {
            // Engine throws InvalidOperationException when DNF > 16 branches; surface a clean compile error.
            throw new WorkbenchException(400, "dnf_overflow", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // SPATIAL
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Emit the single spatial predicate onto the query via <c>WhereNearby&lt;T&gt;</c> / <c>WhereInAABB&lt;T&gt;</c> /
    /// <c>WhereRay&lt;T&gt;</c> on the spatial component <typeparamref name="T"/>. The component must declare a
    /// <c>[SpatialIndex]</c> field or the engine NREs in Release (its <c>Debug.Assert</c> is compiled out), so we validate
    /// <c>Definition.SpatialField</c> first.
    /// </summary>
    /// <remarks>
    /// <b>Dimensionality-dependent argument packing</b> (see <c>EcsQuery.ExecuteSpatial</c>): the engine reads
    /// <c>_spatialParams</c> by the component's coordinate count. NEARBY (center then radius always at slot 3) and RAY
    /// (origin at 0.., direction at 3.., maxDist at 6) line up with the DSL's 3D-shaped <see cref="SpatialClauseDto.Parameters"/>
    /// for both 2D and 3D, so they pass straight through. AABB is the exception: a 2D component reads
    /// <c>(minX, minY, maxX, maxY)</c> from slots 0..3, so the DSL's <c>[minX,minY,minZ,maxX,maxY,maxZ]</c> must be repacked
    /// (drop Z) — passing the 3D layout to a 2D component inverts the box and silently returns nothing.
    /// </remarks>
    private static void ApplySpatial(object ecsQuery, Type ecsQueryType, SpatialClauseDto clause, DatabaseEngine engine)
    {
        var table = ResolveComponentTable(engine, clause.Component);
        var spatialField = table.Definition.SpatialField;
        if (spatialField == null)
        {
            throw new WorkbenchException(400, "spatial_component_not_indexed",
                $"Component '{table.Definition.Name}' has no [SpatialIndex] field — it cannot be used in a SPATIAL clause. " +
                "Pick a component whose position field is marked [SpatialIndex].");
        }

        var parameters = clause.Parameters ?? [];
        var kind = (clause.Kind ?? string.Empty).ToLowerInvariant();
        string methodName;
        object[] args;
        switch (kind)
        {
            case "nearby":
                RequireSpatialParamCount(clause, parameters, 4);
                methodName = "WhereNearby";                                     // (cx, cy, cz, radius)
                args = BoxDoubles(parameters);
                break;
            case "ray":
                RequireSpatialParamCount(clause, parameters, 7);
                methodName = "WhereRay";                                        // (ox, oy, oz, dx, dy, dz, maxDist)
                args = BoxDoubles(parameters);
                break;
            case "aabb":
                RequireSpatialParamCount(clause, parameters, 6);
                methodName = "WhereInAABB";
                args = Is2DSpatial(spatialField.Type)
                    ? [parameters[0], parameters[1], parameters[3], parameters[4], 0d, 0d]   // 2D engine reads (minX, minY, maxX, maxY)
                    : BoxDoubles(parameters);                                                // 3D: (minX, minY, minZ, maxX, maxY, maxZ)
                break;
            default:
                throw new WorkbenchException(400, "invalid_query_syntax",
                    $"Unknown SPATIAL kind '{clause.Kind}'. Expected nearby, aabb, or ray.");
        }

        var method = FindGenericMethod(ecsQueryType, methodName, args.Length);
        try
        {
            method.MakeGenericMethod(table.Definition.POCOType).Invoke(ecsQuery, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is InvalidOperationException ex)
        {
            // Engine rejects e.g. a query-box tier mismatch or a duplicate spatial predicate — surface as a clean 400.
            throw new WorkbenchException(400, "spatial_error", ex.Message);
        }
    }

    private static void RequireSpatialParamCount(SpatialClauseDto clause, double[] parameters, int expected)
    {
        if (parameters.Length != expected)
        {
            throw new WorkbenchException(400, "invalid_query_syntax",
                $"SPATIAL {clause.Kind} expects {expected} numeric parameters but got {parameters.Length}.");
        }
    }

    private static object[] BoxDoubles(double[] values)
    {
        var args = new object[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            args[i] = values[i];                                               // double → boxed object; Invoke unboxes to the double param
        }
        return args;
    }

    // 2D spatial field types (CoordCount 4): AABB / BSphere in f32 or f64. Anything else is 3D (CoordCount 6).
    private static bool Is2DSpatial(FieldType t) =>
        t is FieldType.AABB2F or FieldType.BSphere2F or FieldType.AABB2D or FieldType.BSphere2D;

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // ORDER BY
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void ApplyOrderBy(object ecsQuery, Type ecsQueryType, ComponentTable table, string fieldName, bool descending)
    {
        var componentType = table.Definition.POCOType;
        var fieldType = ExpressionTreeBuilder.ResolveFieldType(componentType, fieldName);

        var builder = typeof(ExpressionTreeBuilder).GetMethod(nameof(ExpressionTreeBuilder.BuildFieldSelector),
            BindingFlags.Public | BindingFlags.Static);
        var lambda = builder.MakeGenericMethod(componentType, fieldType).Invoke(null, [fieldName]);

        var methodName = descending ? "OrderByFieldDescending" : "OrderByField";
        var orderByMethod = FindGenericMethodByArity(ecsQueryType, methodName, genericArity: 2);
        var orderArgs = new object[orderByMethod.GetParameters().Length];
        orderArgs[0] = lambda;
        orderByMethod.MakeGenericMethod(componentType, fieldType).Invoke(ecsQuery, orderArgs);
    }

    private static MethodInfo FindGenericMethodByArity(Type type, string name, int genericArity)
    {
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == genericArity)
            {
                return m;
            }
        }
        throw new InvalidOperationException($"Generic method '{name}' with arity {genericArity} not found on {type.Name}.");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Archetype subtree snapshot (cost-chip support)
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static List<ushort> SnapshotScannedArchetypes(Type rootArchetypeType, QuerySpecDto spec)
    {
        // Walk ArchetypeRegistry, gather all archetypes whose type derives from rootArchetypeType (polymorphic) OR is
        // exactly rootArchetypeType (exact). Apply EXCLUDE filter. WITH/WITHOUT are too costly to evaluate here (they
        // require component-mask lookup) — the cost estimator approximates without them, and Phase 2's PlanBuilder
        // integration replaces this with the real planner output.
        var exclude = new HashSet<string>(spec.Exclude ?? [], StringComparer.Ordinal);
        var ids = new List<ushort>();
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var t = meta.ArchetypeType;
            if (t == null) continue;
            var match = spec.Polymorphic ? rootArchetypeType.IsAssignableFrom(t) : t == rootArchetypeType;
            if (!match) continue;
            if (exclude.Contains(t.Name) || (t.FullName != null && exclude.Contains(t.FullName))) continue;
            ids.Add(meta.ArchetypeId);
        }
        return ids;
    }
}
