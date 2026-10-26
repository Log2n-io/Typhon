using System;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace SwgTatooine.Server;

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

/// <summary>
/// Which region a structure belongs to — the city or point of interest it is part of — <b>encoded region + 1</b>, and 0 for one in the wild.
/// </summary>
/// <remarks>
/// <para>
/// <b>A component of its own, and not a field on <see cref="Structure"/>, because of the index.</b> The engine refuses a writable span over any component
/// carrying an <c>[Index]</c> field, since a write through a span never reaches the index (#1187) — and <c>EconomyTick</c> takes exactly that span over
/// <c>Structure</c> to bump a harvester's countdown and output in place. Satisfying the index there would mean routing the demo's measured per-tick write
/// through <c>OpenMut().Set</c> to maintain an index the write cannot affect. Splitting the key out costs four bytes per structure and leaves that path
/// untouched. It is also the shape the engine documents for a group set's key.
/// </para>
/// <para>
/// <b>Why region + 1, which is not cosmetic.</b> A sparse index never files key 0, so 0 has to be the value that means "no group" — which agrees with the
/// standing rule that optional data spells its absence as 0 and never as a sentinel like -1. The field this replaced used -1, against that rule from the day
/// it was written. A reader wanting the region tests <c>Value != 0</c> and then takes <c>Value - 1</c>; the client does the same with the <c>region</c> it is
/// sent. Cities and points of interest share one index space, a POI being <c>Cities.Count + poiIndex</c>, because they are one question ("which region?")
/// and separating them would need a second field to say which kind of index this is.
/// </para>
/// <para>
/// <b>Sparse, because about a tenth of the structures are in a region</b> — ~1 000 city and POI members against the player houses, factories and harvesters
/// scattered over the planet, which are in none. A dense index would file an entry for every one of those under key 0 and never read it.
/// </para>
/// <para>
/// <b>It is the only thing that says which city a shuttleport serves</b>, which is why it is persisted rather than derived: a world reopened from disk has to
/// answer that without the generator. <c>WorldRebuild</c> reads it to rebuild the port table.
/// </para>
/// </remarks>
[Component("Swg.StructureRegion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct StructureRegion
{
    /// <summary>The region + 1, or 0 for a structure in the wild.</summary>
    [Field, Index(AllowMultiple = true, Sparse = true), OnEnter(CodecKind.I16, Name = "region", Saturate = true)] public int Value;

    public StructureRegion(int value) => Value = value;
}

/// <summary>A city's disc as a group set's region: the key its members carry, and the circle their session's reach is measured against.</summary>
/// <remarks>
/// <para>
/// <b>A circle rather than a box, because the reach is measured to the extent's nearest point.</b> A square around a 456 m city reaches 41 % further at its
/// corners than along its axes, so a session would join Mos Eisley's set from places that are plainly outside it. The cities are discs in the source data and
/// a <c>BSphere2F</c> says so.
/// </para>
/// <para>
/// <b>The key is <c>int</c> to match <see cref="StructureRegion"/>'s.</b> Keys compare as 64-bit and the engine refuses a set whose region and member keys
/// differ in signedness, so this is not a free choice. It holds region + 1 exactly as the members do.
/// </para>
/// </remarks>
[Component("Swg.CityArea", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CityArea
{
    /// <summary>The group's key: the city's index + 1, the same value its structures carry.</summary>
    [Field] public int Key;

    /// <summary>The city's disc, in planet coordinates.</summary>
    [Field] public BSphere2F Circle;
}

/// <summary>Where a city region sits, which is the only reason it has a placement at all.</summary>
/// <remarks>
/// <b>A region belongs to its entity's realm only if its archetype is spatial.</b> One without a spatial field has no realm and its regions are realm 0's —
/// which would hand every planet's sessions Tatooine's cities, since the demo spawns the same map on every planet and the keys would collide. The box is the
/// city's disc, so the spatial index answers for it like anything else; nothing queries it today.
/// </remarks>
[Component("Swg.CityPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CityPlacement
{
    /// <summary>The city's bounding box.</summary>
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

/// <summary>The realm a city region is in — which planet's cities these are.</summary>
[Component("Swg.CityRegionRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CityRegionRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public CityRegionRealm(ushort value) => Value = value;
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
// X and Z. Y is terrain height, LOOKED UP from the heightfield rather than searched.
//
// The old wording here said the height range was "three orders below the horizontal extent". It was two when it was
// written and it is now about 2.2 % of it — 360 m over 16 384 — because the relief cap came off on 2026-09-29. The
// argument does not rest on that ratio and never did: what keeps the index 2D is that 360 m is ONE 256 m CELL DEEP, so a
// third partitioned axis would hold one layer of cells and index nothing. Changing that is a decision about the engine's
// indexing, not about scenery.
//
// `Y` below is therefore a plain `[Field]`, never a `[SpatialIndex]`. It is written where the entity is placed or moved
// and read by anything that needs an altitude; it is never integrated, and no query is ordered by it.

/// <summary>
/// The altitude range every placement's wire codec is written against, in metres.
/// </summary>
/// <remarks>
/// The authored planet measures −110.1 … 250.1 m (<c>TerrainField</c>), so this carries about 50 m of margin at each end
/// — enough for the layer tree to be retuned without a protocol change, and tight enough that 16 bits give a 7.3 mm step.
/// There is no <c>Saturate</c> — it applies to integer codecs and <c>quant</c> is not one — so the range has to be right
/// rather than forgiving. <b>TerrainAltitudeChecks asserts the real field fits inside it with 25 m to spare at each
/// end</b>, which is how the margin is kept from quietly disappearing under a retune of the layer tree.
/// </remarks>
public static class Ground
{
    /// <summary>Lowest altitude the wire can carry, metres.</summary>
    public const double MinM = -160d;

    /// <summary>Highest altitude the wire can carry, metres.</summary>
    public const double MaxM = 320d;
}

/// <summary>
/// What every planet placement has in common: where it is, and the ground under it.
/// </summary>
/// <remarks>
/// A constraint rather than a base class, so a generic reader stays unboxed over a blittable struct. It exists for the
/// things that must treat all five placements alike — the altitude checks, and anything that walks the world asking
/// "is this on the ground" — and for nothing in the tick path, which always knows its own archetype.
/// </remarks>
public interface IGroundPlacement
{
    /// <summary>Centre of the placement on the planet's x axis.</summary>
    float X { get; }

    /// <summary>Centre of the placement on the planet's z axis.</summary>
    float Z { get; }

    /// <summary>Altitude in metres. Not indexed.</summary>
    float Y { get; }
}

/// <summary>A building, prop, house, factory or harvester. Never moves.</summary>
[Component("Swg.StructurePlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct StructurePlacement : IGroundPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    /// <summary>Altitude in metres: the ground under the building. <b>Not indexed</b> — see the note above this block.</summary>
    /// <remarks>
    /// On the wire as its own 16-bit quantised field and <b>never inside the position codec</b>: a vertical step folded
    /// into <c>pos</c> would be measured by the motion tolerance as a horizontal jump, and a walk up a cliff would read
    /// as a teleport. Over −160…+320 m the step is 7.3 mm, an order below anything a player can see, and the range
    /// carries 50 m of margin over the planet's own −110…+250.
    /// </remarks>
    [Field, Replicate(CodecKind.Quant, Min = Ground.MinM, Max = Ground.MaxM, Bits = 16, Name = "y")]
    public float Y;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float y, float halfExtent) => Place.At(ref Bounds, ref Y, x, z, y, halfExtent);
    readonly float IGroundPlacement.Y => Y;

}

/// <summary>A creature lair. Never moves, but its contents do.</summary>
[Component("Swg.LairPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct LairPlacement : IGroundPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    /// <summary>Altitude in metres: the ground under the lair. <b>Not indexed</b> — see the note above this block.</summary>
    /// <remarks>
    /// On the wire as its own 16-bit quantised field and <b>never inside the position codec</b>: a vertical step folded
    /// into <c>pos</c> would be measured by the motion tolerance as a horizontal jump, and a walk up a cliff would read
    /// as a teleport. Over −160…+320 m the step is 7.3 mm, an order below anything a player can see, and the range
    /// carries 50 m of margin over the planet's own −110…+250.
    /// </remarks>
    [Field, Replicate(CodecKind.Quant, Min = Ground.MinM, Max = Ground.MaxM, Bits = 16, Name = "y")]
    public float Y;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float y, float halfExtent) => Place.At(ref Bounds, ref Y, x, z, y, halfExtent);
    readonly float IGroundPlacement.Y => Y;

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
public struct NpcPlacement : IGroundPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    /// <summary>Altitude in metres: the ground under the townsperson. <b>Not indexed</b> — see the note above this block.</summary>
    /// <remarks>
    /// On the wire as its own 16-bit quantised field and <b>never inside the position codec</b>: a vertical step folded
    /// into <c>pos</c> would be measured by the motion tolerance as a horizontal jump, and a walk up a cliff would read
    /// as a teleport. Over −160…+320 m the step is 7.3 mm, an order below anything a player can see, and the range
    /// carries 50 m of margin over the planet's own −110…+250.
    /// </remarks>
    [Field, Replicate(CodecKind.Quant, Min = Ground.MinM, Max = Ground.MaxM, Bits = 16, Name = "y")]
    public float Y;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float y, float halfExtent) => Place.At(ref Bounds, ref Y, x, z, y, halfExtent);
    readonly float IGroundPlacement.Y => Y;

}

/// <summary>A creature. The bulk of the moving population.</summary>
[Component("Swg.CreaturePlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreaturePlacement : IGroundPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    /// <summary>Altitude in metres: the ground under the creature, resampled every time it moves. <b>Not indexed</b> — see the note above this block.</summary>
    /// <remarks>
    /// On the wire as its own 16-bit quantised field and <b>never inside the position codec</b>: a vertical step folded
    /// into <c>pos</c> would be measured by the motion tolerance as a horizontal jump, and a walk up a cliff would read
    /// as a teleport. Over −160…+320 m the step is 7.3 mm, an order below anything a player can see, and the range
    /// carries 50 m of margin over the planet's own −110…+250.
    /// </remarks>
    [Field, Replicate(CodecKind.Quant, Min = Ground.MinM, Max = Ground.MaxM, Bits = 16, Name = "y")]
    public float Y;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float y, float halfExtent) => Place.At(ref Bounds, ref Y, x, z, y, halfExtent);
    readonly float IGroundPlacement.Y => Y;

}

/// <summary>A player character. The smallest population and the only one anyone queries around.</summary>
[Component("Swg.PlayerPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerPlacement : IGroundPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    /// <summary>Altitude in metres: the ground under the player, resampled every time they move. <b>Not indexed</b> — see the note above this block.</summary>
    /// <remarks>
    /// On the wire as its own 16-bit quantised field and <b>never inside the position codec</b>: a vertical step folded
    /// into <c>pos</c> would be measured by the motion tolerance as a horizontal jump, and a walk up a cliff would read
    /// as a teleport. Over −160…+320 m the step is 7.3 mm, an order below anything a player can see, and the range
    /// carries 50 m of margin over the planet's own −110…+250.
    /// </remarks>
    [Field, Replicate(CodecKind.Quant, Min = Ground.MinM, Max = Ground.MaxM, Bits = 16, Name = "y")]
    public float Y;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public readonly float HalfExtent => (Bounds.MaxX - Bounds.MinX) * 0.5f;

    public void SetAt(float x, float z, float y, float halfExtent) => Place.At(ref Bounds, ref Y, x, z, y, halfExtent);
    readonly float IGroundPlacement.Y => Y;

}

/// <summary>The one line of geometry the five placement components share.</summary>
internal static class Place
{
    /// <summary>
    /// Places a 2-D bound and the altitude beside it.
    /// </summary>
    /// <remarks>
    /// The altitude is a parameter rather than something a caller may forget, because forgetting it is the bug this
    /// exists to prevent: an entity at <c>Y = 0</c> on a planet with 360 m of relief is an entity the client draws in
    /// the air or underground, and nothing about a zero looks wrong.
    /// </remarks>
    public static void At(ref AABB2F b, ref float groundY, float x, float z, float y, float halfExtent)
    {
        b.MinX = x - halfExtent;
        b.MaxX = x + halfExtent;
        b.MinY = z - halfExtent;
        b.MaxY = z + halfExtent;
        groundY = y;
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

/// <summary>A player's velocity and destination. Speed is the class granted for what it is doing: a walk, a run, a speeder, or the swoop while mounted.</summary>
[Component("Swg.PlayerMotion", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerMotion
{
    [Field] public float VelX;
    [Field] public float VelZ;
    [Field] public float SpeedMps;
    [Field] public float DestX;
    [Field] public float DestZ;

    /// <summary>
    /// What this player's WALK is worth, in metres per second — the figure <c>TrySpeedFor</c> grants for
    /// <see cref="Replication.SpeedClasses.Walk"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner-only, and it exists so the client can predict its own avatar (A14).</b> Prediction integrates the server's own rule,
    /// <c>pos += dir(heading) x speed x dt</c>, and the client sends a speed CLASS rather than a speed — so without this it would not know how fast to start
    /// moving on the first key press of a session.
    /// </para>
    /// <para>
    /// <b>A replicated stat rather than a constant mirrored in the client, and the difference is the failure mode.</b> A mismatched class VALUE is refused by
    /// the server and counted, loudly; a mismatched SPEED produces position drift that grows without bound for as long as the player keeps walking. The
    /// client already shipped one hard-coded figure the server never corrected — a 100 ms tick period — and it cost four separate symptoms.
    /// </para>
    /// <para>
    /// Per player, because that is where this is going: a mount, a buff or a skill modifier changes it for one character and not for the world. Owner-scoped,
    /// so nobody else's session pays a byte for it.
    /// </para>
    /// </remarks>
    [Field, Owner(CodecKind.F32, Name = "walkMps", Group = "move")] public float WalkMps;

    /// <summary>What this player's RUN is worth, in metres per second. See <see cref="WalkMps"/>.</summary>
    [Field, Owner(CodecKind.F32, Name = "runMps", Group = "move")] public float RunMps;

    /// <summary>
    /// What this player's SWOOP is worth, in metres per second, granted only while <see cref="PlayerState.Mounted"/>. See <see cref="WalkMps"/>.
    /// </summary>
    /// <remarks>
    /// <b>Three speeds and not one "current speed", because prediction needs the figure BEFORE the server has been told.</b> The client integrates the gait
    /// the person just selected, in the same frame they selected it; a single replicated current speed would arrive a round trip later, and the predictor
    /// would spend that round trip integrating the previous gait and then being corrected. The boost (<c>MoveDir.Boost</c>) is not replicated: the client
    /// multiplies by the factor it sent, which the server applies as it is or refuses. Owner-scoped, so these cost one session twelve bytes.
    /// </remarks>
    [Field, Owner(CodecKind.F32, Name = "swoopMps", Group = "move")] public float SwoopMps;

    /// <summary>
    /// A one-tick position offset the move phase owes this player, in METRES — the share of a split tick that belonged to the velocity before an intent
    /// changed (A14b). Metres and not metres per second, because it is paid once whatever the tick rate.
    /// </summary>
    [Field] public float BiasX;

    /// <summary>As <see cref="BiasX"/>.</summary>
    [Field] public float BiasZ;

    /// <summary>
    /// The tick <see cref="BiasX"/>/<see cref="BiasZ"/> are owed for. The move phase applies them only when this matches the tick it is running.
    /// </summary>
    /// <remarks>
    /// <b>Stamped rather than cleared, so that <c>PlayerMove</c> stays a READER of this component.</b> Clearing would make the move phase a writer of
    /// <c>PlayerMotion</c> — which the intent path and <c>PlayerThink</c> already write — and three writers of one component in different phases need an
    /// edge to say which wins. A stale stamp costs one comparison to ignore, and the bias a stopped player leaves behind is never read again.
    /// </remarks>
    [Field] public long BiasTick;
}

/// <summary>
/// The way a scripted player is walking round a city's buildings: up to <see cref="Capacity"/> waypoints to its destination, planned by the bots' own city
/// search (<c>SwgTatooine.Navigation.CityPath</c>) and followed by <c>PlayerThink</c> (<c>SimBridge.Routes.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the crowd needed it</b> (Loïc, 2026-10-24): it steered in a straight line, and once it was collided (2026-10-22) a straight line through a
/// building ended at the building — 34 of 35 bodies round one shuttleport stood still, several on exactly the same coordinates, in its corners.
/// </para>
/// <para>
/// <b>Waypoints as mappable words, read through <see cref="Points"/></b>, for the reason <see cref="AccountSecret"/> spells its digest that way: a fixed
/// buffer or an inline array is refused or skipped by the schema, and a skipped field would not survive a reopen while its count did. Not projected: a
/// route is the server's own business, and a write to it reaches no subscriber.
/// </para>
/// <para>108 bytes, under the 112 a spawned component may carry.</para>
/// </remarks>
[Component("Swg.PlayerRoute", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerRoute
{
    /// <summary>How many waypoints a route holds; a longer walk is planned again when these run out.</summary>
    public const int Capacity = 12;

    /// <summary>The destination this route was planned for; a different one means the body was given a new leg, and the route is planned again.</summary>
    [Field] public float GoalX;

    /// <summary>See <see cref="GoalX"/>.</summary>
    [Field] public float GoalZ;

    /// <summary>How many of the waypoints are this route's.</summary>
    [Field] public byte Count;

    /// <summary>The waypoint being walked to; <see cref="Count"/> when the rest of the way is straight.</summary>
    [Field] public byte Next;

    /// <summary>
    /// The city this route was planned through, plus one; 0 for none. A body walking straight plans again the first time it stands in a city other than
    /// this one — a leg that crosses a town, or ends in one.
    /// </summary>
    [Field] public byte City;

    /// <summary>
    /// 1 while <see cref="PlayerState.MissionX"/>/<see cref="PlayerState.MissionZ"/> is a lair this fighter holds as its objective (lair-objectives.md);
    /// 0 for none — a fight-back, a clone, any other decision clears it.
    /// </summary>
    /// <remarks>
    /// <b>In the byte of padding before <see cref="X0"/></b>, so a body is no wider: a field on <see cref="PlayerState"/> would have been four bytes on
    /// every body. Here because the route is the scripted player's own steering state, written by <c>PlayerThink</c> and read nowhere outside the server.
    /// </remarks>
    [Field] public byte Objective;

    /// <summary>Waypoint 0, east.</summary>
    [Field] public float X0;

    /// <summary>Waypoint 0, north.</summary>
    [Field] public float Z0;

    /// <summary>Waypoint 1, east.</summary>
    [Field] public float X1;

    /// <summary>Waypoint 1, north.</summary>
    [Field] public float Z1;

    /// <summary>Waypoint 2, east.</summary>
    [Field] public float X2;

    /// <summary>Waypoint 2, north.</summary>
    [Field] public float Z2;

    /// <summary>Waypoint 3, east.</summary>
    [Field] public float X3;

    /// <summary>Waypoint 3, north.</summary>
    [Field] public float Z3;

    /// <summary>Waypoint 4, east.</summary>
    [Field] public float X4;

    /// <summary>Waypoint 4, north.</summary>
    [Field] public float Z4;

    /// <summary>Waypoint 5, east.</summary>
    [Field] public float X5;

    /// <summary>Waypoint 5, north.</summary>
    [Field] public float Z5;

    /// <summary>Waypoint 6, east.</summary>
    [Field] public float X6;

    /// <summary>Waypoint 6, north.</summary>
    [Field] public float Z6;

    /// <summary>Waypoint 7, east.</summary>
    [Field] public float X7;

    /// <summary>Waypoint 7, north.</summary>
    [Field] public float Z7;

    /// <summary>Waypoint 8, east.</summary>
    [Field] public float X8;

    /// <summary>Waypoint 8, north.</summary>
    [Field] public float Z8;

    /// <summary>Waypoint 9, east.</summary>
    [Field] public float X9;

    /// <summary>Waypoint 9, north.</summary>
    [Field] public float Z9;

    /// <summary>Waypoint 10, east.</summary>
    [Field] public float X10;

    /// <summary>Waypoint 10, north.</summary>
    [Field] public float Z10;

    /// <summary>Waypoint 11, east.</summary>
    [Field] public float X11;

    /// <summary>Waypoint 11, north.</summary>
    [Field] public float Z11;

    /// <summary>The waypoints as (x, z) pairs, <see cref="Capacity"/> of them; writing through the span writes the component.</summary>
    public Span<float> Points() => MemoryMarshal.CreateSpan(ref X0, Capacity * 2);
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

/// <summary>How a player at no health is down (<see cref="PlayerVitals.Down"/>).</summary>
public static class Downed
{
    /// <summary>On its feet.</summary>
    public const int Up = 0;

    /// <summary>Knocked out: lies where it fell, is nobody's target, and gets up with a fifth of its health (<see cref="TatooineData.KnockOutSec"/>).</summary>
    public const int KnockedOut = 1;

    /// <summary>Dead: lies where it fell, then wakes as a clone outside the nearest city's shuttleport, whole (<see cref="TatooineData.CloneDelaySec"/>).</summary>
    public const int Dead = 2;
}

/// <summary>A player's hit points and weapon timing.</summary>
[Component("Swg.PlayerVitals", 3, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerVitals
{
    // Everyone sees an 8-bit bar; the player alone sees the exact number, in SELF (design/Subscriptions/11 § 2).
    [Field, Fraction(nameof(MaxHealth), Bits = 8, Name = "hp", Group = "vitals"), Owner(CodecKind.Varu, Name = "health", Saturate = true)] public int Health;
    [Field] public int MaxHealth;
    [Field] public int AttackCooldown;
    [Field] public int AttackDamage;

    /// <summary>
    /// Whether the player is down at no health, and how (<see cref="Downed"/>): knocked out or dead, even odds (Loïc, 2026-10-24). Replicated, so every client
    /// plays the fall — the death, or the knock-out it gets up from.
    /// </summary>
    [Field, Replicate(CodecKind.U8, Name = "down", Saturate = true)] public int Down;

    /// <summary>The tick a downed player gets up, or wakes as a clone; 0 on its feet. The player alone sees it, for its countdown.</summary>
    [Field, Owner(CodecKind.Varu, Name = "upTick", Saturate = true)] public int UpTick;

    /// <summary>The tick a blow last landed on this player, 0 for never: it heals only once nothing has hit it for <see cref="TatooineData.RegenQuietSec"/>.</summary>
    [Field] public int LastHitTick;

    public readonly bool IsAlive => Health > 0;
}

/// <summary>A lair's structural integrity — what a destroy mission is sent to reduce to zero.</summary>
[Component("Swg.LairVitals", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct LairVitals
{
    /// <summary>Its integrity, out of <see cref="MaxHealth"/>: replicated as an 8-bit bar, which every client draws over the lair.</summary>
    [Field, Fraction(nameof(MaxHealth), Bits = 8, Name = "hp", Group = "vitals")] public int Health;
    [Field] public int MaxHealth;

    public readonly bool IsAlive => Health > 0;
}

// ── Behaviour ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A creature's behaviour state — written every time it thinks, and worth nothing after a crash: a restarted server
/// re-derives every field from the lair and the terrain.
/// </summary>
[Component("Swg.CreatureBrain", 3, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureBrain
{
    /// <summary>See <see cref="AiMode"/>.</summary>
    [Field, Replicate(CodecKind.U8, Name = "mode", Saturate = true)] public int Mode;

    /// <summary>The lair this creature belongs to. Leashing is measured from here.</summary>
    [Field] public float HomeX;

    [Field] public float HomeZ;

    /// <summary>[CORE3] How far from home before turning back. A creature's default flee range is 192 m.</summary>
    [Field] public float LeashRadius;

    /// <summary>[CORE3] Distance at which a hostile is noticed — <c>DEFAULTAGGRORADIUS = 24</c>. Zero for a passive template.</summary>
    [Field, OnEnter(CodecKind.F16, Name = "aggro")] public float AggroRadius;

    /// <summary>The player this creature is fighting, or <c>EntityId.Null</c> when it is not fighting anybody (SWG-02).</summary>
    /// <remarks>
    /// <b>Replicated as an entity reference</b>, the netId it has in each viewer's own view (0 when it is not in that view): what lets a client draw the
    /// health bar of every creature fighting IT, and nobody else's.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>An entity, not a point, and that is the whole of what SWG-02 adds to the creature's side of a fight.</b> Pursuit used to steer at
    /// <c>CreatureMotion.DestX/DestZ</c> — the position a player occupied on the tick the aggro query found it — and never refreshed it, so a pursuer walked
    /// to a place its target had left and parked there for the rest of its life (gap G6). Holding the identity is what lets the decision re-read the target's
    /// current position, notice it has gone beyond <see cref="TatooineData.MaxChaseRangeM"/>, and give up.
    /// </para>
    /// <para>
    /// It costs eight bytes on the largest population in the world, taking <c>CreatureBrain</c> from 40 to 48. That is a real cost and it is measured rather
    /// than waved away: the alternative — keeping it in <c>CreatureTimers</c>, which is already written every tick and reaches no subscriber — was rejected
    /// because who a creature is fighting is behaviour a client renders, not scheduling.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b><c>EntityLink&lt;Player&gt;</c> and not a bare <see cref="EntityId"/>:</b> the only writer is the aggro query, which is
    /// <c>ClusterSpatialQuery&lt;Player&gt;</c>, so the target is a player by construction. The typed link says so in the schema, which makes the field
    /// indexable and foreign-key-checkable; the bare id did not and could not.
    /// </remarks>
    [Field, Replicate(CodecKind.EntityRef, Name = "target")] public EntityLink<Player> Target;

    /// <summary>Which <see cref="CreatureTemplates"/> entry this creature was spawned from — a rat, a ninja, a mission defender (S0-5).</summary>
    /// <remarks>
    /// <b>A creature that does not carry its template cannot be reasoned about after it is spawned.</b> Its stats were baked from the template at spawn and
    /// the template then thrown away, so nothing downstream — a census, a loot table, a damage model, a client choosing a mesh — can ask what kind of
    /// creature this is. Every one of those had to infer it, and the only inference available was a stat: <c>AggroRadius &gt; 0</c> for "aggressive", which
    /// conflates a passive template with an aggressive one whose radius happens to be scaled to zero.
    /// <para>
    /// <b>On the wire once, on enter</b>: it is immutable per entity, and it is what the client chooses the creature's body by — with <see cref="Skin"/>,
    /// through the table the server publishes (<see cref="CreatureTemplates.Bodies"/>).
    /// </para>
    /// </remarks>
    [Field, OnEnter(CodecKind.U8, Name = "template", Saturate = true)] public byte Template;

    /// <summary>
    /// Which of its template's <see cref="CreatureTemplates.Bodies"/> this creature wears: a ninja hideout mixes three outfits. Drawn at spawn, never
    /// changed, on the wire once.
    /// </summary>
    /// <remarks>Beside <see cref="Template"/> in the padding <see cref="AggroRadius"/> leaves before the 8-byte-aligned <see cref="Target"/>.</remarks>
    [Field, OnEnter(CodecKind.U8, Name = "skin", Saturate = true)] public byte Skin;

    /// <summary>
    /// The creature's content level, 1-50: its lair's (<see cref="Lair.Level"/>), set at spawn and never changed. Its health and its hit are
    /// <c>ContentLevels</c>' of it; on the wire once, on enter.
    /// </summary>
    /// <remarks>In the brain rather than in <see cref="CreatureVitals"/>: both combat directions already read this span, and it fits the padding before
    /// <see cref="Target"/>, where vitals would grow from 12 to 16 bytes.</remarks>
    [Field, OnEnter(CodecKind.U8, Name = "level", Saturate = true)] public byte Level;
}

/// <summary>The lair a creature belongs to: written once, at spawn, and never again.</summary>
/// <remarks>
/// <para>
/// <b>Its own component because it carries an index, and an indexed component may not be written through a span</b> (<c>ClusterRef.GetSpan</c> refuses it,
/// #1187). <see cref="CreatureBrain"/> is written through spans on every decision; this is written by nobody after the spawn, so the index costs an insert
/// at birth and a removal at death and nothing in between.
/// </para>
/// <para>
/// <b><c>OnParentDelete = Delete</c> is what makes a lair transient</b> (Loïc, 2026-10-24: "a lair is a transient entity, we spawn one, then it gets
/// destroyed"). Destroying the lair takes every creature it owns with it — the engine's cascade through this foreign key, in the transaction that destroys
/// the lair, rather than a list of children the simulation would have to keep and a restart would have to rebuild.
/// </para>
/// </remarks>
[Component("Swg.CreatureHome", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureHome
{
    [Field]
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<CreatureLair> Lair;
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
[Component("Swg.CreatureTimers", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CreatureTimers
{
    /// <summary>Ticks until the next decision, and the corpse's countdown while dead. Staggered at spawn so a lair does not think on one tick.</summary>
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
    [Field, Replicate(CodecKind.U8, Name = "mode", Saturate = true)] public int Mode;
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

    /// <summary>Dead: a corpse for <c>SimConfig.CorpseSeconds</c>, then destroyed. Its lair spawns a replacement.</summary>
    public const int Dead = 5;
}

/// <summary>
/// A player's behaviour. Players are simulated as an activity MIX rather than one loop, because the load a planet
/// carries is dominated by what fraction of its players are moving at all.
/// </summary>
[Component("Swg.PlayerState", 5, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerState
{
    /// <summary>See <see cref="PlayerActivity"/>.</summary>
    [Field, Replicate(CodecKind.U8, Name = "activity", Saturate = true)] public int Activity;

    /// <summary>Ticks until this player re-evaluates what it is doing.</summary>
    [Field] public int ActivityTicks;

    /// <summary>
    /// Where the current mission camp is, cached so a travel leg does not re-resolve an entity every tick. The scripted crowd's; not replicated.
    /// </summary>
    [Field] public float MissionX;

    [Field] public float MissionZ;

    /// <summary>
    /// Bumped whenever the missions this body's character holds change — taken, cancelled, completed, or a lair drawn somewhere new (missions.md § 4).
    /// </summary>
    /// <remarks>
    /// <b>A revision, not the journal.</b> The client re-reads <c>GET /swg/missions</c> when it moves. Three journal slots on the body were 48 bytes,
    /// which took a <c>Player</c> page from 24 bodies to 21 for 1 600 scripted bodies that never hold one; four bytes keep 24 (<c>SelectClusterSize</c>,
    /// 2026-10-25).
    /// </remarks>
    [Field, Owner(CodecKind.Varu, Name = "journal", Group = "mission", Saturate = true)] public int JournalRev;

    /// <summary>Bumped whenever this body's character's wallet moves — a grant, a reward, a tip given or received (economy-foundation.md § 2).</summary>
    /// <remarks>
    /// <b>A revision, the journal's pattern</b>: the client re-reads <c>GET /swg/wallet</c> — the balance and the recent rows — when it moves. The balance
    /// itself is a <c>long</c>, which a projected field cannot carry past 2³² (EN-A); the revision can, and costs the body four bytes.
    /// </remarks>
    [Field, Owner(CodecKind.Varu, Name = "wallet", Group = "wallet", Saturate = true)] public int WalletRev;

    /// <summary>
    /// Bumped whenever this body's character's inventory moves — a drop, a cache, a drop lost to a full inventory (inventory-loot.md § 3.6). Revision 5.
    /// </summary>
    /// <remarks>The wallet's pattern: the client re-reads <c>GET /swg/inventory</c> when it moves; four bytes keep a <c>Player</c> page at 24 bodies.</remarks>
    [Field, Owner(CodecKind.Varu, Name = "items", Group = "items", Saturate = true)] public int ItemsRev;

    /// <summary>Missions completed — here only to prove the mission loop closes.</summary>
    [Field] public int MissionsCompleted;

    /// <summary>Which city this player calls home. Travel legs start and end at cities far more often than at random points.</summary>
    [Field] public int HomeCity;

    /// <summary>The city whose shuttleport this player is walking to or queued at (#910) — or, while <see cref="PlayerActivity.LeavingRoom"/>, the
    /// waypoint of the room's route it is walking to.</summary>
    [Field] public int ShuttleFrom;

    /// <summary>The city the shuttle takes this player to — or, while walking to, through or out of a door, the realm of the ROOM that door belongs to
    /// (reused rather than a new field: a wider PlayerState would change every run's layout, interiors or not). The room and not the door's row: this is
    /// persisted, and rows are numbered at open in an order nothing keeps across a reopen.</summary>
    [Field] public int ShuttleDest;

    /// <summary>
    /// Whether the player is riding a swoop: 1, or 0 on foot. Replicated, so everyone watching draws the swoop under the rider.
    /// </summary>
    /// <remarks>
    /// The entitlement <c>TrySpeedFor</c> reads for <see cref="Replication.SpeedClasses.Swoop"/>: a client cannot ride at a swoop's speed without the server
    /// having put it on one (<c>SetMount</c>). A door or a shuttle puts it back on foot — a swoop does not fit through a 0.8 m door, and nobody flies with one.
    /// </remarks>
    [Field, Replicate(CodecKind.U8, Name = "mounted", Saturate = true)] public int Mounted;

    /// <summary>
    /// The combat stance a person put this player in (<c>SetStance</c>, the client's action bar 2–4): <c>Stances.None</c> idle, or what it fights with.
    /// Replicated, so everyone watching draws the weapon in its hand and the weapon's attack on each hit.
    /// </summary>
    /// <remarks>
    /// <b>The gate on a possessed player's shots</b>, and the reach and cadence they have (<see cref="TatooineData.StanceReachM"/>,
    /// <see cref="TatooineData.StanceDelaySec"/>): a player with a target and no stance does not fight. Mounting, boarding and a door put it back to idle.
    /// The scripted crowd never holds one; its <see cref="Activity"/> decides its fights.
    /// </remarks>
    [Field, Replicate(CodecKind.U8, Name = "stance", Saturate = true)] public int Stance;
}

/// <summary>
/// Where a player last asked what fixed thing was near it, and how far it may walk from there before the answer can have
/// changed (the static neighbourhood).
/// </summary>
/// <remarks>
/// <para>
/// <b>A disc, not a cached list.</b> <see cref="ClearM"/> is the distance from <see cref="AnchorX"/>/<see cref="AnchorZ"/>
/// to the nearest blocking surface the last query found, so while the body stays inside it <i>nothing fixed can be
/// touching it</i> — there is no test to run and no query to make. Only a body that leaves the disc pays, and the query it
/// pays for hands it the next disc. Structures are sparse even in the densest city, so a disc is wide and a body crosses
/// one rarely; a body pressed against a wall gets a disc of zero and refreshes every tick, which is correct and is a
/// handful of bodies.
/// </para>
/// <para>
/// <b>Three floats rather than the neighbours themselves.</b> A list would have to be sized for the worst case and kept
/// per body, in a column every walk of the archetype then drags through cache, to answer a question whose answer is
/// almost always "nothing". The neighbours are transient: the query hands them over, the step resolves against them in
/// the same call, and they are discarded.
/// </para>
/// <para>
/// <b>It is self-correcting for teleports.</b> A shuttle, a clone and a portal crossing move a body without walking, which
/// is the case a leash counted in ticks gets wrong; a disc cannot, because the test is whether the new position is inside
/// the old one. <see cref="Realm"/> is carried for the one case geometry alone would miss: an interior's coordinates are
/// local and small, so a room position could land inside a planet anchor's disc by arithmetic coincidence rather than by
/// proximity.
/// </para>
/// <para>
/// A component of its own rather than fields on <see cref="PlayerState"/>, which would change the layout of a column every
/// system walks; this one is read and written by <c>PlayerMove</c> and by nothing else, and is never replicated.
/// </para>
/// <para>
/// <b><see cref="StorageMode.SingleVersion"/> rather than <see cref="StorageMode.Transient"/>, and the choice costs nothing
/// either way.</b> <c>Player</c> is a <c>ClusterDurability.Checkpoint</c> archetype, so no per-tick fence record reaches the
/// WAL for it; Transient would move this column into a store of its own and buy an indirection in the move loop for a
/// durability saving that is already zero.
/// </para>
/// <para>
/// <b>It therefore IS captured by the checkpoint, and a reopened world restores a disc up to a checkpoint interval stale.</b>
/// That is safe, and only for a stated reason: the placement is restored from the same coherent checkpoint instant as the
/// disc, so the anchor still describes where the body actually is — and the structures the disc was measured against do not
/// move. <see cref="Generation"/> is what covers the case where one day they do.
/// </para>
/// </remarks>
[Component("Swg.PlayerProximity", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerProximity
{
    /// <summary>Where the last query was taken from, east.</summary>
    [Field] public float AnchorX;

    /// <summary>Where the last query was taken from, north.</summary>
    [Field] public float AnchorZ;

    /// <summary>
    /// Distance from the anchor within which no fixed thing can touch this body. Zero — the default, and what a body in
    /// contact with a wall holds — forces a query on the next step.
    /// </summary>
    [Field] public float ClearM;

    /// <summary>
    /// The top of the published surface the body was standing on when the disc was taken, or
    /// <see cref="float.NegativeInfinity"/> for a body standing on the terrain.
    /// </summary>
    /// <remarks>
    /// <b>Carried on the disc because the held path must not read a neighbour, and must not forget the floor either.</b>
    /// A body on a building's deck is lifted onto it by the refresh; if the tick that HOLDS its disc re-sampled the
    /// terrain, the body would drop through the deck, its feet would be below the deck's top, and the floor it was
    /// standing on would become a wall and eject it — a refresh, a lift, a drop, for ever. Measured before this field
    /// existed: 418 contacts in 468 steps, and a body that could not walk at all.
    /// </remarks>
    [Field] public float SurfaceY;

    /// <summary>The realm the anchor was taken in. A disc from another realm says nothing about this one.</summary>
    [Field] public ushort Realm;

    /// <summary>
    /// The world's static generation when the anchor was taken; a disc from an older one is retired unread.
    /// </summary>
    /// <remarks>
    /// Nothing bumps it yet — no code path creates a <c>WorldObject</c> after world build — but player houses, harvesters and
    /// camp objects are already in the footprint table, and the first runtime placement would otherwise leave a body standing
    /// inside a new building with nothing anywhere to notice. Two bytes, which this component had spare to its alignment, and
    /// one comparison. See <c>SimBridge.InvalidateStaticNeighbourhoods</c>.
    /// </remarks>
    [Field] public ushort Generation;
}

/// <summary>Who decides what a player does.</summary>
/// <remarks>
/// <b>The field exists so that two deciders cannot fight over one entity (SWG-01).</b> Before it, <c>PlayerThink</c> drove every player in the world; a
/// client sending an intent would have had its destination overwritten by the server's own activity mix on the next tick that player re-decided. The
/// simulation therefore has to be told which players are no longer its to drive, and that is the whole content of this enum.
/// </remarks>
public static class ControllerKind
{
    /// <summary>
    /// Driven by <c>PlayerThink</c>: the simulated crowd, which is the workload this demo exists to generate.
    /// </summary>
    /// <remarks>
    /// <b>Named for what the entity is, not for where its decision is taken.</b> It was <c>InProcess</c>, which described the
    /// decider's location and read as a third category of thing beside players and NPCs -- and for a while it was one: a session
    /// used to be handed a free body out of this crowd, so the value meant both "the crowd" and "not yet claimed". Avatars ended
    /// that (A5): a login spawns its own body and a logout destroys it, nothing claims a scripted one, and the value now means
    /// exactly one thing. A scripted body is an NPC that happens to carry a player's components, because measuring what a player
    /// population costs is the point.
    /// </remarks>
    public const byte Scripted = 0;

    /// <summary>Possessed by a human client's session. <c>PlayerThink</c> leaves it alone entirely.</summary>
    public const byte Human = 1;

    /// <summary>
    /// Possessed by a bot, which sends the same intents over the same wire. Distinct from <see cref="Human"/> only so a measurement can say which it
    /// measured; nothing in the simulation branches on the difference.
    /// </summary>
    public const byte Bot = 2;
}

/// <summary>
/// One byte: who decides what this player does. Read by every system that must leave a possessed player alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>One byte in a component of its own, and the arithmetic is the whole reason.</b> Three systems read this every tick for every player —
/// <c>PlayerThink</c>, <c>Shuttle</c> and <c>Dungeon</c> — on the measurement path as well as the serve path. It first shipped as one field of a 16-byte
/// struct alongside the target and the controller, with a comment claiming "one byte per player, contiguous: a cache line covers 64 of them". That was wrong
/// by a factor of sixteen: components are stored array-of-structs, so a 16-byte stride puts **four** players in a cache line and made the guard a new
/// 16-byte stream over every player per tick. One byte per player is what the comment described and what this is: 64 players per line, and a whole cluster's
/// worth in one.
/// </para>
/// <para>
/// <b>Separate from <see cref="PlayerState"/> for the reason <see cref="CreatureTimers"/> exists.</b> <c>PlayerState</c> is in the compiled projection — it
/// carries <c>[Replicate]</c> on <c>Activity</c> and <c>[Owner]</c> on the mission waypoint — and <c>ClusterRef.GetSpan</c> marks a cluster changed when the
/// span is handed out, never observing what the caller does with it. A possession mark written beside replicated state would read as a change to that state
/// on every input tick. Keeping it out also leaves <c>PlayerState</c>'s layout byte-for-byte what every earlier measurement ran against.
/// </para>
/// <para>Zero is the resting state: <c>Scripted</c>, so a spawned player needs no initialisation.</para>
/// </remarks>
[Component("Swg.PlayerControl", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerControl
{
    /// <summary>See <see cref="ControllerKind"/>.</summary>
    [Field] public byte Kind;
}

/// <summary>
/// What a player looks like: the outfit its character wears, as everyone watching draws it (Loïc, 2026-10-25: "each machine display its own character with
/// the casual2_male skin, but I dont see this skin on the character of my other session").
/// </summary>
/// <remarks>
/// <para>
/// <b>The character's, copied onto the body at spawn</b> (<see cref="Character.Outfit"/>): a look is something a person keeps across sessions, so it is
/// persisted with the character and the body only carries it for the wire. Before it, a client drew its own avatar as one constant and every other player as
/// whatever its netId hashed to — so two sessions never agreed on what either of them looked like.
/// </para>
/// <para>
/// <b>A component of its own, for the reason <see cref="PlayerControl"/> is one.</b> It is written at spawn and on progress (a level, the XP byte), rarely; next to state that changes
/// it would ride every change of that state's cluster for nothing, and it would widen a column every system walks.
/// </para>
/// <para>Zero is "no look": the pooled crowd's scripted players are spawned without one and are dressed by netId, as before.</para>
/// <para>
/// <b>Also the name, as an id</b> (A12): what others read on the player, resolved by a client over <c>/typhon/names</c>
/// (<see cref="PlayerNames"/>). The text itself on the body — a <c>String64</c> — took the archetype's clusters from 25 players to 10; this
/// 8-byte component keeps them at 24. It is the place for the customisation that will follow the outfit, for the same reason.
/// </para>
/// </remarks>
[Component("Swg.PlayerLook", 4, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerLook
{
    /// <summary>One plus an index into <see cref="TatooineData.PlayerOutfits"/>, or 0 for none. On the wire once, on enter: it never changes.</summary>
    [Field, OnEnter(CodecKind.U8, Name = "outfit", Saturate = true)] public byte Outfit;

    /// <summary>1 for a body whose account the bot fleet signed up (<see cref="Accounts.Account"/>'s <c>Bot</c>), 0 otherwise: a viewer's to show, never a branch.</summary>
    /// <remarks>In the padding after <see cref="Outfit"/>: the component stays eight bytes.</remarks>
    [Field, OnEnter(CodecKind.U8, Name = "bot", Saturate = true)] public byte Bot;

    /// <summary>The character's combat level, 1-50 (<see cref="CharacterProgress.Level"/>); 0 for the scripted crowd, which has no character.</summary>
    /// <remarks>A copy of the character's, written at spawn and on each level-up (character-level.md § 1), for the wire and the combat drain.</remarks>
    [Field, Replicate(CodecKind.U8, Name = "level", Saturate = true)] public byte Level;

    /// <summary>The way to the next level, in 256ths (<see cref="Progression.XpByte"/>): the owner's XP bar. Written with <see cref="Level"/>.</summary>
    /// <remarks>Here, and with <see cref="Level"/> in the padding before <see cref="NameId"/>: the component stays eight bytes and <see cref="Player"/> grows nothing.</remarks>
    [Field, Owner(CodecKind.U8, Name = "xp", Group = "progress", Saturate = true)] public byte Xp;

    /// <summary>The character's name as a <see cref="PlayerNames"/> id, 0 for none (the scripted crowd). On the wire once, on enter.</summary>
    [Field, OnEnter(CodecKind.U32, Name = "nameId")] public uint NameId;
}

/// <summary>
/// Which session drives this player and what it is aiming at. Read only where an intent is applied, never on the per-tick guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split from <see cref="PlayerControl"/> because the two have different read rates by an order of magnitude.</b> The possession byte is read for every
/// player every tick; these two are read for the handful of players a client sent a command about. Keeping them together made the common read pay for the
/// rare one — see <see cref="PlayerControl"/> for the measurement that says by how much.
/// </para>
/// <para>
/// <b>Not replicated, deliberately.</b> A client knows its own target: it sent the command. Putting <see cref="Target"/> on the wire would pull this
/// component into the projection; when a reason to show a target to OTHER clients arrives, it belongs in the event that describes the attack rather than in
/// per-entity state.
/// </para>
/// </remarks>
[Component("Swg.PlayerSession", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerSession
{
    /// <summary>What this player is attacking or inspecting, resolved from the <c>netId</c> its client sent; <c>EntityId.Null</c> for none.</summary>
    /// <remarks>
    /// The resolved entity, not the <c>netId</c>. An identity is released and reused when an entity leaves every session's view, so a stored <c>netId</c>
    /// can come to name a different entity between the tick it was sent and the tick it is read — which is a target that silently changes creature.
    /// </remarks>
    [Field] public EntityId Target;

    /// <summary>
    /// The session driving this player — <c>SessionId.Value</c> — or 0 when <see cref="PlayerControl.Kind"/> is <see cref="ControllerKind.Scripted"/>.
    /// </summary>
    /// <remarks>
    /// It is what <c>TatooineReplication.ControlledBy</c> scans for, so the world rather than a dictionary answers which player a session holds.
    /// </remarks>
    [Field] public uint Controller;

    /// <summary>Which archetype <see cref="Target"/> belongs to — see <c>CombatTargetKind</c>; meaningless when the target is null (SWG-02).</summary>
    /// <remarks>
    /// <para>
    /// Carried rather than derived at the point of use, because the code that decides what to write to a target must know its archetype and an
    /// <see cref="EntityId"/> alone does not say: the routing id it carries is comparable only against
    /// <c>DatabaseEngine.ArchetypeIdOf&lt;T&gt;()</c>, which is exactly the classification this field caches. The sim's own acquisition knows what it aimed at
    /// for free; a client's <c>SetTarget</c> classifies once, when the netId is resolved, instead of on every shot.
    /// </para>
    /// <para>It costs nothing: <see cref="Controller"/> leaves four bytes of padding before the struct's 8-byte alignment.</para>
    /// </remarks>
    [Field] public byte TargetKind;
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

    /// <summary>
    /// Walking to where a building's door path starts, on open ground in front of its ramp (Realms G1b). <see cref="PlayerState.ShuttleDest"/> holds the
    /// door, as the realm of the room it leads into.
    /// </summary>
    public const int ToPortal = 6;

    /// <summary>In a building's interior realm; leaves through the same door when the timer runs out.</summary>
    public const int Inside = 7;

    /// <summary>Walking the door path through the door: the crossing fires on the step over its plane, the same test a client's body takes.</summary>
    public const int EnteringDoor = 8;

    /// <summary>
    /// Walking out of a room: through its navigation to the way out, then through it. <see cref="PlayerState.ShuttleDest"/> holds the room's realm and
    /// <see cref="PlayerState.ShuttleFrom"/> the waypoint reached.
    /// </summary>
    public const int LeavingRoom = 9;
}

/// <summary>A creature lair: the object that spawns and owns a population, and what a destroy mission sends a player to break.</summary>
/// <remarks>
/// <b>A mission's lair is a wild lair</b> (missions.md § 8): an offer names one the world has, and taking it sets <see cref="Mission"/> — the crowd then
/// leaves it alone and the lair director does not retire it until the desk releases it.
/// </remarks>
[Component("Swg.Lair", 5, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Lair
{
    /// <summary>Which creature template this lair spawns; indexes <see cref="CreatureTemplates"/>.</summary>
    [Field, OnEnter(CodecKind.U16, Name = "template", Saturate = true)] public int CreatureTemplate;

    /// <summary>How many creatures this lair keeps alive.</summary>
    [Field] public int SpawnLimit;

    /// <summary>How many are alive right now.</summary>
    [Field] public int AliveCount;

    /// <summary>
    /// Its occupants still standing: <see cref="AliveCount"/> less the corpses not yet removed. While any stands the lair cannot be damaged — a camp is
    /// destroyed once all its occupants are killed (Loïc, 2026-10-24). Replicated, so a client can draw the lair's bar as shielded.
    /// </summary>
    [Field, Replicate(CodecKind.U8, Name = "guards", Saturate = true)] public int Guards;

    /// <summary>
    /// 1 once a blow from <see cref="Owner"/> has landed on this mission lair, 0 otherwise. Breaking it completes the mission only then (missions.md, Q3): a
    /// lair broken by somebody else alone sends the mission to another lair of its level, so a passer-by cannot end a mission, and a fight shared with
    /// friends still pays. Cleared when the reservation is released.
    /// </summary>
    [Field] public int OwnerHit;

    /// <summary>How far from the lair its creatures spawn and wander.</summary>
    [Field] public float SpawnRadius;

    /// <summary>
    /// The wilderness site that spawned this lair, one-based into <c>SimBridge</c>'s site list (regions, then points of interest); 0 for one placed by hand
    /// (a check), which the director never retires and an offer never names.
    /// </summary>
    /// <remarks>What the lair director refills: a retired lair is replaced by another drawn from the same site, so a region keeps its density.</remarks>
    [Field] public int Site;

    /// <summary>
    /// The lair's content level, 1-50 (content-levels.md): the level map's at where it stands, or its mission's. Every creature it spawns carries it
    /// (<see cref="CreatureBrain.Level"/>), and its health is <c>ContentLevels.LairHealth</c> of it.
    /// </summary>
    /// <remarks>In the 4-byte hole <see cref="Site"/> leaves before the 8-aligned <see cref="Mission"/>, so it costs nothing.</remarks>
    [Field, OnEnter(CodecKind.U8, Name = "level", Saturate = true)] public int Level;

    /// <summary>The held mission that reserved this lair (<see cref="HeldMissionArch"/>); <c>EntityId.Null</c> on an unreserved wild lair.</summary>
    [Field] public EntityId Mission;

    /// <summary>The body of the mission's holder, while it is online — whose blows set <see cref="OwnerHit"/>; <c>EntityId.Null</c> on a wild lair.</summary>
    [Field] public EntityLink<Player> Owner;

    /// <summary>
    /// The tick this lair is retired at by the lair director, whether or not anybody broke it — not while a mission reserves it. 0 for never.
    /// </summary>
    [Field] public long ExpireTick;

    /// <summary>The tick this lair may next replace a creature it lost; 0 when it has lost none.</summary>
    [Field] public long NextSpawnTick;
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
    [Field, OnEnter(CodecKind.U8, Name = "kind", Saturate = true)] public int Kind;

    /// <summary>
    /// Which way this structure faces: a quarter turn about +Y, 0 to 3, the way the renderer counts its yaw — one
    /// quarter sends +Z to +X, so a point <c>(x, z)</c> goes to <c>(z, -x)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The world's, not the renderer's.</b> A client used to invent this from a hash of the position, which was fine
    /// while a structure was a box — a box looks the same whichever quarter turn you give it. It stops being fine the
    /// moment the thing has a door and a collision shape: the server would be pushing a body out of a wall that the
    /// client had drawn on the other side of the building. Two copies of a rule are two answers, and this is the half
    /// that has to be right.
    /// </para>
    /// <para>
    /// <b>Quarter turns rather than a free angle</b>, because that is what the site planner's axis-aligned boxes admit:
    /// a square footprint stays axis-aligned under a quarter turn, so collision keeps working on 2-D AABBs and a
    /// building still faces along the street. Replicated on enter with <c>kind</c> — a building never turns.
    /// </para>
    /// </remarks>
    [Field, OnEnter(CodecKind.U8, Name = "yaw", Saturate = true)] public int Yaw;

    /// <summary>
    /// Which published asset this structure is, as an index into the table the server publishes, or
    /// <see cref="StructureAssets.NoAsset"/> for a kind nothing stands in for.
    /// </summary>
    /// <remarks>
    /// <b>The server picks, and says so.</b> It is the side that pushes bodies out of this asset's walls, so it cannot
    /// let the renderer pick a different one — the wall you cannot walk through would not be the wall you can see.
    /// Replicating the CHOICE rather than the rule also keeps the two sides from needing the same random draw, which
    /// they could not have: a client numbers entities by netId and the world builds them by EntityId.
    /// </remarks>
    [Field, OnEnter(CodecKind.U8, Name = "asset", Saturate = true)] public int Asset;

    /// <summary>
    /// The realm behind this building's door — its room — or 0 when the building cannot be entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The link, stored, rather than an index the realm id was computed from.</b> This was the building's slot in its planet's door list, and the
    /// interior was <c>Planets + p·N + slot</c> wherever anyone needed it: the server's crossing code, the rebuild, the client's inspector. Now the world
    /// build assigns the realm once and writes it here, and everything else reads it. The way back is data too: the room's <c>Portal</c> names its twin,
    /// and the twin names this building as its owner.
    /// </para>
    /// <para>
    /// <b>0 is "none", and it cannot be a room</b>: realm 0 is planet 0. It costs no memory — five ints plus this one is 24 bytes, which is what the struct
    /// already occupied.
    /// </para>
    /// <para>
    /// <b>Replicated on enter</b> so a viewer can walk into the building it picked without knowing the realm layout. Sent once with the entity, like
    /// <c>kind</c> and <c>region</c>: a building's room never changes.
    /// </para>
    /// </remarks>
    [Field, OnEnter(CodecKind.U16, Name = "interior", Saturate = true)] public int InteriorRealm;

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

    /// <summary>A crafting station, in the city slots that were a generic terminal drawing a bazaar, a crafting station or a mission terminal.</summary>
    public const int CraftingStation = 1;
    public const int Shuttleport = 2;
    public const int PoiProp = 3;
    public const int PlayerHouse = 4;
    public const int Factory = 5;
    public const int Harvester = 6;
    public const int CampObject = 7;

    /// <summary>
    /// A building's room: the one structure of an interior realm, at that realm's origin (Realms G1b). Its walls are what a body inside collides with and
    /// what a client draws, and its <c>Portal</c> is the way back out.
    /// </summary>
    public const int Interior = 8;

    /// <summary>
    /// A bazaar terminal: one beside every shuttleport's gate, and more spread over the city by its area (<see cref="SimConfig.FacilityDensityPerKm2"/>).
    /// </summary>
    public const int Bazaar = 9;

    /// <summary>A mission terminal, placed like a <see cref="Bazaar"/>.</summary>
    public const int MissionTerminal = 10;

    /// <summary>
    /// A city's town hall: one per city, across the way from its shuttleport's gate with its door facing it, and a room behind the door like a house's.
    /// </summary>
    public const int TownHall = 11;
}

/// <summary>Where one side of a door stands: the published door box, turned and placed. Never moves.</summary>
/// <remarks>
/// Spatial so the collision pass finds the doors near a body with the same kind of radius query it finds the walls with, in the body's own realm. The box
/// contains the door's mid-plane over the opening's width, so any step that crosses the plane ends near the box.
/// </remarks>
[Component("Swg.PortalPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PortalPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

/// <summary>Which realm a door side is in (Realms G1): a planet for a building's front door, the interior for the room's way out.</summary>
[Component("Swg.PortalRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PortalRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public PortalRealm(ushort value) => Value = value;
}

/// <summary>
/// One side of a door: the plane a body crosses, where it lands on the other side, and the two links that make a building and its room one thing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is in the realm's own frame and already placed</b> — mirrored from the descriptor, turned by the building's quarter turn and moved
/// to where it stands — so the crossing test is a dot product and the landing a copy. The published asset is read once, when the world is built, and a
/// reopened world needs no asset to know where its doors are.
/// </para>
/// <para>
/// <b>Two links, both stored.</b> <see cref="Twin"/> is the other side of the same door, in the other realm; <see cref="Owner"/> the structure this side
/// belongs to — the building on the planet, the room inside. So "which building is this room in" is a room's portal, its twin, the twin's owner, with no
/// arithmetic on realm ids anywhere. <see cref="Twin"/> is stored on the room's side only, and paired both ways at open (<c>PortalTable.Build</c>): the
/// door into a room is spawned before its way out exists, and linking it back afterwards was a second write that left a window with half-linked doors.
/// </para>
/// </remarks>
[Component("Swg.PortalDoor", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PortalDoor
{
    /// <summary>Where the mid-plane crosses the floor at the middle of the opening, east.</summary>
    [Field] public float CenterX;

    /// <summary>The same point, north.</summary>
    [Field] public float CenterZ;

    /// <summary>The unit direction a body crosses this side in, east component: into the building on the planet, out of the room inside.</summary>
    [Field] public float ForwardX;

    /// <summary>The same direction, north component.</summary>
    [Field] public float ForwardZ;

    /// <summary>Half the opening's width: a crossing further off its middle than this went through the wall beside it.</summary>
    [Field] public float HalfWidth;

    /// <summary>Where a body that crosses lands, in <see cref="TargetRealm"/>'s frame: the published spawn on the far side.</summary>
    [Field] public float ArriveX;

    /// <summary>The same point, north.</summary>
    [Field] public float ArriveZ;

    /// <summary>
    /// The feet's height there: the published spawn's, placed with its structure. A crossing lands at this or the terrain, whichever is higher — the
    /// house's front spawn is on the landing at the top of its ramp, 0.7 m above the house's origin, and a body put on the terrain under it stood inside
    /// the landing and was pushed out of it. Always a real height: a door with nothing published under its landing stores the terrain's there.
    /// </summary>
    [Field] public float ArriveY;

    /// <summary>The realm on the far side.</summary>
    [Field] public int TargetRealm;

    /// <summary>The other side of this door, on a room's way out; none on the door into the room, which the way out names (see the remarks).</summary>
    [Field] public EntityLink<Portal> Twin;

    /// <summary>The structure this side belongs to: the building, or its room.</summary>
    [Field] public EntityLink<WorldObject> Owner;
}

