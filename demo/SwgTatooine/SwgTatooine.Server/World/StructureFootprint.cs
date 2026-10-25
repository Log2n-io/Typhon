namespace SwgTatooine.Server;

/// <summary>
/// How much ground each kind of structure occupies, as a half-extent in metres.
/// </summary>
/// <remarks>
/// <para>
/// <b>These numbers are the browser's, and the two copies have to agree.</b> The client draws a structure at the size its
/// style table gives the kind (<c>demo/SwgTatooine/SwgTatooine.Client/src/render/styles.ts</c>, <c>STRUCTURE_SIZES</c>); the server
/// decides where one may stand. If the server plans with a smaller box than the client draws, buildings overlap on screen
/// while the plan says they do not — which is exactly the defect this type exists to end. A shared assembly is not
/// available (the client is TypeScript), so the duplication is deliberate and <c>SitePlannerChecks.TheFootprintsMatchWhatTheClientDraws</c> pins it.
/// </para>
/// <para>
/// <b>Every footprint is a SQUARE, and that is what makes the overlap test exact rather than conservative.</b> The client
/// yaws a structure by <c>gridYaw</c>, which is a multiple of 90 degrees — so a square's silhouette is the same box
/// whatever the draw, and two structures overlap exactly when their axis-aligned boxes do. A circumscribed circle would
/// have been the usual conservative stand-in and it is not good enough here: at Anchorhead, 55 buildings of radius 12.7 m
/// in a 125 m disc is 57 % of the area, past the 54.7 % where random sequential placement of discs jams and no amount of
/// retrying finds a spot. The squares are 44.8 %, which places comfortably.
/// </para>
/// </remarks>
public static class StructureFootprint
{
    /// <summary>The gap left between two structures, in metres: a street rather than a shared wall.</summary>
    /// <remarks>
    /// Deliberately small, and counted once for a PAIR rather than once per box. The difference is not cosmetic: at
    /// Anchorhead, the tightest city on the map, charging both boxes puts the exclusion square at 22 m and the packing
    /// at 54 % -- the limit where random sequential placement jams, measured as one building the planner could not fit
    /// and two overlapping pairs. Charged once it is a 20 m square and 44 %, which plants with attempts to spare.
    /// </remarks>
    public const float MarginM = 2f;

    /// <summary>The most of a site's ground its structures may exclude before placement starts to jam.</summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not chosen.</b> Sweeping the building count of a 125 m disc (Anchorhead, the tightest city on the map) over 64 seeds each:
    /// </para>
    /// <code>
    ///   n   packed   seeds that gave up / 64
    ///  58    47.3 %    0
    ///  59    48.1 %    0
    ///  60    48.9 %    0     &lt;- last clean count
    ///  61    49.7 %    1
    ///  63    51.3 %   10
    ///  68    55.4 %   42
    /// </code>
    /// <para>
    /// So the ceiling sits just under 49 % and the cliff past it is steep. 48 % keeps a seed's worth of margin below the last clean count and still leaves
    /// Anchorhead — 44.8 % at the real planet size — three buildings of headroom, so the authentic map clamps nothing.
    /// </para>
    /// </remarks>
    public const float MaxPacking = 0.48f;

