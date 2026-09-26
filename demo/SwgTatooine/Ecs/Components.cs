using System.Runtime.InteropServices;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace SwgTatooine;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Components
//
// ── Why every archetype has its OWN placement / motion / vitals type ───────────────────────────────────────────────
//
// The scheduler derives system ordering from declared component access, and it evaluates a write-write conflict at
// COMPONENT-TYPE granularity — not per archetype. One shared placement component across five archetypes therefore made
// every movement system conflict with every other movement system, and the runtime refused to build the graph at all:
//
//     Systems 'CreatureThink' and 'PlayerThink' both declare Writes<Locomotion> in phase index 1.
//
// Those two systems walk disjoint entity sets and cannot race. The deriver has no way to know that: a system's
// .Input(view) names the archetype it walks, but the conflict test never consults it. So the types are split per
// archetype. That is not a workaround — it is what this engine's access model asks for, and it costs nothing at
// runtime, because the structs are identical in layout and each archetype has its own cluster storage regardless.
//
// ── Storage discipline ────────────────────────────────────────────────────────────────────────────────────────────
//
//   SingleVersion  — everything the simulation regenerates for itself: position, velocity, AI scratch, spawn timers.
//                    In-place writes and, via the archetype's ClusterDurability.Checkpoint, no WAL record per tick. A
//                    crash rolls these back to the last checkpoint, which for a creature's position is exactly the
//                    right amount of durability: a restarted server would re-derive it anyway.
//
//   Versioned      — anything a player would notice losing. Inventory is the only one, and it is the reason the WAL is
//                    in the loop at all rather than switched off.
//
// The split is the point of the workload. A fence whose archetypes all behave identically never exercises the code
// that decides per archetype, and a real game server has never had one durability policy.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// ── Realm keys (Realms G1) ──────────────────────────────────────────────────────────────────────────────────────────
//
// The realm an entity is in — which planet, and later which interior or space. In a component of its OWN rather than
// beside Bounds: a 2D AABB placement stays 16 bytes, the stride the engine's SIMD narrowphase needs, and losing that
// kernel measured ~20 % of a tick here. One type per archetype for the same reason the placements are split: the
// scheduler's write-conflict test is per component type, and TeleportSystem writes the player's.

