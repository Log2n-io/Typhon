namespace Typhon.Workbench.Dtos.Query;

/// <summary>
/// Plan-only response from <c>POST /api/sessions/{id}/query/plan</c> — powers the live cost chip in the Query Console
/// toolbar (≈ <c>N archetypes / N entities / N MB</c>). Wraps <see cref="Typhon.Engine.Querying.ExecutionPlan"/> cost
/// estimates with Console-only aggregates; the structural plan (evaluator list, scan range) lands in Phase 2 when the
/// plan-drawer view is enabled.
/// </summary>
/// <remarks>
/// <b>Naming deviation:</b> The Query Console design doc (§6.3) refers to this DTO as <c>ExecutionPlanDto</c>. Shipped
/// as <c>QueryPlanDto</c> to match the existing Profiler DTO family (<c>QueryDefinitionDto</c>, <c>QueryExecutionDto</c>,
/// <c>QueryExecutionPhaseDto</c>). One-line edit to the design doc tracked in AC-19.
/// </remarks>
/// <param name="EstimatedTotalEntities">Sum of per-archetype entity estimates across the polymorphic subtree, after
/// <c>With</c>/<c>Without</c>/<c>Exclude</c> masks. Drives the "≈ N entities" badge.</param>
/// <param name="ArchetypesScanned">Count of archetype IDs the planner would visit (post-mask). Drives the
/// "≈ N archetypes" badge.</param>
/// <param name="EstimatedPagesRead">Estimated page reads: <c>entities_touched × component_record_size / page_size</c>,
/// rounded up. Drives the "≈ N MB" badge (UI converts pages→bytes using the engine's page size).</param>
/// <param name="PerEvaluatorEstimates">One entry per <see cref="Typhon.Engine.Querying.FieldEvaluator"/> in the planner's
/// chosen order: the cardinality estimate after that evaluator has filtered. Surfaces the planner's selectivity decisions
/// when the user opens Explain mode. Empty array when the query has no field predicates.</param>
public record QueryPlanDto(
    long EstimatedTotalEntities,
    int ArchetypesScanned,
    long EstimatedPagesRead,
    long[] PerEvaluatorEstimates);

/// <summary>Body of <c>POST /api/sessions/{id}/query/plan</c>: the DSL text whose plan is requested.</summary>
/// <param name="Dsl">The Query Console DSL text. See <c>claude/design/Apps/Workbench/views/query-console.md</c> §5.1
/// for the grammar.</param>
public record QueryPlanRequest(string Dsl);
