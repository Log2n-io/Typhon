using Typhon.Schema.Definition;

namespace SpaceBattle;

/// <summary>A combat ship. Dynamic: it moves every tick, so its clusters migrate and its AABBs churn.</summary>
/// <remarks>
/// <c>ClusterDurability.Checkpoint</c>, not the default <c>FenceWal</c>. Ships are the overwhelming majority of the
/// entities here and every one of them writes position, motion and combat state EVERY tick, so at ~20 000 ships and
/// 60 Hz the fence emits WAL records at 100-150 MB/s — for data whose value is measured in frames. Accepting
/// checkpoint-interval loss on a ship's position is the correct trade for a simulation: after a crash the fleet
/// resumes from the last checkpoint, which is indistinguishable from the fleet having been somewhere slightly
/// different a moment earlier.
/// <para>
/// Stations keep <c>FenceWal</c> deliberately. They are few, they change rarely, and their state IS the run — losing
/// which bases a faction still holds is not a rounding error the way a ship's position is.
/// </para>
/// </remarks>
[Archetype(ClusterDurability = ClusterDurability.Checkpoint)]
public partial class Ship : Archetype<Ship>
{
    public static readonly Comp<Pos> Position = Register<Pos>();
    public static readonly Comp<Motion> Motion = Register<Motion>();
    public static readonly Comp<Combat> Combat = Register<Combat>();
    public static readonly Comp<Miner> Miner = Register<Miner>();
}

/// <summary>A spawn station. Never moves — the static half of the static/dynamic story.</summary>
[Archetype]
public partial class Station : Archetype<Station>
{
    public static readonly Comp<Pos> Position = Register<Pos>();
    public static readonly Comp<StationInfo> Info = Register<StationInfo>();
}

/// <summary>A projectile. Fast, short-lived, high churn.</summary>
/// <remarks>
/// <c>ClusterDurability.Checkpoint</c> for the same reason as <see cref="Ship"/>, only more so. A shot writes its
/// position every tick and then ceases to exist within a second or two — it is the shortest-lived state in the
/// simulation, and there is no crash outcome in which the exact position of a bullet mid-flight is worth a WAL record.
/// Under <c>FenceWal</c> the projectile pass was the second-largest log producer here after ships, for data guaranteed
/// to be irrelevant by the time any recovery reads it.
/// <para>
/// The hit it takes on recovery is that a crash loses shots in flight back to the last checkpoint. That is
/// indistinguishable from the guns having fired a moment later.
/// </para>
/// </remarks>
[Archetype(ClusterDurability = ClusterDurability.Checkpoint)]
public partial class Shot : Archetype<Shot>
{
    public static readonly Comp<Pos> Position = Register<Pos>();
    public static readonly Comp<Bullet> Bullet = Register<Bullet>();
}

/// <summary>An asteroid. Drifts slowly; depleted by miners; respawned on a slow timer.</summary>
[Archetype]
public partial class Rock : Archetype<Rock>
{
    public static readonly Comp<Pos> Position = Register<Pos>();
    public static readonly Comp<Asteroid> Asteroid = Register<Asteroid>();
}

/// <summary>A super-power pickup lying in space. Static: it never moves, only despawns or is collected.</summary>
[Archetype]
public partial class Loot : Archetype<Loot>
{
    public static readonly Comp<Pos> Position = Register<Pos>();
    public static readonly Comp<PickupInfo> Info = Register<PickupInfo>();
}
