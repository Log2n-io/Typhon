using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Current overload response level. Each level adds more aggressive degradation measures on top of the previous.
/// </summary>
[PublicAPI]
public enum OverloadLevel : byte
{
    /// <summary>No overload — all systems run at normal rate.</summary>
    Normal = 0,

    /// <summary>System throttling active — Low-priority systems shed, Normal-priority throttled via <c>ThrottledTickDivisor</c>.</summary>
    SystemThrottling = 1,

    /// <summary>Scope reduction active — per-system entity budgets enforced, deferred entities tracked.</summary>
    ScopeReduction = 2,

    /// <summary>Tick rate modulation (TiDi) — simulation slows by integer multiplier (2x, 3x, 4x, 6x).</summary>
    TickRateModulation = 3,

    /// <summary>Player shedding — last resort. <see cref="TyphonRuntime.OnCriticalOverload"/> callback fires for game-specific handling.</summary>
    PlayerShedding = 4
}
