using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>What a realm does while no session observes it and no application pins it.</summary>
[PublicAPI]
public enum RealmUnobserved : byte
{
    /// <summary>Keep simulating (at <see cref="RealmConfig.UnobservedTickDivisor"/> of the rate). A planet whose AI must go on.</summary>
    Simulate = 0,

    /// <summary>Go dormant after <see cref="RealmConfig.SleepAfterTicks"/> unobserved ticks: no systems, no maintenance. An empty interior.</summary>
    Sleep = 1,
}

/// <summary>
/// A realm's configuration: its spatial grid (its identity — bounds, cell size, depth) and its policy when unobserved. Every load-bearing value is
/// required and explicit; nothing is derived from another realm or from a workload.
/// </summary>
/// <remarks>
/// Registered through <see cref="DatabaseEngine.Realms"/> before <c>InitializeArchetypes</c>. The single-world <c>ConfigureSpatialGrid(config)</c>
/// registers realm 0 with <see cref="SimulatedAlways"/>.
/// </remarks>
[PublicAPI]
public sealed class RealmConfig
{
    /// <summary>The realm's own grid: bounds, cell size, 2D or 3D, and every per-realm spatial knob. Never shared with another realm.</summary>
    public required SpatialGridConfig Grid { get; init; }

    /// <summary>What the realm does while unobserved.</summary>
    public required RealmUnobserved WhenUnobserved { get; init; }

    /// <summary>Unobserved simulation rate divisor: 1 = full rate, N = each cluster once every N system runs. At least 1.</summary>
    public required int UnobservedTickDivisor { get; init; }

    /// <summary>Unobserved ticks before a <see cref="RealmUnobserved.Sleep"/> realm goes dormant — the hysteresis that keeps a door flap from
    /// toggling it. Required (&gt; 0) with <see cref="RealmUnobserved.Sleep"/>, ignored otherwise.</summary>
    public int SleepAfterTicks { get; init; }

    /// <summary>The realm this one belongs to, for event routing only (an interior's planet). <see cref="RealmId.None"/> for a root.</summary>
    public RealmId Parent { get; init; } = RealmId.None;

    /// <summary>A realm simulated at full rate whether observed or not — what a single-world application's realm 0 is.</summary>
    public static RealmConfig SimulatedAlways(SpatialGridConfig grid) =>
        new() { Grid = grid, WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 1 };

    /// <summary>Refuses a configuration whose load-bearing values are missing or out of range. No value is clamped.</summary>
    internal void Validate(RealmId id)
    {
        if (!(Grid.CellSize > 0d) || Grid.CellCount <= 0)
        {
            throw new ArgumentException($"Realm {id.Value}: Grid is not a configured grid (cell size {Grid.CellSize}). Build it with SpatialGridConfig.",
                nameof(Grid));
        }

        if (!Enum.IsDefined(WhenUnobserved))
        {
            throw new ArgumentOutOfRangeException(nameof(WhenUnobserved), WhenUnobserved, $"Realm {id.Value}: unknown unobserved policy.");
        }

        if (UnobservedTickDivisor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(UnobservedTickDivisor), UnobservedTickDivisor,
                $"Realm {id.Value}: UnobservedTickDivisor must be at least 1 (1 = full rate).");
        }

        if (WhenUnobserved == RealmUnobserved.Sleep && SleepAfterTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SleepAfterTicks), SleepAfterTicks,
                $"Realm {id.Value}: a Sleep realm needs SleepAfterTicks > 0 — the unobserved ticks it waits before going dormant.");
        }

        if (Parent == id)
        {
            throw new ArgumentException($"Realm {id.Value} cannot be its own parent.", nameof(Parent));
        }
    }
}
