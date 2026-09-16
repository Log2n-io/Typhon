using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Operator configuration for engine-owned replication (Subscriptions v2). Distinct from <see cref="SubscriptionServerOptions"/>, which configures the
/// v1 TCP transport — this type governs how much memory replication state may occupy, not how clients connect.
/// </summary>
/// <remarks>
/// <para>
/// This is the one piece of engine memory sized by <i>client</i> behaviour rather than by the database: the watched set grows with connections, observer
/// radii and client-supplied regions, all of which are untrusted input. Left unbounded it is a memory-exhaustion path the application cannot close, which
/// is why the budget is a hard ceiling rather than a hint.
/// </para>
/// <para>
/// <b>An unused subsystem allocates nothing.</b> No sessions means no watched set means no blocks, so the budget costs a non-subscriber exactly zero.
/// Nothing is reserved up front either: the first allocation is a single slab, and the pool grows one slab at a time until the budget binds.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class SubscriptionsOptions
{
    /// <summary>
    /// Ceiling on native memory held by per-cluster replication state blocks. Default: 256 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Size it from a formula, not from this default.</b> A block covers a whole cluster, so the requirement is
    /// <c>96 B × N × (watched clusters)</c>, where <c>N</c> is the archetype's cluster slot count — it is driven by cluster <i>occupancy</i>, not by the
    /// watched entity count alone. An operator sizes this from their own expected concurrent view, exactly as they size the page cache. The engine ships a
    /// rail, not an estimate of anyone's working set; the default matches the page cache's own default as the engine's scale for a significant pool, and is
    /// not derived from any application's workload.
    /// </para>
    /// <para>
    /// It is a ceiling, never a reservation. On exhaustion the pool evicts idle blocks and then degrades sessions (dropping a rate class or shrinking the
    /// near radius); it never allocates beyond the budget on the tick path, never throws there, and never leaves an in-view entity unwatched.
    /// </para>
    /// <para>
    /// <b>Not yet implemented:</b> the design also calls for a warning when utilization crosses 80 % — the resource graph's default health threshold — so an
    /// operator learns the budget binds before sessions degrade. The pool exposes the ratio, but emitting the warning needs a logger it does not yet have,
    /// and lands with the DI wiring that reads this option. Until then the budget binds silently.
    /// </para>
    /// </remarks>
    public long StatePoolBudgetBytes { get; init; } = 256L * 1024 * 1024;
}
