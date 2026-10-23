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
    /// Every enterable city building's door, in spawn order — portal <c>j</c> of planet <c>p</c> leads to interior realm
    /// <c>Planets + p · InteriorsPerPlanet + j</c> (Realms G1b). Recorded whether or not interiors are on: it draws nothing from the RNG.
    /// </summary>
    public List<(float X, float Z)> Portals { get; } = [];

    /// <summary>
    /// How each door's building stands, index-aligned with <see cref="Portals"/>: its quarter turn and its asset.
    /// </summary>
    /// <remarks>
    /// <b>Recorded because the door is not where the building is.</b> A published asset puts its doorway in a wall, several metres off the centre the
    /// structure was spawned at, and in a direction that depends on how the building was turned. The two facts needed to find it are drawn at spawn time
    /// from the world's own RNG and would otherwise have to be read back out of the database per portal; <see cref="PlaceDoors"/> uses them once, when the
    /// asset shapes have been loaded — which is after the world is built, so this cannot simply be resolved in place.
    /// </remarks>
    public List<(byte Yaw, byte Asset)> PortalFacings { get; } = [];

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

    /// <summary>
    /// Moves every door from its building's centre to the doorway the asset publishes.
    /// </summary>
    /// <param name="shapes">The loaded asset colliders, which carry each asset's doorway.</param>
    /// <returns>How many doors moved.</returns>
    /// <remarks>
    /// <b>Called once, after the shapes load and before the first tick.</b> A door at the centre of a building is reachable only by a body that walks
    /// through walls, which is what the scripted crowd used to do; with collision on it stopped at the wall and no interior was ever entered. The asset's
    /// own doorway is where a viewer sees a door, so it is where the crossing has to be for the two to agree.
    /// </remarks>
    public int PlaceDoors(AssetColliders shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        if (PortalFacings.Count != Portals.Count)
        {
            throw new InvalidOperationException(
                $"{Portals.Count} doors but {PortalFacings.Count} facings. Every door records how its building stands; see PortalFacings.");
        }

        var moved = 0;
        for (var j = 0; j < Portals.Count; j++)
        {
            var door = shapes.DoorFor(PortalFacings[j].Asset);
            if (!door.Exists)
            {
                continue;
            }

            var (sin, cos) = QuarterTurn(PortalFacings[j].Yaw);

            var (cx, cz) = Portals[j];
            Portals[j] = (cx + ((door.CenterX * cos) - (door.CenterZ * sin)), cz + ((door.CenterX * sin) + (door.CenterZ * cos)));
            moved++;
        }

        return moved;
    }

    /// <summary>Each city's run in <see cref="Portals"/>, index-aligned with <see cref="Cities"/>.</summary>
    public List<(int First, int Count)> CityPortals { get; } = [];
}
