namespace Typhon.Profiler;

/// <summary>
/// One node in the v7+ <c>ResourceGraphSnapshot</c> table — pre-order tree walk of the engine's <c>ResourceGraph</c> at
/// trace start. The tree is reconstructed by readers via <see cref="ParentId"/>; the root has <see cref="ParentId"/> == -1.
/// </summary>
public sealed class ResourceGraphNodeRecord
{
    /// <summary>Stable resource id (engine-assigned).</summary>
    public long Id { get; init; }

    /// <summary>Display name.</summary>
    public string Name { get; init; }

    /// <summary>Resource type byte (matches <c>ResourceType</c> enum ordinal).</summary>
    public byte Type { get; init; }

    /// <summary>Parent resource id, or -1 for root.</summary>
    public long ParentId { get; init; }

    /// <summary>UTC ticks when the resource was created (informational; not used for ordering).</summary>
    public long CreatedAtUtcTicks { get; init; }

    /// <summary>Exhaustion-policy byte (matches the engine's per-resource policy enum ordinal).</summary>
    public byte ExhaustionPolicy { get; init; }
}
