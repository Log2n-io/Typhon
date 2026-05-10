using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-system entity budget for overload Level 2 scope reduction.
/// When the budget is exceeded, remaining entities are deferred to subsequent ticks.
/// </summary>
/// <remarks>Defined for future Level 2 scope reduction. Not enforced yet.</remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct EntityBudget
{
    /// <summary>Maximum entities processed at <see cref="OverloadLevel.Normal"/>.</summary>
    public int Normal;

    /// <summary>Maximum entities processed at <see cref="OverloadLevel.ScopeReduction"/>.</summary>
    public int Reduced;

    /// <summary>Absolute minimum entities — never process fewer than this.</summary>
    public int Minimum;

    /// <summary>Strategy for selecting which entities to defer.</summary>
    public DeferralMode DeferralMode;

    /// <summary>No budget cap — process all entities regardless of overload level.</summary>
    public static readonly EntityBudget Unlimited = new() { Normal = int.MaxValue, Reduced = int.MaxValue, Minimum = int.MaxValue };
}
