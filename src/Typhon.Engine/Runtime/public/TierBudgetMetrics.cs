using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Per-tier cost and entity count metrics from the previous tick (issue #234). Exposed on <see cref="TickContext.TierBudgetMetrics"/>
/// so game code (typically the <c>TierAssignment</c> <see cref="CallbackSystem"/>) can adaptively adjust tier boundaries based on
/// actual tick cost vs. budget.
/// </summary>
/// <remarks>
/// <para>Computed at tick end from <see cref="SystemTelemetry.DurationUs"/> aggregated by <see cref="SystemDefinition.TierFilter"/>.
/// Systems targeting multiple tiers (e.g. <see cref="SimTier.Near"/>) contribute equally to each matching tier.</para>
/// <para>On the first tick, all fields are zero (no previous-tick data). Game code should guard against <c>BudgetMs == 0</c>.</para>
/// </remarks>
[PublicAPI]
public struct TierBudgetMetrics
{
    /// <summary>Wall-clock cost in milliseconds of all systems with <see cref="SimTier.Tier0"/> in their tier filter.</summary>
    public float Tier0CostMs;
    /// <summary>Wall-clock cost of Tier 1 systems.</summary>
    public float Tier1CostMs;
    /// <summary>Wall-clock cost of Tier 2 systems.</summary>
    public float Tier2CostMs;
    /// <summary>Wall-clock cost of Tier 3 systems.</summary>
    public float Tier3CostMs;

    /// <summary>Sum of all per-system execution times (including non-tier-filtered systems).</summary>
    public float TotalCostMs;
    /// <summary>Target tick duration: <c>1000f / BaseTickRate</c>.</summary>
    public float BudgetMs;
    /// <summary><c>TotalCostMs / BudgetMs</c>. 1.0 = fully utilized, &gt;1.0 = overrun.</summary>
    public float UtilizationRatio;

    /// <summary>Total entities processed by Tier 0 systems.</summary>
    public int Tier0EntityCount;
    /// <summary>Total entities processed by Tier 1 systems.</summary>
    public int Tier1EntityCount;
    /// <summary>Total entities processed by Tier 2 systems.</summary>
    public int Tier2EntityCount;
    /// <summary>Total entities processed by Tier 3 systems.</summary>
    public int Tier3EntityCount;
}
