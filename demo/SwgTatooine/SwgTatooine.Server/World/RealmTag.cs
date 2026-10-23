namespace SwgTatooine;

/// <summary>
/// What kind of place a realm is, as the <c>AppTag</c>'s top nibble names it.
/// </summary>
/// <remarks>
/// The numbers are a wire contract with <c>demo/SwgTatooine.Client/src/data/realm-view.ts</c> and must not be reordered.
/// </remarks>
internal enum RealmScene : uint
{
    Planet = 0,
    Interior = 1,
    Space = 2,
    Dungeon = 3,
}

/// <summary>
/// The demo's <see cref="RealmReplicationConfig.AppTag"/>: everything a renderer needs to choose a scene, in one
/// <c>u32</c> the engine carries opaquely.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so a client never has to reproduce this program's realm arithmetic.</b> An interior is realm
/// <c>Planets + planet · InteriorsPerPlanet + portal</c> (<c>SimBridge.Portals.cs</c>), which depends on two command-line
/// flags a browser cannot see, and a dungeon's id is handed out at run time. A client that decoded realm ids would be
/// re-deriving a layout it has no way to know, and would be silently wrong the first time a flag moved.
/// </para>
/// <para>
/// <b>The scene nibble duplicates the realm KIND deliberately.</b> The kind is on the wire too, as an index into the
/// catalog's names, and a client could branch on the string <c>"interior"</c>. Making it a number here means a rename on
/// this side cannot quietly change what the renderer draws.
/// </para>
/// <para>
/// The layout, which <c>realm-view.ts</c>'s <c>decodeAppTag</c> mirrors field for field:
/// </para>
/// <code>
/// 31..28  scene      0 planet | 1 interior | 2 space | 3 dungeon
/// 27..20  palette    the ground palette a planet is tinted with: 0 sand, 1 green, 2 brown, …
/// 19..12  placeSet   which static place/name table the scene draws (0 = Tatooine's)
/// 11..0   slot       the planet's index, or the interior's or dungeon's index within its planet
/// </code>
/// <para>
/// <b>Realm 0 is deliberately tag 0.</b> Planet 0 is <c>ConfigureSpatialGrid</c>'s realm and carries no
/// <see cref="RealmReplicationConfig"/> of its own, so the engine sends <c>AppTag = 0</c> for it
/// (<c>SubscriptionsRuntime</c>). A tag of 0 decodes to exactly "planet, sand, Tatooine's places, slot 0", which is what
/// planet 0 is — so the default costs no special case on either side.
/// </para>
/// </remarks>
internal static class RealmTag
{
    /// <summary>How many ground palettes the client has. Planets past this reuse one; nothing breaks, they just repeat.</summary>
    internal const int Palettes = 4;

    /// <summary>The tag for planet <paramref name="planet"/>, whose palette is chosen from its index so two planets never look alike.</summary>
    internal static uint Planet(int planet) => Pack(RealmScene.Planet, (uint)(planet % Palettes), 0, (uint)planet);

    /// <summary>The tag for interior <paramref name="portal"/> of a planet. Its palette is unused: an interior draws no ground.</summary>
    internal static uint Interior(int portal) => Pack(RealmScene.Interior, 0, 0, (uint)portal);

    /// <summary>The tag for the space realm, of which there is one.</summary>
    internal static uint Space() => Pack(RealmScene.Space, 0, 0, 0);

    /// <summary>The tag for dungeon <paramref name="slot"/>, whose realm id is handed out at run time.</summary>
    internal static uint Dungeon(int slot) => Pack(RealmScene.Dungeon, 0, 0, (uint)slot);

    /// <summary>
    /// Packs the four fields, each masked to its own width so an out-of-range value cannot corrupt its neighbour.
    /// </summary>
    /// <remarks>
    /// Masking rather than throwing is the right failure here: the tag is cosmetic on the client — it picks a colour and
    /// a label — and a run that refused to start because a 4 097th interior overflowed a field would be trading a working
    /// simulation for a wrong tint.
    /// </remarks>
    private static uint Pack(RealmScene scene, uint palette, uint placeSet, uint slot) =>
        (((uint)scene & 0xFu) << 28) | ((palette & 0xFFu) << 20) | ((placeSet & 0xFFu) << 12) | (slot & 0xFFFu);
}
