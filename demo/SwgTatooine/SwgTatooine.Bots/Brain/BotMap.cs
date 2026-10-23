namespace SwgTatooine.Bots.Brain;

/// <summary>The planet map a bot ships with.</summary>
/// <remarks>
/// <para>
/// <b>Shipped, not received, and that is the design rather than a shortcut.</b> A player knows where Mos Eisley is
/// without standing in it, because a real client ships the planet map; so does a bot. What a bot does NOT ship is what
/// is inside a city — which building is the shuttleport is a draw the world generator made — so that has to be seen or
/// delivered. <see cref="FacilityMap"/> is the other half.
/// </para>
/// <para>
/// <b>The same table the browser ships</b> (<c>demo/SwgTatooine.Client/src/data/world-data.ts</c>), with the same
/// coordinates and the same display names. Duplicated rather than referenced, deliberately: this process references
/// <c>Typhon.Client</c> and nothing of the server, which is the blueprint claim the fleet exists to keep honest, and a
/// load generator reading the server's own tables would be measuring a path no real client takes. The server's
/// <c>TatooineData.Cities</c> is where the coordinates come from and where a change to them starts.
/// </para>
/// <para>
/// <b>The names are the client's and not the server's</b>, for the reason <c>06-gameplay.md § 7</c> gives: public
/// artefacts carry original names rather than the franchise's. The coordinates are identical, so a bot and a browser
/// walking to "Port Sable" and "Mos Eisley" arrive at the same place.
/// </para>
/// </remarks>
public static class BotMap
{
    /// <summary>The planet's edge, metres — the coordinate range is half this each way about the origin.</summary>
    public const float PlanetEdgeM = 16384f;

    /// <summary>Tatooine's cities, in the order the server numbers them, which is the order a destination names.</summary>
    /// <remarks>
    /// <b>The index IS the wire value</b> for both <c>BoardShuttle</c>'s destination and the <c>region</c> a structure
    /// carries (as region + 1), so the order of these rows is a protocol detail and not a presentation choice.
    /// </remarks>
    public static readonly City[] Cities =
    [
        new City("Port Sable", 3460f, -4768f, 456f),
        new City("Whitestone", -1370f, -3639f, 336f),
        new City("Dry Wells", -2890f, 2198f, 380f),
        new City("Red Mesa", 1530f, 3175f, 330f),
        new City("Tern's Rest", 59f, -5372f, 125f),
        new City("Tallow", 3795f, 2388f, 180f),
        new City("Far Reach", -5166f, -6620f, 180f),
    ];
}
