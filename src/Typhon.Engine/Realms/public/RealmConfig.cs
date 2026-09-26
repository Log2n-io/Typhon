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

    /// <summary>
    /// How sessions are served in this realm (12-realms § 2.1), or <see langword="null"/> when no session may be in it: entering it throws, and a session
    /// whose followed entity enters it is in no realm. Realm 0 of <c>ConfigureSpatialGrid</c> is served with the subscription options' own cell.
    /// </summary>
    public RealmReplicationConfig Replication { get; init; }

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

        Replication?.Validate(id);
    }
}

/// <summary>
/// How a realm is replicated (12-realms § 2.1): the kind its sessions' profile variants are chosen by, its replication cell, its position width and an
/// opaque tag its clients pick a scene by. Application policy, not persisted identity: the grid is the identity (D-1).
/// </summary>
[PublicAPI]
public sealed class RealmReplicationConfig
{
    /// <summary>The realm's kind, one of <c>SubscriptionsRegistry.RealmKinds</c>; <c>""</c>, the default kind, always exists.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The realm's replication cell, in metres — declared, never derived (L3).</summary>
    public required double CellM { get; init; }

    /// <summary>Position bits per axis on the wire in this realm: 16 or 24 (32 needs a wider block layout — not yet).</summary>
    public int PositionBits { get; init; } = 24;

    /// <summary>Opaque to the engine; travels in the realm's <c>REALM</c> block for the client to choose its scene by.</summary>
    public uint AppTag { get; init; }

    internal void Validate(RealmId id)
    {
        ArgumentNullException.ThrowIfNull(Kind, nameof(Kind));
        if (!double.IsFinite(CellM) || CellM <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(CellM), CellM, $"Realm {id.Value}: the replication cell must be a positive number of metres.");
        }

        if (PositionBits is not (16 or 24))
        {
            throw new ArgumentOutOfRangeException(nameof(PositionBits), PositionBits,
                $"Realm {id.Value}: positions travel at 16 or 24 bits per axis (32 needs a replication block layout that reserves it).");
        }
    }
}
