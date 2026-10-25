using System;
using System.Collections.Generic;

namespace SwgTatooine.Server;

/// <summary>
/// The handles and geometry the simulation systems need after the world is built: entity ids they must address directly,
/// and the places a behaviour picks destinations from.
/// </summary>
/// <remarks>
/// This exists because a spatial query returns a raw entity key, not something a transaction can open, and because a
/// player deciding to travel needs a list of somewhere-to-go rather than a uniform point on the map. Keeping it beside
/// the database rather than inside it is deliberate: none of this is simulation state, and persisting it would mean
/// maintaining it.
/// </remarks>
public sealed class WorldIndex
{
    /// <summary>Every creature lair, so the spawn system can walk them without a query.</summary>
    public List<EntityId> Lairs { get; } = [];

    /// <summary>Every player.</summary>
    public List<EntityId> Players { get; } = [];

    /// <summary>NPC city discs, with the share of players who call each home.</summary>
    public List<(float X, float Z, float Radius, float PlayerWeight)> Cities { get; } = [];

    /// <summary>Each city's shuttleport, index-aligned with <see cref="Cities"/> — where a shuttle passenger queues and where one arrives.</summary>
    public List<(float X, float Z)> Shuttleports { get; } = [];

    /// <summary>Point-of-interest discs — where a player roams to when it is not in a city or on a mission.</summary>
    public List<(float X, float Z, float Radius)> Pois { get; } = [];

    /// <summary>
    /// The places wild lairs are spawned in — every point of interest, then every wilderness region — in the order <c>Lair.Site</c> numbers them from one.
    /// </summary>
    /// <remarks>
    /// The half of a site that is a fact about the map (its disc, its template mix, how many lairs it holds) is planned from the map, so a reopened world
    /// plans the same list; the half that is a recollection (how many are standing now) is counted from the lairs on disk.
    /// </remarks>
    public List<LairSite> Sites { get; } = [];

    /// <summary>Every mission terminal on the planet: the entity, where it stands, and its city's group key (region + 1) — what the mission desk lists at.</summary>
    public List<MissionTerminalRef> Terminals { get; } = [];

    /// <summary>
    /// Sine and cosine of a placement quarter turn, in the sense the RENDERER turns the building.
    /// </summary>
    /// <param name="quarters">The replicated quarter turns, 0 to 3.</param>
    /// <returns>The pair, for (x, z) -> (x cos - z sin, x sin + z cos).</returns>
    /// <remarks>
    /// <b>Measured against the drawn building, not derived.</b> The obvious table is mirrored: Babylon turns about +Y in
    /// the opposite sense to the textbook 2-D rotation this formula is written as, so quarters 1 and 3 come out swapped.
    /// Quarters 0 and 2 agree either way, which is exactly what makes the mistake survive a casual look — half the
    /// buildings in the world had their door placed on the wrong side, and the other half were perfect. Checked by
    /// transforming the published door through the placed instance own world matrix in the browser and comparing.
    /// </remarks>
    public static (float Sin, float Cos) QuarterTurn(byte quarters) => quarters switch
    {
        1 => (-1f, 0f),
        2 => (0f, -1f),
        3 => (1f, 0f),
        _ => (0f, 1f),
    };
}

/// <summary>A mission terminal as the mission desk lists at it (<see cref="WorldIndex.Terminals"/>).</summary>
/// <param name="Id">The terminal's entity.</param>
/// <param name="X">Where it stands.</param>
/// <param name="Z">Where it stands.</param>
/// <param name="Region">Its city's group key, region + 1 — what its offers carry so the city set delivers them.</param>
/// <param name="Realm">Its planet.</param>
public readonly record struct MissionTerminalRef(EntityId Id, float X, float Z, int Region, ushort Realm);

/// <summary>A disc of the wilderness that keeps a number of lairs standing: a point of interest or a spawn region (<see cref="WorldIndex.Sites"/>).</summary>
/// <remarks>
/// A class, mutated in place by the lair director (<c>SimBridge.LairLifeTick</c>, serial): <see cref="Live"/> and <see cref="NextSpawnTick"/> are the
/// director's bookkeeping, and the rest is the map's.
/// </remarks>
public sealed class LairSite
{
    public LairSite(ushort realm, float x, float z, float radius, int[] templates, int target)
    {
        Realm = realm;
        X = x;
        Z = z;
        Radius = radius;
        Templates = templates;
        Target = target;
    }

    /// <summary>The planet the site is on.</summary>
    public ushort Realm { get; }

    public float X { get; }

    public float Z { get; }

    public float Radius { get; }

    /// <summary>The <see cref="CreatureTemplates"/> a new lair here is drawn from.</summary>
    public int[] Templates { get; }

    /// <summary>How many lairs the site keeps standing: what the world was built with.</summary>
    public int Target { get; }

    /// <summary>How many are standing now.</summary>
    public int Live { get; set; }

    /// <summary>The earliest tick a replacement may be spawned here; 0 when none is owed.</summary>
    public long NextSpawnTick { get; set; }
}
