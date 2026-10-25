using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace SwgTatooine.Server;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Missions (claude/design/SwgTatooine/design/missions.md)
//
// An OFFER is listed at a mission terminal, a HELD MISSION belongs to a character, and the lair is spawned for the held
// mission when it is taken. Nothing is pooled.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Where an offer is listed: at its terminal's centre. Its position is what a client finds the terminal's offers by.</summary>
[Component("Swg.MissionOfferPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MissionOfferPlacement
{
    /// <summary>A point box at the terminal's centre.</summary>
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    /// <summary>Places the offer at a point.</summary>
    public void SetAt(float x, float z) => Bounds = new AABB2F { MinX = x, MinY = z, MaxX = x, MaxY = z };
}

/// <summary>What an offer says, as a terminal lists it: what you will fight, how hard, what it pays, and roughly where.</summary>
/// <remarks>
/// <para>
/// <b>Approximate on the wire, exact on the server.</b> A client is told a direction and a distance rounded to 100 m; the anchor stays here, and the lair is
/// drawn round it only when the offer is taken (missions.md, R2).
/// </para>
/// <para>
/// <b>Immutable.</b> An offer is listed, then taken or aged out — both destroy it — so every field is sent on enter and the archetype is static.
/// </para>
/// </remarks>
[Component("Swg.MissionOfferInfo", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MissionOfferInfo
{
    /// <summary>The terminal it is listed at.</summary>
    [Field] public EntityId Terminal;

    /// <summary>
    /// A number the desk gives each offer, carried back by <c>TakeMission</c> beside the netId: a netId is released and reused when its entity leaves every
    /// view, so a late take naming a taken offer's netId must not take whatever has that netId now.
    /// </summary>
    [Field, OnEnter(CodecKind.Varu, Name = "serial", Saturate = true)] public uint Serial;

    /// <summary>The lair kind it sends the taker to break (<see cref="CreatureTemplates"/>), which is also its name.</summary>
    [Field, OnEnter(CodecKind.U8, Name = "kind", Saturate = true)] public byte Kind;

    /// <summary>1 for now (missions.md, R7).</summary>
    [Field, OnEnter(CodecKind.U8, Name = "difficulty", Saturate = true)] public byte Difficulty;

    /// <summary>Which way the anchor lies from the terminal: 0 north, then clockwise to 7, north-west.</summary>
    [Field, OnEnter(CodecKind.U8, Name = "octant", Saturate = true)] public byte Octant;

    /// <summary>Credits paid when it is completed.</summary>
    [Field, OnEnter(CodecKind.Varu, Name = "reward", Saturate = true)] public int Reward;

    /// <summary>How far the anchor is from the terminal, metres, rounded to 100.</summary>
    [Field, OnEnter(CodecKind.Varu, Name = "distanceM", Saturate = true)] public int DistanceM;

    /// <summary>The point the lair is drawn round. Not on the wire.</summary>
    [Field] public float AnchorX;

    [Field] public float AnchorZ;

    /// <summary>The tick this offer is taken off the list untaken, and replaced.</summary>
    [Field] public long ExpireTick;
}

/// <summary>The city an offer's terminal stands in, as the city set's key: region + 1, like <see cref="StructureRegion"/>.</summary>
/// <remarks>
/// <b>What delivers the list.</b> An offer is a member of the city set, so a session in a city holds every offer of every terminal in it — live, the same for
/// everyone, with no polling and no crescent query.
/// </remarks>
[Component("Swg.MissionOfferRegion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MissionOfferRegion
{
    [Field, Index(AllowMultiple = true, Sparse = true)] public int Value;

    public MissionOfferRegion(int value) => Value = value;
}

/// <summary>The planet an offer is listed on.</summary>
[Component("Swg.MissionOfferRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MissionOfferRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public MissionOfferRealm(ushort value) => Value = value;
}

/// <summary>Who holds a mission: a person's character, or a scripted body of the crowd.</summary>
public static class MissionHolderKind
{
    /// <summary><see cref="HeldMission.Holder"/> is a <see cref="CharacterArch"/> entity; its body is whichever avatar that character has online.</summary>
    public const byte Character = 0;

    /// <summary><see cref="HeldMission.Holder"/> is a scripted <see cref="Player"/>: the body itself, which never logs out.</summary>
    public const byte Scripted = 1;
}

/// <summary>
/// A mission somebody took: what the offer said, where its lair is drawn round, and the lair standing for it now.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial, like <see cref="Character"/>, and for both of its reasons</b> (missions.md § 2). A held mission is
/// what a player was told they have, so it rides the WAL; and being Versioned with no placement is what lets <c>GET /swg/missions</c> read it from a Kestrel
/// thread while the tick runs (<c>EW-02</c>).
/// </para>
/// <para>
/// <b>The holder is a plain id, not a link</b>, because it names a character for a person and a body for the crowd. A character deleted with its account
/// leaves its missions behind; the desk retires a mission whose character it cannot find.
/// </para>
/// </remarks>
[Component("Swg.HeldMission", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HeldMission
{
    /// <summary>The character (or scripted body) holding it. Indexed: the journal is "every mission of this character".</summary>
    [Field, Index(AllowMultiple = true)] public EntityId Holder;

    /// <summary>See <see cref="MissionHolderKind"/>.</summary>
    [Field] public byte HolderKind;

    /// <summary>The offer's lair kind, difficulty, direction, and distance as it was listed — what the journal shows.</summary>
    [Field] public byte Kind;

    [Field] public byte Difficulty;

    [Field] public byte Octant;

    [Field] public int DistanceM;

    [Field] public int Reward;

    /// <summary>The point every lair of this mission is drawn round.</summary>
    [Field] public float AnchorX;

    [Field] public float AnchorZ;

    /// <summary>Where its current lair stands, for the waypoint; meaningless while <see cref="Lair"/> is null.</summary>
    [Field] public float LairX;

    [Field] public float LairZ;

    /// <summary>The lair standing for it now, or <c>EntityId.Null</c>: its holder is offline, or a new one is still to be drawn.</summary>
    [Field] public EntityId Lair;

    /// <summary>The tick it was taken.</summary>
    [Field] public long TakenTick;

    /// <summary>The tick the crowd gives up on it; 0 for a person, who keeps a mission until it is done or cancelled.</summary>
    [Field] public long ExpireTick;

    /// <summary>The planet it is on.</summary>
    [Field] public ushort Realm;
}
