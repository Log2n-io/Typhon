// unset

using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Centralized attribute name constants for distributed tracing spans.
/// </summary>
/// <remarks>
/// <para>
/// These constants follow OpenTelemetry semantic conventions with the <c>typhon.</c> prefix
/// for Typhon-specific attributes. Using constants ensures consistency across all span
/// instrumentation and enables IDE autocompletion.
/// </para>
/// <para>
/// Example usage:
/// <code>
/// activity?.SetTag(TyphonSpanAttributes.TransactionTsn, tsn);
/// activity?.SetTag(TyphonSpanAttributes.EntityId, entityId);
/// </code>
/// </para>
/// </remarks>
[PublicAPI]
public static class TyphonSpanAttributes
{
    // ═══════════════════════════════════════════════════════════════════════════
    // TRANSACTION ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The Transaction Sequence Number (TSN) identifying the transaction.
    /// </summary>
    public const string TransactionTsn = "typhon.transaction.tsn";

    /// <summary>
    /// The final status of the transaction (e.g., "committed", "rolledback").
    /// </summary>
    public const string TransactionStatus = "typhon.transaction.status";

    /// <summary>
    /// The number of component types touched by the transaction.
    /// </summary>
    public const string TransactionComponentCount = "typhon.transaction.component_count";

    /// <summary>
    /// Whether a concurrency conflict was detected during commit.
    /// </summary>
    public const string TransactionConflictDetected = "typhon.transaction.conflict_detected";

    // ═══════════════════════════════════════════════════════════════════════════
    // ENTITY ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The primary key (entity ID) of the entity being operated on.
    /// </summary>
    public const string EntityId = "typhon.entity.id";

    /// <summary>
    /// The type name of the component being operated on.
    /// </summary>
    public const string ComponentType = "typhon.component.type";

    /// <summary>
    /// The revision number of a component.
    /// </summary>
    public const string ComponentRevision = "typhon.component.revision";

    /// <summary>
    /// Whether a read operation found the requested entity.
    /// </summary>
    public const string ReadFound = "typhon.read.found";

    // ═══════════════════════════════════════════════════════════════════════════
    // INDEX ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The name of the index being operated on.
    /// </summary>
    public const string IndexName = "typhon.index.name";

    /// <summary>
    /// The type of index operation being performed.
    /// </summary>
    public const string IndexOperation = "typhon.index.operation";

    /// <summary>
    /// Indicates a B+Tree node split occurred.
    /// </summary>
    public const string IndexNodeSplit = "typhon.index.node_split";

    /// <summary>
    /// Indicates a B+Tree node merge occurred.
    /// </summary>
    public const string IndexNodeMerge = "typhon.index.node_merge";

    /// <summary>
    /// The depth level in the B+Tree where the operation occurred.
    /// </summary>
    public const string IndexNodeDepth = "typhon.index.node_depth";

    // ═══════════════════════════════════════════════════════════════════════════
    // PAGE CACHE ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The page index (file page ID) being accessed.
    /// </summary>
    public const string PageId = "typhon.page.id";

    /// <summary>
    /// The source of the page data ("cache" or "disk").
    /// </summary>
    public const string PageSource = "typhon.page.source";

    /// <summary>
    /// Whether a page request was served from cache.
    /// </summary>
    public const string CacheHit = "typhon.cache.hit";

    /// <summary>
    /// The number of pages involved in a batch operation.
    /// </summary>
    public const string PageCount = "typhon.page.count";

    // ═══════════════════════════════════════════════════════════════════════════
    // ECS ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The archetype name (e.g., "Factory", "House").</summary>
    public const string EcsArchetype = "typhon.ecs.archetype";

    /// <summary>The component type name (e.g., "Typhon.Test.ECS.Position").</summary>
    public const string EcsComponentType = "typhon.ecs.component_type";

    /// <summary>Number of entities affected by a batch operation.</summary>
    public const string EcsEntityCount = "typhon.ecs.entity_count";

    /// <summary>Number of cascade-deleted children.</summary>
    public const string EcsCascadeCount = "typhon.ecs.cascade_count";

    /// <summary>Number of matching entities returned by a query.</summary>
    public const string EcsQueryResultCount = "typhon.ecs.query.result_count";

    /// <summary>The query scan mode used ("broad" or "targeted").</summary>
    public const string EcsQueryScanMode = "typhon.ecs.query.scan_mode";

    // ── View ─────────────────────────────────────────────────────────────────

    /// <summary>Number of delta entries processed during View.Refresh.</summary>
    public const string ViewDeltaCount = "typhon.view.delta_count";

    /// <summary>Whether the view overflowed and required a full re-query.</summary>
    public const string ViewOverflow = "typhon.view.overflow";
}