/// <summary>The realm a building, house or prop is in.</summary>
[Component("Swg.StructureRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct StructureRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public StructureRealm(ushort value) => Value = value;
}

/// <summary>The realm a lair is in.</summary>
[Component("Swg.LairRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct LairRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public LairRealm(ushort value) => Value = value;
}

/// <summary>The realm a city NPC is in.</summary>
[Component("Swg.NpcRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct NpcRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public NpcRealm(ushort value) => Value = value;
}

/// <summary>The realm a creature is in.</summary>
[Component("Swg.CreatureRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public CreatureRealm(ushort value) => Value = value;
}

/// <summary>The realm a starship is in: the space realm (Realms G1c).</summary>
[Component("Swg.ShipRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct ShipRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public ShipRealm(ushort value) => Value = value;
}

/// <summary>The realm a player is in.</summary>
[Component("Swg.PlayerRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public PlayerRealm(ushort value) => Value = value;
}

// ── Placement ───────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Two dimensions, not three, and that is the faithful choice: SWG's own server indexes a planet with a 2D QuadTree over
// X and Z. Y is terrain height, looked up from the heightmap rather than searched, and over a 16 km map its range is
// three orders below the horizontal extent — a third partitioned axis would be one cell deep.

/// <summary>A building, prop, house, factory or harvester. Never moves.</summary>
[Component("Swg.StructurePlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct StructurePlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float halfExtent) => Place.At(ref Bounds, x, z, halfExtent);
}

/// <summary>A creature lair. Never moves, but its contents do.</summary>
[Component("Swg.LairPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct LairPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float halfExtent) => Place.At(ref Bounds, x, z, halfExtent);
}

/// <summary>
/// A starship (Realms G1c): the demo's one 3D, f64 placement — the space realm is a deep grid, and a ship's bounds are an <see cref="AABB3D"/>.
/// </summary>
[Component("Swg.ShipPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct ShipPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB3D Bounds;

    public readonly double X => (Bounds.MinX + Bounds.MaxX) * 0.5;

    public readonly double Y => (Bounds.MinY + Bounds.MaxY) * 0.5;

    public readonly double Z => (Bounds.MinZ + Bounds.MaxZ) * 0.5;

    public readonly double HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5;

    public void SetAt(double x, double y, double z, double h)
    {
        Bounds.MinX = x - h;
        Bounds.MinY = y - h;
        Bounds.MinZ = z - h;
        Bounds.MaxX = x + h;
        Bounds.MaxY = y + h;
        Bounds.MaxZ = z + h;
    }
}

/// <summary>A starship's flight: its velocity per tick and the waypoint it flies to.</summary>
[Component("Swg.ShipMotion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct ShipMotion
{
    [Field] public double VelX;
    [Field] public double VelY;
    [Field] public double VelZ;
    [Field] public double DestX;
    [Field] public double DestY;
    [Field] public double DestZ;
    [Field] public float SpeedMps;
}

/// <summary>A city NPC. Densely packed inside a city, and overwhelmingly stationary.</summary>
[Component("Swg.NpcPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct NpcPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float halfExtent) => Place.At(ref Bounds, x, z, halfExtent);
}

/// <summary>A creature. The bulk of the moving population.</summary>
[Component("Swg.CreaturePlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreaturePlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float halfExtent) => Place.At(ref Bounds, x, z, halfExtent);
}

/// <summary>A player character. The smallest population and the only one anyone queries around.</summary>
[Component("Swg.PlayerPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float halfExtent) => Place.At(ref Bounds, x, z, halfExtent);
}

/// <summary>The one line of geometry the five placement components share.</summary>
internal static class Place
{
    public static void At(ref AABB2F b, float x, float z, float halfExtent)
    {
        b.MinX = x - halfExtent;
        b.MaxX = x + halfExtent;
        b.MinY = z - halfExtent;
        b.MaxY = z + halfExtent;
    }
}

// ── Motion ──────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Split from placement so the system that DECIDES where to go and the system that MOVES do not collide on one component
// and get serialised for it. That inversion — Think writes motion and reads placement, Move writes placement and reads
// motion — is what orders the two phases without either naming the other.

/// <summary>A creature's velocity and destination.</summary>
[Component("Swg.CreatureMotion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureMotion
{
    [Field] public float VelX;
    [Field] public float VelZ;
    [Field] public float SpeedMps;
    [Field] public float DestX;
    [Field] public float DestZ;
}

/// <summary>A player's velocity and destination. Speed changes with the activity: 5 m/s on foot, 12 mounted.</summary>
[Component("Swg.PlayerMotion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerMotion
{
    [Field] public float VelX;
    [Field] public float VelZ;
    [Field] public float SpeedMps;
    [Field] public float DestX;
    [Field] public float DestZ;
}

/// <summary>A city NPC's shuffle.</summary>
[Component("Swg.NpcMotion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct NpcMotion
{
    [Field] public float VelX;
    [Field] public float VelZ;
    [Field] public float SpeedMps;
    [Field] public float DestX;
    [Field] public float DestZ;
}

// ── Vitals ──────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// SWG has no combat round. A weapon's delay is baseSpeed x (1 - speedSkillMod/100), floored at one second, so the
// cooldown below is per-weapon rather than a global tick of the fight.

/// <summary>A creature's hit points and weapon timing.</summary>
[Component("Swg.CreatureVitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureVitals
{
    [Field, Fraction(nameof(MaxHealth), Bits = 8, Name = "hp", Group = "vitals")] public int Health;
    [Field] public int MaxHealth;
    [Field] public int AttackDamage;

    public readonly bool IsAlive => Health > 0;
}

/// <summary>A player's hit points and weapon timing.</summary>
[Component("Swg.PlayerVitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerVitals
{
    // Everyone sees an 8-bit bar; the player alone sees the exact number, in SELF (design/Subscriptions/11 § 2).
    [Field, Fraction(nameof(MaxHealth), Bits = 8, Name = "hp", Group = "vitals"), Owner(CodecKind.Varu, Name = "health")] public int Health;
    [Field] public int MaxHealth;
    [Field] public int AttackCooldown;
    [Field] public int AttackDamage;

    public readonly bool IsAlive => Health > 0;
}

/// <summary>A lair's structural integrity — what a destroy mission is sent to reduce to zero.</summary>
[Component("Swg.LairVitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct LairVitals
{
    [Field] public int Health;
    [Field] public int MaxHealth;

    public readonly bool IsAlive => Health > 0;
}

// ── Behaviour ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A creature's behaviour state — written every time it thinks, and worth nothing after a crash: a restarted server
/// re-derives every field from the lair and the terrain.
/// </summary>
[Component("Swg.CreatureBrain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureBrain
{
    /// <summary>See <see cref="AiMode"/>.</summary>
    [Field, Replicate(CodecKind.U8, Name = "mode")] public int Mode;

    /// <summary>The lair this creature belongs to. Leashing is measured from here.</summary>
    [Field] public float HomeX;

    [Field] public float HomeZ;

    /// <summary>[CORE3] How far from home before turning back. A creature's default flee range is 192 m.</summary>
    [Field] public float LeashRadius;

    /// <summary>[CORE3] Distance at which a hostile is noticed — <c>DEFAULTAGGRORADIUS = 24</c>. Zero for a passive template.</summary>
    [Field, OnEnter(CodecKind.F16, Name = "aggro")] public float AggroRadius;

    /// <summary>The lair that owns this creature, so a kill can decrement its live count.</summary>
    [Field] public EntityId Lair;

}

/// <summary>
/// A creature's scheduling bookkeeping: when it next decides, when it stops walking, when it moves again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split out of <see cref="CreatureBrain"/> because nothing on the wire reads it, and the engine can only see that at COMPONENT granularity.</b>
/// <c>ClusterRef.GetSpan</c> marks a cluster changed when the span is handed out — it returns a mutable span and never observes what the caller does with
/// it — so the engine's only static defence is the set of components the compiled projection reads. <c>CreatureBrain</c> is in that set, because the
/// projection sends <c>Mode</c> and <c>AggroRadius</c>; a countdown living beside them is therefore indistinguishable from a real change every time it is
/// written.
/// </para>
/// <para>
/// <b>This is the realistic schema, not a trick to please a gate.</b> Replicated state and scheduling state have different lifetimes, different readers
/// and different write rates, and a server that mixes them pays for the mixture on every tick of every entity.
/// </para>
/// </remarks>
[Component("Swg.CreatureTimers", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureTimers
{
    /// <summary>Ticks until the next decision, and the respawn countdown while dead. Staggered at spawn so a lair does not think on one tick.</summary>
    [Field] public int ThinkCooldown;

    /// <summary>
    /// The tick a wandering creature stops walking its current leg, after which it stands still until <see cref="RestUntilTick"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An ABSOLUTE tick rather than a countdown, and that is the point of it.</b> A countdown has to be decremented, and decrementing writes on every
    /// tick of a creature's life — which keeps its cluster permanently dirty however still the creature is. A stamp is written once per decision and
    /// merely compared in between.
    /// </para>
    /// <para>
    /// <b>Why a creature rests at all.</b> A wandering creature that walks continuously writes a new position every tick forever, which is one workload
    /// shape among many and happens to be the one no cache can help. Real creatures graze: they amble a few metres and then stand. The duty cycle here is
    /// one leg to four rests — stationary about 80 % of the time.
    /// </para>
    /// </remarks>
    [Field] public long MoveUntilTick;

    /// <summary>The tick a resting creature picks its next leg. See <see cref="MoveUntilTick"/>.</summary>
    [Field] public long RestUntilTick;

    /// <summary>
    /// Ticks until the weapon firing at this creature cycles again. Decremented every tick while engaged, so it lives here rather than in
    /// <see cref="CreatureVitals"/>: beside the health the projection sends, every decrement read as a change to a replicated component.
    /// </summary>
    [Field] public int AttackCooldown;
}

/// <summary>A city NPC's behaviour. Almost always <see cref="AiMode.Idle"/>.</summary>
[Component("Swg.NpcBrain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct NpcBrain
{
    [Field, Replicate(CodecKind.U8, Name = "mode")] public int Mode;
    [Field] public float HomeX;
    [Field] public float HomeZ;
    [Field] public float LeashRadius;
}

/// <summary>A city NPC's scheduling bookkeeping. Split from <see cref="NpcBrain"/> for the reason <see cref="CreatureTimers"/> records.</summary>
[Component("Swg.NpcTimers", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct NpcTimers
{
    /// <summary>The tick a shuffling NPC stops its current leg. See <see cref="CreatureTimers.MoveUntilTick"/> for why these are stamps, not countdowns.</summary>
    [Field] public long MoveUntilTick;

    /// <summary>The tick a standing NPC picks its next leg.</summary>
    [Field] public long RestUntilTick;
}

/// <summary>What an AI agent is currently doing.</summary>
public static class AiMode
{
    /// <summary>Standing still. Vendors, trainers, ambient civilians — most of a planet's NPC population.</summary>
    public const int Idle = 0;

    /// <summary>Wandering inside the leash radius, picking a new destination on arrival.</summary>
    public const int Wander = 1;

    /// <summary>Closing on something it intends to attack.</summary>
    public const int Pursue = 2;

    /// <summary>In weapon range, attacking on the cooldown.</summary>
    public const int Fighting = 3;

    /// <summary>Past the leash, returning home and ignoring everything on the way.</summary>
    public const int Leashing = 4;

    /// <summary>Dead, awaiting the lair's respawn timer.</summary>
    public const int Dead = 5;
}

/// <summary>
/// A player's behaviour. Players are simulated as an activity MIX rather than one loop, because the load a planet
/// carries is dominated by what fraction of its players are moving at all.
/// </summary>
[Component("Swg.PlayerState", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerState
{
    /// <summary>See <see cref="PlayerActivity"/>.</summary>
    [Field, Replicate(CodecKind.U8, Name = "activity")] public int Activity;

    /// <summary>Ticks until this player re-evaluates what it is doing.</summary>
    [Field] public int ActivityTicks;

    /// <summary>Where the current mission camp is, cached so a travel leg does not re-resolve an entity every tick.</summary>
    [Field, Owner(CodecKind.F32, Name = "missionX", Group = "mission")] public float MissionX;

    [Field, Owner(CodecKind.F32, Name = "missionZ", Group = "mission")] public float MissionZ;

    /// <summary>Missions completed — here only to prove the mission loop closes.</summary>
    [Field] public int MissionsCompleted;

    /// <summary>Which city this player calls home. Travel legs start and end at cities far more often than at random points.</summary>
    [Field] public int HomeCity;

    /// <summary>The city whose shuttleport this player is walking to or queued at (#910). Meaningful only while taking a shuttle.</summary>
    [Field] public int ShuttleFrom;

    /// <summary>The city the shuttle takes this player to — or, while <see cref="PlayerActivity.ToPortal"/>, the portal it walks to (reused rather
    /// than a new field: a wider PlayerState would change every run's layout, interiors or not).</summary>
    [Field] public int ShuttleDest;
}

/// <summary>What a simulated player is doing this tick.</summary>
/// <remarks>
/// The mix matters more than the individual behaviours: a planet where 40 % of players are parked in a cantina generates
/// a completely different fence load from one where they are all running across the desert, and pre-CU SWG — whose
/// crafting and entertainer professions were first-class — was much closer to the former.
/// </remarks>
public static class PlayerActivity
{
    /// <summary>In a city. Writes no position, so it costs the fence nothing — and still costs every awareness query that finds it.</summary>
    public const int Idle = 0;

    /// <summary>Running or riding between two points. The behaviour that produces cell crossings.</summary>
    public const int Travelling = 1;

    /// <summary>At a camp or a lair, fighting.</summary>
    public const int Combat = 2;

    /// <summary>Wandering near a point of interest — exploring, surveying, harvesting.</summary>
    public const int Roaming = 3;

    /// <summary>Walking to this city's shuttleport to take a shuttle (#910).</summary>
    public const int ToShuttle = 4;

    /// <summary>Queued at the shuttleport; the Shuttle system boards it while the shuttle is down.</summary>
    public const int AwaitingShuttle = 5;

    /// <summary>Walking to a building's door (Realms G1b). <see cref="PlayerState.ShuttleDest"/> holds the portal, in its city's planet.</summary>
    public const int ToPortal = 6;

    /// <summary>In a building's interior realm; leaves through the same door when the timer runs out.</summary>
    public const int Inside = 7;
}

/// <summary>A creature lair: the object that spawns and owns a population, and what a destroy mission sends a player to break.</summary>
[Component("Swg.Lair", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Lair
{
    /// <summary>Which creature template this lair spawns; indexes <see cref="CreatureTemplates"/>.</summary>
    [Field, OnEnter(CodecKind.U16, Name = "template")] public int CreatureTemplate;

    /// <summary>How many creatures this lair keeps alive.</summary>
    [Field] public int SpawnLimit;

    /// <summary>How many are alive right now.</summary>
    [Field] public int AliveCount;

    /// <summary>Ticks until the next respawn attempt.</summary>
    [Field] public int RespawnCooldown;

    /// <summary>How far from the lair its creatures spawn and wander.</summary>
    [Field] public float SpawnRadius;

    /// <summary>Non-zero if this lair belongs to a destroy mission and dies with it.</summary>
    [Field] public int MissionId;
}

/// <summary>
/// Anything built and placed: a city building, a point-of-interest prop, a player house, a factory, a harvester.
/// </summary>
/// <remarks>
/// One component rather than several, because from the spatial layer's point of view they are the same thing — a box
/// that never moves — and the difference between a cantina and a harvester is a tick rate, which
/// <see cref="TickPeriod"/> carries.
/// </remarks>
[Component("Swg.Structure", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Structure
{
    /// <summary>See <see cref="StructureKind"/>.</summary>
    [Field, OnEnter(CodecKind.U8, Name = "kind")] public int Kind;

    /// <summary>Which city or point of interest this belongs to; -1 for a structure standing alone in the wild.</summary>
    [Field, OnEnter(CodecKind.I16, Name = "region")] public int OwnerRegion;

    /// <summary>
    /// Ticks between updates. A building is 0 and never ticks. SWG's own arithmetic sets the others: manufacturing is
    /// <c>complexity x 8</c> seconds per unit and a harvester's rate is units per minute, so both are tens of seconds to
    /// minutes — hundreds to thousands of ticks here.
    /// </summary>
    [Field] public int TickPeriod;

    /// <summary>Ticks until the next update. Staggered at spawn, so the economy is a trickle rather than a spike.</summary>
    [Field] public int TickCountdown;

    /// <summary>Accumulated output. Proves the slow loop actually runs.</summary>
    [Field] public int Accumulated;
}

/// <summary>The kinds of placed object the world holds.</summary>
public static class StructureKind
{
    public const int Building = 0;
    public const int Terminal = 1;
    public const int Shuttleport = 2;
    public const int PoiProp = 3;
    public const int PlayerHouse = 4;
    public const int Factory = 5;
    public const int Harvester = 6;
    public const int CampObject = 7;
}

/// <summary>
/// The one thing in this world worth a WAL record. Item ownership and the credit balance are transactional — losing
/// thirty seconds of them would be losing a player's stuff — so this is <see cref="StorageMode.Versioned"/> and rides
/// full MVCC and per-commit WAL, on an archetype whose other half has opted out of the WAL entirely.
/// </summary>
/// <remarks>
/// It exists to make the durability split real rather than theoretical. A workload where every archetype has the same
/// policy never exercises the per-archetype decision, and that decision is one the fence actually makes.
/// </remarks>
[Component("Swg.Inventory", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct Inventory
{
    [Field] public long Credits;

    /// <summary>Items carried. Loot from a kill increments it inside a real transaction.</summary>
    [Field] public int ItemCount;

    /// <summary>Total value of what is carried.</summary>
    [Field] public long ItemValue;
}
