namespace Typhon.Workbench.Dtos.Query;

/// <summary>
/// The Query Console's internal IR — the single source of truth shared by chip mode and DSL mode. Both modes are
/// projections of this shape: chip → DSL is always defined; DSL → chip is always defined when the DSL parses (no escape
/// constructs in the grammar). See <c>claude/design/Apps/Workbench/views/query-console.md</c> §5.3.
/// </summary>
/// <remarks>
/// Phase-1 scope: <c>Spatial</c> and <c>Navigate</c> are parsed and round-tripped but the compiler does not yet emit
/// <c>EcsQuery.WhereNearby/WhereInAABB/WhereRay</c> or <c>EcsQuery.NavigateField</c> calls — they land in Phase 3
/// (NAVIGATE) and a later phase (SPATIAL). The grammar is forward-compatible so users authoring queries today won't
/// silently lose clauses when the compiler catches up.
/// </remarks>
/// <param name="Archetype">FROM clause: the archetype's registered <c>typeName</c> (matches
/// <c>ComponentSchemaDto.TypeName</c>, not CLR <c>FullName</c>).</param>
/// <param name="Polymorphic"><c>true</c> when FROM specifies <c>polymorphic</c> (engine's
/// <see cref="Typhon.Engine.Transactions.Transaction.Query{TArchetype}"/>); <c>false</c> for <c>exact</c>
/// (engine's <c>QueryExact&lt;T&gt;</c>). Default is <c>true</c>.</param>
/// <param name="With">WITH clause: components the entity must have (engine's <c>EcsQuery.With&lt;T&gt;()</c>).</param>
/// <param name="Without">WITHOUT clause: components the entity must NOT have (engine's <c>EcsQuery.Without&lt;T&gt;()</c>).</param>
/// <param name="Exclude">EXCLUDE clause: archetype subtree exclusion (engine's
/// <c>EcsQuery.Exclude&lt;TExcluded&gt;()</c>).</param>
/// <param name="Enabled">ENABLED clause: components whose <c>EnabledBits</c> must be on (max 4 per engine; UI enforces).</param>
/// <param name="Disabled">DISABLED clause: components whose <c>EnabledBits</c> must be off (max 4 per engine).</param>
/// <param name="Where">Root of the WHERE predicate AST (null when WHERE absent). See <see cref="PredicateNodeDto"/>.</param>
/// <param name="Select">SELECT clause: the components whose fields become result columns. When non-empty it is
/// authoritative — projection = these components (the materializer reads each via <c>EntityRef.ReadRaw</c>). When empty,
/// projection falls back to the WHERE component — the implicit Phase-1 default, so no-SELECT behaviour is unchanged.</param>
/// <param name="Spatial">SPATIAL clauses — parsed but compiler-stubbed in Phase 1 (Phase 3+).</param>
/// <param name="Navigate">NAVIGATE clauses — parsed but compiler-stubbed in Phase 1 (Phase 3).</param>
/// <param name="OrderBy">ORDER BY clause (null when absent). Engine requires a matching WHERE on the same component.</param>
/// <param name="Skip">SKIP clause (0 when absent). Engine requires <see cref="OrderBy"/> when non-zero.</param>
/// <param name="Take">TAKE clause. Default 1000 if user's DSL omits it (Console policy, design §4.6).</param>
/// <param name="Revision">AT REVISION clause. Phase 1: HEAD only (other kinds parsed but Compile rejects).</param>
public record QuerySpecDto(
    string Archetype,
    bool Polymorphic,
    string[] With,
    string[] Without,
    string[] Exclude,
    string[] Enabled,
    string[] Disabled,
    PredicateNodeDto Where,
    string[] Select,
    SpatialClauseDto[] Spatial,
    NavigateClauseDto[] Navigate,
    OrderByDto OrderBy,
    int Skip,
    int Take,
    RevisionDto Revision);

/// <summary>
/// One node in the WHERE predicate AST. Discriminated by <see cref="Kind"/> — <c>"and"</c> / <c>"or"</c> /
/// <c>"cmp"</c>. Flat-fields encoding (rather than separate record types) for JSON-friendly cross-language consumption
/// and a single client-side schema entry.
/// </summary>
/// <param name="Kind">Discriminator: <c>"and"</c>, <c>"or"</c>, or <c>"cmp"</c>.</param>
/// <param name="Children">Non-null only when <see cref="Kind"/> is <c>"and"</c> or <c>"or"</c>; sub-predicates in source
/// order.</param>
/// <param name="Component">Non-null only when <see cref="Kind"/> is <c>"cmp"</c>: the component's registered <c>typeName</c>.</param>
/// <param name="Field">Non-null only when <see cref="Kind"/> is <c>"cmp"</c>: the indexed field name on
/// <see cref="Component"/>. Engine rejects non-indexed fields at plan-build time; the chip-mode picker pre-filters.</param>
/// <param name="Op">Non-null only when <see cref="Kind"/> is <c>"cmp"</c>: one of <c>==</c>, <c>!=</c>, <c>&gt;</c>,
/// <c>&lt;</c>, <c>&gt;=</c>, <c>&lt;=</c> (the full set the engine's <c>ExpressionParser</c> recognises).</param>
/// <param name="Value">Non-null only when <see cref="Kind"/> is <c>"cmp"</c>: JSON-native scalar literal (number / bool /
/// string / enum-as-string). The compiler resolves it to the engine's expected CLR type at compile time.</param>
public record PredicateNodeDto(
    string Kind,
    PredicateNodeDto[] Children,
    string Component,
    string Field,
    string Op,
    object Value);

