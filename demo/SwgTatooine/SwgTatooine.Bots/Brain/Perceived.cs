namespace SwgTatooine.Bots.Brain;

/// <summary>What one bot was sent this tick, reduced to what a decision needs.</summary>
/// <remarks>
/// <para>
/// <b>A radius of entities, which is the whole difference between a bot and the server's own crowd.</b> <c>PlayerThink</c>
/// decides from global tables — every city, every lair, by lookup — and a bot decides from this: what replication put in
/// its store. That is why the two do not share a brain. Handing a bot the server's tables would give a client
/// authority-side knowledge it has no business holding, and restricting the server's crowd to a radius would cripple it
/// for no reason.
/// </para>
/// <para>
/// <b>Reduced rather than passed whole.</b> The store holds hundreds of entities and the decision needs a handful of
/// facts about them, so the scan happens once per tick in <c>Perception</c> and the brain reads a struct. It also means
/// the brain can be tested by writing one of these down, which a store cannot.
/// </para>
/// <para>
/// <b>Only what a goal reads lives here.</b> A field computed every tick for every bot and read by nothing is a cost
/// with no behaviour attached: health and the number of players in view were both written here and never consulted, and
/// reading health meant resolving a field index against one archetype and applying it to another. When a goal needs
/// either, it comes back with the goal that reads it — and with a test that fails without it.
/// </para>
/// </remarks>
public readonly struct Perceived
{
    /// <summary>Whether this session drives a body at all; a camera does not and has nothing to decide.</summary>
    public bool HasBody { get; init; }

    /// <summary>Where the body is, east.</summary>
    public float X { get; init; }

    /// <summary>Where the body is, north.</summary>
    public float Z { get; init; }

    /// <summary>The realm it is in: 0 is the planet, anything else an interior, a dungeon or space.</summary>
    public int Realm { get; init; }

    /// <summary>The nearest creature it could attack, or 0.</summary>
    /// <remarks>
    /// <b>A creature and not a lair, and the difference was a measurement rather than a preference.</b> The design said
    /// "`Player`, `CityNpc`, `Creature`, `CreatureLair` and `WorldObject` are all on the wire for a player session", and
    /// the first version of this brain hunted lairs on that basis — and never hunted anything, because only the two GOD
    /// profiles observe `CreatureLair`. A player session, bot or browser, is never sent one. Adding the archetype to the
    /// player's sphere would cost about 0.7 extra spatial cell-queries per tick per session on the profile every CPU
    /// number in this demo is taken on; hunting what a player can actually see costs nothing and is what a player does.
    /// </remarks>
    public uint NearestCreature { get; init; }

    /// <summary>Where that creature is, east.</summary>
    public float NearestCreatureX { get; init; }

    /// <summary>Where that creature is, north.</summary>
    public float NearestCreatureZ { get; init; }

    /// <summary>How far that creature is, metres.</summary>
    public float NearestCreatureM { get; init; }

    /// <summary>Whether a <c>BoardShuttle</c> this bot sent was refused in the frame just applied.</summary>
    /// <remarks>
    /// <b>The server's answer, read rather than guessed at.</b> A boarding is the one decision here the server can
    /// refuse, and the first version of this brain had no path for the answer: it asked once, moved to waiting, and
    /// stood still for the full patience — four minutes — whenever the ask did not take. The refusal is on the wire
    /// already, as an <c>ACKS</c> reason, and <c>TatooineReplication</c> says in as many words that it is refused rather
    /// than silently turned into a walk <i>because</i> a brain comparing routes needs to know which happened. Reading it
    /// turns four minutes of inertness into one tick.
    /// </remarks>
    public bool BoardRefused { get; init; }

    /// <summary>The REGION whose shuttleport is within boarding range — a city plus one — or 0 for none.</summary>
    /// <remarks>
    /// <b>A region and not a city index, so that absence is 0 rather than -1.</b> This is the value the server puts on
    /// the wire: a structure's <c>region</c> field is its city plus one, because the key is a sparse index's and a
    /// sparse index never files key 0. Carrying it unchanged means the brain has no sentinel to get wrong, and a
    /// <c>default</c> of this struct says "no port in range" instead of "standing at city 0's port". The subtraction
    /// happens once, where a destination reaches the wire.
    /// </remarks>
    public int PortRegion { get; init; }

    /// <summary>Whether the body is riding its swoop: the server's replicated <c>mounted</c>, the only authority on it.</summary>
    public bool Mounted { get; init; }

    /// <summary>
    /// The body's replicated combat stance (<c>stance</c>): 0 none, 3 the pistol. A swoop, a door, a flight and a clone all put the weapon away, so a fight
    /// reads it rather than remembering having drawn one.
    /// </summary>
    public int Stance { get; init; }

    /// <summary>Missions its character holds, as the journal (<c>GET /swg/missions</c>) last said; 0 for none.</summary>
    public int MissionsHeld { get; init; }

    /// <summary>Whether the journal gave the held mission's lair a position: false while it is being drawn or moved.</summary>
    public bool HasMissionLair { get; init; }

    /// <summary>Where the held mission's lair stands, east.</summary>
    public float MissionLairX { get; init; }

    /// <summary>Where the held mission's lair stands, north.</summary>
    public float MissionLairZ { get; init; }

    /// <summary>The held mission's lair as the bot sees it — the lair in view standing on the journal's point — or 0 while it is out of view.</summary>
    public uint MissionLair { get; init; }

    /// <summary>That lair's occupants still standing (its replicated <c>guards</c>): it cannot be hurt until they are down.</summary>
    public int MissionLairGuards { get; init; }

    /// <summary>The structures it holds, read when a goal plans a walk; null in a test that needs none.</summary>
    public IStructureView Structures { get; init; }
}
