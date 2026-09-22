using System.Collections.Generic;

namespace SwgTatooine;

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
}