    /// <summary>How many structures of one kind a disc can hold without them standing inside each other.</summary>
    /// <param name="radiusM">The site's radius in metres.</param>
    /// <param name="kind">A <see cref="StructureKind"/>, whose footprint sets the exclusion square.</param>
    /// <returns>The most that may be asked for; at least one.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why a site needs to be asked this at all.</b> A city's building count is a fact about Star Wars Galaxies and its radius is a coordinate, so
    /// <c>--world</c> scales the radius and leaves the count alone (<see cref="TatooineMap.Build"/>). Compress the world and the same buildings are asked
    /// to stand in a fraction of the ground: at <c>--world 2</c> Mos Eisley's 456 m disc becomes 55.7 m and is still asked for 190 structures, which is
    /// 780 % of its area. No arrangement exists, so the honest answer is to plant fewer and say so rather than to search for one.
    /// </para>
    /// <para>
    /// <b>At least one, always.</b> A city with no shuttleport is not a city — it is unreachable by the only transport the world has — so a disc too small
    /// for even one structure gets its port anyway and lets it overhang.
    /// </para>
    /// <para>
    /// Computed on one kind's footprint rather than on the mix a caller will actually plant. Eight of every nine city structures are buildings, and the
    /// error in the other two directions cancels: the dome excludes about three buildings' worth and a crafting station about a thirtieth of one.
    /// </para>
    /// </remarks>
    public static int CapacityOf(float radiusM, int kind)
    {
        var span = (2f * HalfExtentM(kind)) + MarginM;
        return System.Math.Max(1, (int)(MaxPacking * System.MathF.PI * radiusM * radiusM / (span * span)));
    }

    /// <summary>The half-extent of one kind, in metres.</summary>
    /// <param name="kind">A <see cref="StructureKind"/>.</param>
    /// <returns>Half the edge of the square it occupies.</returns>
    /// <remarks>
    /// The default is the building's, not zero: an unknown kind is a kind this table has not been taught yet, and giving
    /// it no footprint would let it be planted inside something else — a silent wrong answer in place of a loud one.
    /// </remarks>
    public static float HalfExtentM(int kind) => kind switch
    {
        StructureKind.Building => 9f,        // 18 x 18
        StructureKind.CraftingStation => 0.8f,      // 1.6 x 1.6
        // The published terminal and the stand point in front of it with its 0.3 m body square (blender-models, 2026-10-24): `bazaar_terminal` reaches
        // 0.65 m forward and is stood at 1.1 m; `mission_terminal` 0.26 m and 0.65 m. Reserved, or a neighbour is planted on the spot a user stands.
        StructureKind.Bazaar => 1.4f,
        StructureKind.MissionTerminal => 1.0f,
        // 46.2 x 46.2, the published `tat_shuttleport`'s own footprint (2026-10-22). It was 16 (32 x 32) while the asset
        // was 46 across, so a dome stood over the ground its neighbours were planted on. The asset is what a shuttleport
        // IS, so the reservation follows it rather than the other way round; the cost is ~1 100 m2 more of a city's disc,
        // which the build reports as `Crowded` if any city cannot afford it.
        StructureKind.Shuttleport => 23.1f,
        StructureKind.PoiProp => 2.5f,       // 5 x 5
        StructureKind.PlayerHouse => 7f,     // 14 x 14
        // 6 x 6 round the published `factory` (catalogue 11): 5.7 m wide and 4.7 m deep, and the stand point at its console 2.1 m in front with its
        // 0.3 m body reaches 2.4. It was a 22 m box while nothing was published for it.
        StructureKind.Factory => 3f,
        // 6.8 x 6.8, the published `resource_extractor`'s 6.7 x 5.6 m pad. It was 6 x 6.
        StructureKind.Harvester => 3.4f,
        StructureKind.CampObject => 2f,      // 4 x 4
        // 28 x 28, around a room drawn at its realm's origin: the town hall's reaches 14 m from it, the large house's 9.84 m, the small one's 5.6. It was 20 x
        // 20 before the town hall, and one size for every room keeps a room's broad-phase box what it always was: everything in the realm.
        StructureKind.Interior => 14f,
        // 35.6 x 35.6 round the published `tat_townhall`'s origin (2026-10-25), which is not its middle: 30.4 m wide, it runs from 9.6 m behind the origin to
        // its ramp's foot 17.75 m in front, and a square centred on the origin has to reach the ramp. The 8 m it reserves behind the back wall is the price
        // of the one rule every structure keeps — its box is centred where it stands — which collision, the push-out and the layout checks all read.
        StructureKind.TownHall => 17.8f,
        _ => 9f,
    };
}
