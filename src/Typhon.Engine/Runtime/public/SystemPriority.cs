using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Priority level for system scheduling under overload conditions.
/// Defined here for DAG construction; enforcement deferred to overload management (#201).
/// </summary>
[PublicAPI]
public enum SystemPriority
{
    /// <summary>Every tick, never throttled or shed.</summary>
    Critical = 0,

    /// <summary>Every tick normally; may be throttled under severe load.</summary>
    High = 1,

    /// <summary>Every Nth tick under load (throttledTickDivisor).</summary>
    Normal = 2,

    /// <summary>Shed entirely under load; runs per tickDivisor normally.</summary>
    Low = 3
}
