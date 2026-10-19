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

    /// <summary>Its health as a fraction of its maximum, or 1 when the server does not replicate it.</summary>
    public float HealthFraction { get; init; }

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

    /// <summary>The city whose shuttleport is within boarding range, or -1.</summary>
    public int PortCity { get; init; }

    /// <summary>How many other players are in view, which is what makes a place busy.</summary>
    public int PlayersInView { get; init; }
}
