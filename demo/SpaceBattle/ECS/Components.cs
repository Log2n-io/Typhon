using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace SpaceBattle;

/// <summary>
/// The spatial field. Point-form AABB (Min == Max) — <see cref="ClusterRef{TArch}.WriteSpatial"/> buckets on the
/// AABB centre, so point-form makes centre == position exactly.
/// </summary>
/// <remarks>
/// The <c>[SpatialIndex(margin)]</c> margin is the fat-AABB slack in world units. It is deliberately configurable
/// per archetype here because it is one of the knobs this demo exists to explore.
/// </remarks>
[Component("SpaceBattle.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Pos
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public float X
    {
        readonly get => Bounds.MinX;
        set { Bounds.MinX = value; Bounds.MaxX = value; }
    }

    public float Y
    {
        readonly get => Bounds.MinY;
        set { Bounds.MinY = value; Bounds.MaxY = value; }
    }

    public static Pos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } };
}

/// <summary>Velocity plus the per-ship speed cap, so a scenario can mix fast and slow hulls.</summary>
[Component("SpaceBattle.Motion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Motion
{
    [Field] public float VX;
    [Field] public float VY;
    [Field] public float MaxSpeed;
}

/// <summary>
/// Everything combat-related, in one component so a ship is three components rather than six — fewer components
/// means a wider cluster N, which is itself a variable worth being able to observe.
/// </summary>
[Component("SpaceBattle.Combat", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Combat
{
    // NOTE ON FIELD ORDER — largest first, no padding holes.
    // Typhon packs [Field] members tightly; the CLR pads a Sequential struct to natural alignment. When the two
    // disagree the component reads back shifted (this cost an hour: StationInfo.Faction was returning
    // SpawnCooldown's value). Ordering fields large-to-small makes both layouts identical by construction.
    [Field] public float TargetX;
    [Field] public float TargetY;
    [Field] public short Hp;

    /// <summary>
    /// Damage pool in front of <see cref="Hp"/>, regenerating after a lull. Makes disengaging meaningful and
    /// rewards focused burst over trickle, which plain hit-point inflation does not.
    /// </summary>
    [Field] public short Shield;

    /// <summary>Ticks since the last hit. Gates regeneration and selects the hit-flash colour.</summary>
    [Field] public short CalmTicks;

    /// <summary>Ticks until the weapon may fire again.</summary>
    [Field] public short Cooldown;
    /// <summary>Ticks until the target is re-acquired even if the current one still looks alive.</summary>
    [Field] public short ReacquireIn;
    [Field] public short Damage;

    /// <summary>
    /// Separation direction, each component stored as the unit value x1000. Refreshed on each re-acquisition and
    /// applied every tick in between.
    /// </summary>
    /// <remarks>
    /// Fixed-point in two shorts rather than two floats: it keeps <c>Combat</c> at 32 bytes instead of 36, and the
    /// decode is a divide rather than the trig an angle-plus-magnitude packing would need. A thousandth of a unit
    /// vector is far finer than the steering resolution can use.
    /// </remarks>
    [Field] public short SepX;
    [Field] public short SepY;

    [Field] public byte Faction;
    [Field] public byte Dead;
    [Field] public byte HasTarget;
    [Field] public byte Kind;      // 0 = fighter, 1 = heavy, 2 = miner
    /// <summary>Ticks of red hit-flash remaining. Purely cosmetic.</summary>
    [Field] public byte HitFlash;
    /// <summary>Ticks remaining of "I am under attack". While non-zero a fighter defends instead of hunting miners.</summary>
    [Field] public byte ThreatTicks;
    /// <summary>Ticks the ship is held stationary after firing.</summary>
    [Field] public byte RootTicks;

    /// <summary>
    /// Steering state, bit-packed into what was a pad byte: bit 0 is the orbit direction, bits 1-2 are the last
    /// radial decision (0 hold, 1 approach, 2 retreat).
    /// </summary>
    /// <remarks>
    /// The orbit direction must PERSIST or a ship re-picks it every tick and jitters in place instead of circling.
    /// The last radial decision is only there to count approach/retreat flips — the metric that catches the
    /// boundary-oscillation failure this design is most at risk of.
    /// </remarks>
    [Field] public byte SteerFlags;   // 4+4+2*8+1*8 = 32 = sizeof(Combat)
}

/// <summary>A faction's spawn point. Static — never moves, so its clusters never migrate.</summary>
[Component("SpaceBattle.Station", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct StationInfo
{
    // ⚠ TRAILING PADDING — see the note on Combat. The [Field] set must total a multiple of the struct's natural
    // alignment, or Typhon's tight per-slot stride diverges from the CLR's padded sizeof and every slot after the
    // first reads shifted. Explicit [Field] pad bytes keep the two strides identical.
    [Field] public int SpawnedTotal;
    [Field] public short SpawnCooldown;

    /// <summary>Structural integrity. Regenerates only while disabled, and slowly — a siege is meant to stick.</summary>
    [Field] public short Hp;

    /// <summary>
    /// Absorbs damage before <see cref="Hp"/> and regenerates after a lull. This is the anti-camping mechanism:
    /// a lone harasser can never chip a station down, only a sustained assault can.
    /// </summary>
    [Field] public short Shield;

    /// <summary>Ticks until the station's gun may fire again.</summary>
    [Field] public short Cooldown;

    /// <summary>Ticks since the last hit. Gates shield regeneration, and doubles as the hit-flash timer.</summary>
    [Field] public short CalmTicks;

    [Field] public byte Faction;

    /// <summary>Non-zero once HP reaches 0: stops spawning and shooting until it has rebuilt.</summary>
    [Field] public byte Disabled;  // 4+2*5+1*2 = 16 = sizeof(StationInfo)
}

/// <summary>
/// A projectile. Separate archetype on purpose: high spawn/destroy churn is the harshest test of cluster
/// allocation, drain and the per-cell index, and it is exactly the traffic a real game generates.
/// </summary>
[Component("SpaceBattle.Bullet", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Bullet
{
    // ⚠ TRAILING PADDING — see the note on Combat. The [Field] set must total a multiple of the struct's natural
    // alignment, or Typhon's tight per-slot stride diverges from the CLR's padded sizeof and every slot after the
    // first reads shifted. Explicit [Field] pad bytes keep the two strides identical.
    [Field] public float VX;
    [Field] public float VY;
    [Field] public short Life;
    [Field] public short Damage;
    [Field] public byte Faction;
    [Field] public byte Dead;
    /// <summary>Fired while the faction's weapon-power effect was active: double damage, drawn big and red.</summary>
    [Field] public byte Boosted;

    /// <summary>Fired by "the one", which is an anti-ship weapon: these rounds pass through stations without harming them.</summary>
    /// <remarks>
    /// Carried on the PROJECTILE rather than resolved from the firer, because by the time a round reaches a station the
    /// ship that fired it is elsewhere and may have stood down entirely — the shot has to know its own rules. Occupies
    /// what was a pad byte, so <c>Bullet</c> stays 16 bytes and the tight-vs-CLR layout agreement is unchanged.
    /// </remarks>
    [Field] public byte FromTheOne;   // 4+4+2+2+1+1+1+1 = 16 = sizeof(Bullet)
}

/// <summary>
/// Miner state. Present on every ship (archetypes are fixed component sets) but only meaningful when
/// <see cref="Combat.Kind"/> is <c>KindMiner</c>.
/// </summary>
/// <remarks>Field order is largest-first — see the note on <see cref="Combat"/>.</remarks>
[Component("SpaceBattle.Miner", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Miner
{
    /// <summary>
    /// Entity key of the asteroid being worked. A STABLE identity, unlike a cluster chunk id.
    /// </summary>
    /// <remarks>
    /// This used to be a (chunk, slot) pair, which is a storage location rather than an identity: an asteroid that
    /// drifts across a cell boundary migrates to another cluster and its chunk id changes. Miners then lost their
    /// rock 4 857 times per 8 000 ticks, restarting the approach each time and never docking.
    /// </remarks>
    [Field] public long OreKey;

    /// <summary>The station this miner returns cargo to. Fixed at spawn.</summary>
    [Field] public float HomeX;
    [Field] public float HomeY;
    /// <summary>Last known position of the asteroid being worked, refreshed every tick while held.</summary>
    [Field] public float OreX;
    [Field] public float OreY;
    [Field] public short Cargo;
    [Field] public short CargoMax;

    /// <summary>
    /// Ticks before another ore search is attempted. Its own field: it previously shared storage with the target's
    /// slot index, so the value meant two different things depending on a flag stored elsewhere.
    /// </summary>
    [Field] public short SearchCooldown;

    /// <summary>0 = seeking ore, 1 = docked and mining, 2 = returning home.</summary>
    [Field] public byte Mode;
    [Field] public byte HasOre;    // 8+4*4+2*3+1*2 = 32 = sizeof(Miner), alignment 8 from the long
}

/// <summary>
/// An asteroid: a slowly drifting, finite pile of material. Slow movement still means the cluster AABBs churn and
/// the occasional cell migration happens, so asteroids are not a static archetype.
/// </summary>
[Component("SpaceBattle.Asteroid", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Asteroid
{
    // ⚠ TRAILING PADDING — see the note on Combat. The [Field] set must total a multiple of the struct's natural
    // alignment, or Typhon's tight per-slot stride diverges from the CLR's padded sizeof and every slot after the
    // first reads shifted. Explicit [Field] pad bytes keep the two strides identical.
    [Field] public float VX;
    [Field] public float VY;
    [Field] public int Capacity;
    [Field] public int MaxCapacity;
    [Field] public byte Dead;
    [Field] public byte Pad0;
    [Field] public byte Pad1;
    [Field] public byte Pad2;      // 4+4+4+4+1+3 = 20 = sizeof(Asteroid)
}

/// <summary>
/// A super-power pickup. Won by shooting it: each faction accumulates its own tally of hits and the first to reach
/// <see cref="Config.PickupHitsToWin"/> takes the effect, faction-wide, for a period that depends on the type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-faction tallies, not one shared counter.</b> A single counter with "whoever lands the last hit wins"
/// inverts the mechanic: every hit before the winning one is work donated to a rival, so the optimal play becomes
/// waiting for someone else to do it. Separate tallies make every shot advance only the side that fired it, which
/// is what turns it into a race.
/// </para>
/// <para>
/// Layout: 5 shorts + 2 bytes = 12 bytes packed, alignment 2, sizeof 12 — no padding needed. Four progress slots
/// because <see cref="Config.Factions"/> permits up to four.
/// </para>
/// </remarks>
[Component("SpaceBattle.Pickup", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PickupInfo
{
    /// <summary>Ticks until it despawns uncontested.</summary>
    [Field] public short Life;

    /// <summary>Hits landed by faction 0.</summary>
    [Field] public short Prog0;
    /// <summary>Hits landed by faction 1.</summary>
    [Field] public short Prog1;
    /// <summary>Hits landed by faction 2.</summary>
    [Field] public short Prog2;
    /// <summary>Hits landed by faction 3.</summary>
    [Field] public short Prog3;

    /// <summary>0 = weapon power, 1 = shield, 2 = speed.</summary>
    [Field] public byte Kind;
    [Field] public byte Dead;      // 2*5 + 1*2 = 12 = sizeof(PickupInfo)

    /// <summary>Progress for one faction. Indexed access without an array — the fields are separate for layout.</summary>
    public readonly short Progress(int faction) => faction switch
    {
        0 => Prog0,
        1 => Prog1,
        2 => Prog2,
        _ => Prog3,
    };

    public void AddProgress(int faction, int delta)
    {
        var v = (short)System.Math.Clamp(Progress(faction) + delta, 0, short.MaxValue);
        switch (faction)
        {
            case 0: Prog0 = v; break;
            case 1: Prog1 = v; break;
            case 2: Prog2 = v; break;
            default: Prog3 = v; break;
        }
    }
}
