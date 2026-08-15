using Typhon.Schema.Definition;

namespace SpaceBattle;

/// <summary>A combat ship. Dynamic: it moves every tick, so its clusters migrate and its AABBs churn.</summary>
[Archetype]
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
[Archetype]
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