/// <summary>
/// Spatial clause — one entry per <c>SPATIAL</c> stage. Phase 1: parser stores; compiler does not yet emit
/// <c>EcsQuery.WhereNearby/WhereInAABB/WhereRay</c> calls (those land in a later phase per design §13).
/// </summary>
/// <param name="Component">Component with <c>[SpatialIndex]</c> the spatial query targets.</param>
/// <param name="Kind"><c>"nearby"</c> (sphere), <c>"aabb"</c> (axis-aligned box), or <c>"ray"</c>.</param>
/// <param name="Parameters">Kind-dependent: nearby = <c>[cx, cy, cz, r]</c>; aabb = <c>[minX, minY, minZ, maxX, maxY, maxZ]</c>;
/// ray = <c>[ox, oy, oz, dx, dy, dz, maxDist]</c>.</param>
public record SpatialClauseDto(
    string Component,
    string Kind,
    double[] Parameters);

/// <summary>
/// Cross-archetype navigation clause — one entry per <c>NAVIGATE</c> stage. Phase 1: parser stores; compiler does not
/// yet emit <c>EcsQuery.NavigateField&lt;TSource, TTarget&gt;</c> calls (lands in Phase 3 per design §13).
/// </summary>
/// <param name="Field">FK field name on the source component (extracted from <c>NAVIGATE Field -&gt; TargetComp</c>).</param>
/// <param name="TargetComponent">Target archetype's component <c>typeName</c>.</param>
/// <param name="Where">Optional nested WHERE on the target side (null when omitted).</param>
public record NavigateClauseDto(
    string Field,
    string TargetComponent,
    PredicateNodeDto Where);

/// <summary>
/// ORDER BY clause. Engine constraint: the field must be indexed AND there must be a WHERE on the same component
/// (<c>EcsQuery.OrderByField</c> at <c>EcsQuery.cs:481-484</c>). Compiler pre-validates so chip mode can disable invalid
/// combinations before Run.
/// </summary>
/// <param name="Component">Component <c>typeName</c> the ordering field belongs to.</param>
/// <param name="Field">Indexed field on <see cref="Component"/>.</param>
/// <param name="Descending"><c>true</c> for <c>DESC</c>, <c>false</c> for <c>ASC</c> / default.</param>
public record OrderByDto(
    string Component,
    string Field,
    bool Descending);

/// <summary>
/// AT REVISION clause — the MVCC time-travel control. Phase 1: only <see cref="Kind"/> <c>"head"</c> is supported by the
/// compiler; other kinds parse cleanly but <c>QuerySpecCompiler</c> rejects them with <c>InvalidQueryException</c>. Full
/// picker (revision / tick / time) lands in Phase 2 (design §4.5).
/// </summary>
/// <param name="Kind"><c>"head"</c> | <c>"revision"</c> | <c>"tick"</c> | <c>"time"</c>.</param>
/// <param name="Value">For <c>"revision"</c> / <c>"tick"</c>: the numeric target. Otherwise 0.</param>
/// <param name="TimeIso">For <c>"time"</c>: ISO-8601 timestamp. Otherwise null/empty.</param>
public record RevisionDto(
    string Kind,
    long Value,
    string TimeIso);

/// <summary>Body of <c>POST /api/sessions/{id}/query/parse</c>: DSL text to be parsed to a <see cref="QuerySpecDto"/>.</summary>
/// <param name="Dsl">The Query Console DSL text.</param>
public record QueryParseRequest(string Dsl);

/// <summary>
/// Response from <c>POST /api/sessions/{id}/query/parse</c>: round-trip parser output. Used by chip mode when the user
/// switches from DSL mode (rebuild chips from the parsed spec) and by import flows ("paste DSL → load as query"). The
/// parser never throws on user input — diagnostics surface via <see cref="Errors"/> with line/column.
/// </summary>
/// <param name="Spec">Parsed spec. Non-null even when <see cref="Errors"/> is non-empty — represents a best-effort partial
/// parse so chip mode can still surface what was understood.</param>
/// <param name="Errors">Diagnostics with line/column. Empty array on clean parse.</param>
public record QueryParseResponse(
    QuerySpecDto Spec,
    ParseErrorDto[] Errors);

/// <summary>One parse diagnostic. Mirrors Monaco's marker shape so the client can surface it as an inline editor squiggle
/// without translation.</summary>
/// <param name="Line">1-based line in the DSL source.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Message">User-facing message — directly displayable, no stack-trace noise.</param>
public record ParseErrorDto(
    int Line,
    int Column,
    string Message);
