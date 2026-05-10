using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Priority level for a published View's subscription deltas. Controls throttling behavior under overload.
/// </summary>
[PublicAPI]
public enum SubscriptionPriority : byte
{
    /// <summary>Always pushed, even under severe overload. Use for player state, critical game data.</summary>
    Critical,

    /// <summary>Default priority. Throttled at overload Level 1+.</summary>
    Normal,

    /// <summary>Low priority. Throttled aggressively (every 2nd/4th tick), paused at Level 4.</summary>
    Low
}
