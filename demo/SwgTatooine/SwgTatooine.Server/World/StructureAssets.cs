namespace SwgTatooine.Server;

/// <summary>
/// Which published asset a structure IS: the server's answer, which the client is told rather than left to guess.
/// </summary>
/// <remarks>
/// <para>
/// <b>It moved here the day collision started reading descriptors.</b> While an asset only decided what a building
/// looked like, the client could pick one — a rendering decision about a server-placed thing. The moment the server
/// pushes a body out of that asset's walls, the two must name the same asset or the wall you cannot walk through is not
/// the wall you can see. Two copies of a mapping are two answers; this is the one.
/// </para>
/// <para>
/// <b>Published in <c>/typhon/demo.json</c></b> so the client reads it instead of holding a second table. A kind with
/// several assets is cycled by netId, which is a rule rather than a table — so the rule travels, not its output.
/// </para>
/// </remarks>
internal static class StructureAssets
{
    /// <summary>Means "this structure has no published asset": it keeps the footprint box the world planted it with.</summary>
    internal const int NoAsset = 255;

    /// <summary>
    /// Every asset a structure may be, in the order the wire's index counts them.
    /// </summary>
    /// <remarks>
    /// <b>Published in <c>/typhon/demo.json</c> and indexed by <c>Structure.Asset</c>.</b> The client looks an index up
    /// in this list rather than holding its own copy of the kind-to-asset rule — the rule is the server's, because the
    /// server pushes bodies out of the walls of whatever it names. Append only: an index is on the wire.
    /// </remarks>
    internal static readonly string[] Table =
    [
        "tat_house_large",
        "tat_house_small",
        "tat_shuttleport",
        "bazaar_terminal",
        "crafting_station",
        "mission_terminal",

        // The rooms behind the houses' doors (Realms G1b): a room is a structure of its interior realm, standing at that realm's origin. Never picked by
        // kind — a room is the asset its house's door names (AssetColliders.DoorPlan.TargetAsset).
        "tat_house_large_interior",
        "tat_house_small_interior",
    ];

    /// <summary>
    /// The kind each asset stands for, parallel to <see cref="Table"/> — the inverse of <see cref="Pick"/>.
    /// </summary>
    /// <remarks>
    /// <b>It exists so an asset can be compared against the ground the world reserved for it.</b>
    /// <see cref="AssetColliders.MaxOverhangM"/> needs to know, per asset, how far its walls reach past that
    /// reservation, and the reservation is a property of the KIND (<see cref="StructureFootprint.HalfExtentM"/>) while
    /// the walls are a property of the ASSET. <see cref="Pick"/> maps one way and draws from an rng, so it cannot be
    /// asked the question backwards. <c>StructureAssetChecks.EveryAssetIsPickedOnlyByTheKindItNames</c> pins the two
    /// tables against each other, so an asset appended to one and forgotten in the other fails rather than quietly
    /// reserving the wrong ground.
    /// </remarks>
    internal static readonly int[] KindOf =
    [
        StructureKind.Building,
        StructureKind.PlayerHouse,
        StructureKind.Shuttleport,
        StructureKind.Terminal,
        StructureKind.Terminal,
        StructureKind.Terminal,
        StructureKind.Interior,
        StructureKind.Interior,
    ];

    /// <summary>The asset a structure of this kind is, as an index into <see cref="Table"/>.</summary>
    /// <param name="kind">A <see cref="StructureKind"/>.</param>
    /// <param name="rng">Draws between equivalent assets; a city's terminals are then not all the same object.</param>
    /// <returns>An index, or <see cref="NoAsset"/>.</returns>
    /// <remarks>
    /// The footprints line up with <see cref="StructureFootprint.HalfExtentM"/>, which is why this is a table and not a
    /// scale: a building is 18 x 18 m and the large house is 18.8 x 18.2; a player house is 14 x 14 against the small
    /// house's 11.1 x 13.5. Assets are authored in metres and are never scaled to fit.
    /// </remarks>
    internal static int Pick(int kind, ref Rng rng) => kind switch
    {
        StructureKind.Building => 0,
        StructureKind.PlayerHouse => 1,
        StructureKind.Shuttleport => 2,
        StructureKind.Terminal => 3 + rng.NextInt(0, 3),

        // PoiProp, Factory, Harvester, CampObject: nothing published stands in for them. Interior: the door decides, not the kind.
        _ => NoAsset,
    };

    /// <summary>The asset id an index names, or <see langword="null"/> for one that names none.</summary>
    /// <param name="index">A <c>Structure.Asset</c>.</param>
    /// <returns>A catalogue asset id.</returns>
    internal static string Of(int index) => index >= 0 && index < Table.Length ? Table[index] : null;
}
