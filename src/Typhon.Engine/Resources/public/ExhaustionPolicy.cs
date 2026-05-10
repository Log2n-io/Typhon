using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Defines how a component responds when a bounded resource reaches its limit.
/// </summary>
/// <remarks>
/// <para>
/// Exhaustion policies are <b>hardcoded per component</b> based on their semantics — they are not
/// configurable. A cache must be able to evict; a durability buffer must wait; a client-facing
/// limit must fail fast.
/// </para>
/// <para>
/// Some components use multiple policies in sequence (e.g., PageCache tries Evict first,
/// then Wait if all pages are pinned).
/// </para>
/// </remarks>
[PublicAPI]
public enum ExhaustionPolicy
{
    /// <summary>
    /// No policy — used for intermediate/structural nodes that don't own a bounded resource.
    /// </summary>
    /// <remarks>
    /// <para>This is the default value (<c>default(ExhaustionPolicy) == None</c>).</para>
    /// <para>Subsystem grouping nodes (e.g., "Storage", "DataEngine") use this sentinel
    /// because they don't directly manage capacity — their children do.</para>
    /// </remarks>
    None = 0,

    /// <summary>
    /// Throw <see cref="ResourceExhaustedException"/> immediately.
    /// </summary>
    /// <remarks>
    /// <para>Use when:</para>
    /// <list type="bullet">
    /// <item><description>The caller can handle failure gracefully</description></item>
    /// <item><description>Queueing would make the problem worse</description></item>
    /// <item><description>The resource represents edge capacity (client-facing)</description></item>
    /// </list>
    /// <para>Examples: Transaction creation beyond max, query memory allocation beyond limit.</para>
    /// </remarks>
    FailFast = 1,

    /// <summary>
    /// Block caller until resource becomes available. Respects <c>Deadline</c>.
    /// </summary>
    /// <remarks>
    /// <para>Use when:</para>
    /// <list type="bullet">
    /// <item><description>The resource will become available soon</description></item>
    /// <item><description>Blocking is acceptable (not on UI thread, has timeout)</description></item>
    /// <item><description>The alternative is worse (losing durability)</description></item>
    /// </list>
    /// <para>Examples: Page latch acquisition, WAL ring buffer space.</para>
    /// </remarks>
    Wait = 2,

    /// <summary>
    /// Remove least-recently-used entry, retry.
    /// </summary>
    /// <remarks>
    /// <para>Use when:</para>
    /// <list type="bullet">
    /// <item><description>Entries can be recreated (cache semantics)</description></item>
    /// <item><description>Eviction has bounded cost</description></item>
    /// <item><description>The resource is self-healing</description></item>
    /// </list>
    /// <para>Examples: Page cache (evict clean pages first), chunk accessor MRU cache.</para>
    /// </remarks>
    Evict = 3,

    /// <summary>
    /// Continue with reduced performance or functionality.
    /// </summary>
    /// <remarks>
    /// <para>Use when:</para>
    /// <list type="bullet">
    /// <item><description>A fallback exists (slower but correct)</description></item>
    /// <item><description>Failure is worse than degradation</description></item>
    /// <item><description>The situation is temporary</description></item>
    /// </list>
    /// <para>Examples: Transaction pool empty → allocate new, compression buffer unavailable → skip compression.</para>
    /// </remarks>
    Degrade = 4
}
