using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Strategy for selecting which entities to process when a system's entity budget is exceeded.
/// Remaining entities are deferred to subsequent ticks.
/// </summary>
/// <remarks>Defined for future Level 2 scope reduction. Not enforced yet.</remarks>
[PublicAPI]
public enum DeferralMode : byte
{
    /// <summary>Rotate through the full entity set across ticks. Best for fairness (e.g., AI).</summary>
    RoundRobin = 0,

    /// <summary>Always process highest-priority entities first. Best for combat (nearby enemies).</summary>
    Priority = 1,

    /// <summary>Random subset each tick. Best for cosmetic effects.</summary>
    Random = 2
}
