namespace SwgTatooine;

/// <summary>
/// Tatooine as Star Wars Galaxies had it, in the game's own coordinates.
/// </summary>
/// <remarks>
/// <para><b>Provenance is per-constant and it matters.</b> Three tiers appear here, and they are not equally trustworthy:</para>
/// <list type="bullet">
///   <item><b>[CORE3]</b> — a compiled constant read out of SWGEmu's Core3, the open-source reimplementation of the
///   pre-CU server. These are the strongest: they are what a server actually ran, not what anyone remembers.</item>
///   <item><b>[WIKI]</b> — a coordinate or figure from the SWG community wikis. Reliable for "where is Mos Eisley",
///   because thousands of people walked there, but the precision is a waypoint someone typed, and sources disagree by
///   tens of metres. Noted where they disagree.</item>
///   <item><b>[EST]</b> — mine. Nobody datamined how many buildings Mos Eisley holds or how many creatures were alive on
///   a planet at once, so the counts below are reasoned from what IS known and are labelled as invention. They set the
///   workload's scale, so they are the numbers to argue with.</item>
/// </list>
/// </remarks>
public static class TatooineData
{
    // ── The planet ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CORE3] Planet edge in metres. Every SWG planet is 16 384 x 16 384, built from one-metre tiles, with coordinates
    /// running -8192..+8192 on X and Z. Verified as <c>coordinateMin</c>/<c>coordinateMax</c> in Core3's
    /// <c>AiAgent.idl</c>, and corroborated by Raph Koster (SWG's creative director) writing about the terrain system.
    /// </summary>
    public const float PlanetEdgeM = 16384f;

    /// <summary>[CORE3] Coordinate bound on each axis — half the planet edge.</summary>
    public const float PlanetHalfExtentM = 8192f;

    // ── Spatial and behavioural constants ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CORE3] Default distance at which a hostile NPC notices a player. <c>DEFAULTAGGRORADIUS = 24</c> in
    /// <c>AiAgent.idl</c>. Strikingly small next to the map — a 24 m bubble on a 16 km planet — and it is the reason a
    /// creature's spatial query is cheap while a player's awareness query is not.
    /// </summary>
    public const float AggroRadiusM = 24f;

    /// <summary>[CORE3] How far a creature will chase before giving up and returning: <c>MAX_OOS_RANGE = 75</c>.</summary>
    public const float MaxChaseRangeM = 75f;

    /// <summary>[CORE3] Default flee range, and the radius a creature will not leave its lair by.</summary>
    public const float LeashRadiusM = 192f;

    /// <summary>
    /// [CORE3] AI behaviour tick, in milliseconds: <c>BEHAVIORINTERVALMIN/MID/MAX = 400/700/1000</c>. Creatures do NOT
    /// think every server tick — they think between two and a half and one times a second, staggered.
    /// </summary>
    public const int AiIntervalMinMs = 400;

    public const int AiIntervalMaxMs = 1000;

    /// <summary>[WIKI] Melee weapons reach 0-6 m.</summary>
    public const float MeleeRangeM = 6f;

    /// <summary>[WIKI] Ranged weapons reach 0-75 m, which is also the chase cap — not a coincidence.</summary>
    public const float RangedRangeM = 75f;

    /// <summary>[CORE3, from the printed manual] Spatial <c>/say</c> carries 50 m.</summary>
    public const float SayRangeM = 50f;

    /// <summary>
    /// [CORE3] Interest-management radius: <c>ZoneServer::CLOSEOBJECTRANGE = 192</c> (<c>ZoneServer.idl</c>). The range
    /// argument to essentially every "who can see me" call on the ground; space uses
    /// <c>SPACECLOSEOBJECTRANGE = 2048</c>.
    /// </summary>
    /// <remarks>
    /// <para>This was reasoned to before it was found. The first pass could not locate a global constant — Core3's
    /// <c>Zone::getInRangeObjects(x, z, y, range, ...)</c> takes the radius as a call parameter — so 192 m was chosen as
    /// an upper bound because it is the largest radius the AI's own behaviour constants use. Mining the source turned up
    /// the real constant and it is the same number. The reasoning is left here because it was the reasoning, not because
    /// it is still needed.</para>
    /// <para>Two details worth knowing if this is ever tuned. A BUILDING gets four times the radius —
    /// <c>CLOSEOBJECTRANGE * 4 = 768</c> — and the quadtree's own refresh scan is
    /// <c>min(range * 2, 768)</c>: it always searches twice the nominal range to catch what is about to enter it, and
    /// caps at exactly the building figure.</para>
    /// </remarks>
    public const float AwarenessRadiusM = 192f;

    /// <summary>[CORE3] A building's awareness radius — <c>CLOSEOBJECTRANGE * 4</c>, and the cap on any refresh scan.</summary>
    public const float BuildingAwarenessRadiusM = 768f;

    /// <summary>[WIKI] Player run speed on foot, unencumbered.</summary>
    public const float PlayerRunSpeedMps = 5f;

    /// <summary>[WIKI] Mount and speeder speed, the middle of the 11.8-12.5 range across mount types.</summary>
    public const float PlayerMountSpeedMps = 12f;

    /// <summary>
    /// [WIKI] Weapon delay floor. Combat is continuous and gated per weapon by
    /// <c>delay = baseSpeed x (1 - speedSkillMod/100)</c>, floored at one second; a pistol at +50 skill hits this floor
    /// and a rifle lands near three seconds. There is no discrete combat round.
    /// </summary>
    public const float MinAttackDelaySec = 1f;

    public const float MaxAttackDelaySec = 3f;

    /// <summary>[WIKI] A mission terminal lets a player hold two missions at a time.</summary>
    public const int MaxConcurrentMissions = 2;

    /// <summary>
    /// [CORE3] Minimum and maximum distance from the player at which a destroy mission places its target.
    /// </summary>
    /// <remarks>
    /// <para><c>MissionManagerImplementation::randomizeGenericDestroyMission</c> computes
    /// <c>base + difficultyFactor * level + random(randomDistance) + random(difficultyRandomDistance * level)</c>, and the
    /// shipped configuration (<c>mission_manager.lua</c>) sets <c>base = 1000</c>, <c>randomDistance = 1000</c> and BOTH
    /// difficulty terms to zero — so in practice it is a uniform 1 000-2 000 m at a random bearing, and mission
    /// difficulty does not move the target further away. Twenty tries; the point must be in bounds, on dry land, and
    /// outside every city region, or the mission is simply not offered.</para>
    /// <para><b>A destroy-mission target is ONE object, not a camp.</b> The waypoint first shows a 128 m
    /// <c>MissionSpawnActiveArea</c>; on approach the real position is jittered +/-256 m from it and rejection-tested up
    /// to 256 times. What spawns there is a single <c>LairObject</c> with
    /// <c>difficulty * (900 + random(200))</c> hit points, and its defenders arrive as mobiles clustered around it in
    /// three waves gated on the lair's damage: one at spawn, one at first damage, one at half health. Once killed they
    /// do not come back — <c>checkRespawn</c> short-circuits for a destroy-mission lair.</para>
    /// </remarks>
    public const float DestroyMissionMinDistanceM = 1000f;

    public const float DestroyMissionMaxDistanceM = 2000f;

    // ── Player city tiers ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CORE3] Player-city radius by rank, in metres, from <c>city_manager.lua</c>: Outpost 150, Village 200,
    /// Township 300, City 400, Metropolis 450.
    /// </summary>
    public static readonly float[] PlayerCityRadiusM = [150f, 200f, 300f, 400f, 450f];

    /// <summary>[CORE3] Citizens each rank requires: 2, 4, 6, 8, 10.</summary>
    public static readonly int[] PlayerCityCitizens = [2, 4, 6, 8, 10];

    /// <summary>
    /// [CORE3] Minimum separation between two player cities, from <c>CityManagerImplementation.cpp</c>, which compares
    /// <c>squaredDistanceTo &lt; 1024 * 1024</c>. A thousand metres, and it is why player cities never form a
    /// contiguous sprawl however many players build.
    /// </summary>
    public const float PlayerCityMinSeparationM = 1000f;

    // ── Cities ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [WIKI coordinates, EST extents and populations] The seven NPC cities.
    /// </summary>
    /// <remarks>
    /// <para>Coordinates are the community waypoints; sources vary by tens of metres and that variance is immaterial at
    /// this scale. The radii, building counts and NPC counts are MINE — no per-city inventory was ever datamined, and
    /// lore figures like "Mos Eisley, population 60 000" are flavour text with no mechanical basis. What is documented is
    /// the qualification rule: a settlement is a city rather than an outpost when it has a cloning facility, a cantina, a
    /// starport or shuttleport and a medical facility. So the counts below are scaled from that floor by how much each
    /// settlement is known to have beyond it.</para>
    /// <para>Player weights follow the same reasoning: Mos Eisley was the planet's hub and everybody passed through it.</para>
    /// <para><b>Three radii and one centre are no longer estimates.</b> Core3 declares the cities as regions in
    /// <c>tatooine_regions.lua</c> — <c>{"mos_eisley", 3460, -4768, {CIRCLE, 456}, CITY+NOSPAWNAREA}</c>, Bestine 336 m,
    /// Anchorhead 125 m. They are tagged <c>NOSPAWNAREA</c>, so no creature spawning happens inside a city at all; its
    /// NPCs are hand-placed. Anchorhead in particular is far smaller than the estimate had it (125 against 220), which
    /// makes its NPC population correspondingly denser.</para>
    /// </remarks>
    public static readonly CityDef[] Cities =
    [
        //          name            x        z       radius  bldgs  npcs  weight
        new CityDef("Mos Eisley",    3460f,  -4768f,  456f,   190,   340,  0.34f),   // [CORE3] centre and radius
        new CityDef("Bestine",      -1370f,  -3639f,  336f,   140,   240,  0.18f),   // [CORE3] radius
        new CityDef("Mos Espa",     -2890f,   2198f,  380f,   135,   220,  0.16f),
        new CityDef("Mos Entha",     1530f,   3175f,  330f,   105,   170,  0.13f),
        new CityDef("Anchorhead",      59f,  -5372f,  125f,    55,    80,  0.08f),   // [CORE3] radius
        new CityDef("Mos Taike",     3795f,   2388f,  180f,    38,    55,  0.06f),
        new CityDef("Wayfar",       -5166f,  -6620f,  180f,    35,    50,  0.05f),
    ];

    // ── Points of interest ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [WIKI coordinates, EST extents and contents] The named landmarks players actually travelled to.
    /// </summary>
    /// <remarks>
    /// Where two sources disagree the first is used and the alternative is noted. Kenobi's hut is the worst case — the
    /// current wiki says (-4512, -2270) and an older map says (-4773, -3009), which is 800 m apart and probably a real
    /// map revision rather than a transcription error.
    /// </remarks>
    public static readonly PoiDef[] Pois =
    [
        //         name                       x        z      radius props lairs
        new PoiDef("Krayt Dragon Graveyard",  7450f,   4531f,  520f,   85,   14),   // "stretches over 1000m" per the wiki
        new PoiDef("Jabba's Palace",         -5962f,  -6259f,  190f,   45,    6),   // alt (-5868,-6189), (-5856,-6183)
        new PoiDef("Great Pit of Carkoon",   -6169f,  -3387f,  150f,   22,    4),   // alt (-6183,-3371)
        new PoiDef("Fort Tusken",            -3980f,   6311f,  230f,   60,   12),   // alt (-3966,6267)
        new PoiDef("Jawa Fortress",          -6141f,   1854f,  170f,   38,    7),   // alt (-6110,1892)
        new PoiDef("Kenobi Homestead",       -4512f,  -2270f,   90f,   12,    2),   // older map: (-4773,-3009)
        new PoiDef("Lars Homestead",         -2579f,  -5500f,  110f,   16,    2),   // alt (-2600,-5500)
    ];

    // ── Wilderness spawn regions ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EST throughout] The wilderness. Nothing about creature density on a live planet was ever published, so these
    /// regions and their densities are invented — but invented against a real constraint: the regions are placed on the
    /// named terrain features of the Tatooine map, and the densities (13-22 lairs per square kilometre) put roughly one
    /// lair every 220-280 m inside a spawn area, which is about how often SWG actually put one in front of you. Over the
    /// ~116 km2 the regions cover that is near 2 000 lairs and, at four to six creatures each, some 9 000 creatures — the
    /// order a planet holding a few hundred players needs to keep them all in content.
    /// </summary>
    /// <remarks>
    /// The point of having named regions at all, rather than a uniform sprinkle, is that a real planet's creature
    /// population is CLUMPED — and clumping is the whole difference between a cluster layer that can prune and one that
    /// cannot. A uniform sprinkle would have made this workload a restatement of the density, which is the trap the
    /// earlier EVE scenario fell into.
    /// </remarks>
    public static readonly SpawnRegionDef[] SpawnRegions =
    [
        //                  name                       x        z     radius  lairs/km²  template
        new SpawnRegionDef("Northern Dune Sea",     -1200f,   5600f,  2600f,   18f,   CreatureTemplates.Bantha),
        new SpawnRegionDef("Western Dune Sea",      -6200f,   4200f,  2100f,   15f,   CreatureTemplates.TuskenRaider),
        new SpawnRegionDef("Jundland Wastes",       -3600f,  -1400f,  2400f,   22f,   CreatureTemplates.Squill),
        new SpawnRegionDef("Xelric Draw",            5400f,  -1500f,  1900f,   20f,   CreatureTemplates.WompRat),
        new SpawnRegionDef("Eastern Wastes",         6100f,   3000f,  2200f,   14f,   CreatureTemplates.Ronto),
        new SpawnRegionDef("Southern Wastes",         800f,  -6800f,  2300f,   17f,   CreatureTemplates.WompRat),
        new SpawnRegionDef("Mos Espa Approach",     -3400f,    600f,  1500f,   21f,   CreatureTemplates.Squill),
        new SpawnRegionDef("Arid Flats",             2200f,    400f,  2000f,   13f,   CreatureTemplates.Bantha),
    ];
}

/// <summary>
/// The creature kinds the wilderness spawns. Real Tatooine fauna, with statistics that are [EST] — SWG's creature
/// tables were per-template and enormous, and reproducing them would add variety without adding load.
/// </summary>
public static class CreatureTemplates
{
    public const int WompRat = 0;
    public const int Squill = 1;
    public const int Bantha = 2;
    public const int TuskenRaider = 3;
    public const int Ronto = 4;
    public const int MissionDefender = 5;

    public const int Count = 6;

    /// <summary>Creatures a lair of each template keeps alive. [EST], shaped by how SWG lairs actually felt: 4-6 typical.</summary>
    public static readonly int[] SpawnLimit = [6, 5, 4, 5, 3, 8];

    /// <summary>Hit points. [EST].</summary>
    public static readonly int[] Health = [180, 260, 900, 640, 1100, 420];

    /// <summary>Damage per attack. [EST].</summary>
    public static readonly int[] Damage = [12, 18, 45, 38, 30, 26];

    /// <summary>Movement speed in metres per second. [EST], below a player's 5 m/s so a player can always disengage.</summary>
    public static readonly float[] SpeedMps = [3.4f, 3.0f, 2.2f, 4.2f, 2.0f, 3.6f];

    /// <summary>
    /// Whether the template attacks on sight. [EST]. Banthas and rontos are famously passive; womp rats, squills and
    /// Tuskens are not. A passive creature never runs an aggro query, which is a real difference in cost.
    /// </summary>
    public static readonly bool[] Aggressive = [true, true, false, true, false, true];

    /// <summary>
    /// [CORE3] How far a lair scatters its creatures: the <c>LairSpawn.size</c> field, which every Tatooine template
    /// sets to 25 m. Mobiles jitter +/-size around the lair.
    /// </summary>
    /// <remarks>
    /// The first pass estimated 40-100 m from how a lair "felt". The real number is 25 and it is uniform across
    /// templates, which makes a lair a considerably TIGHTER knot of entities than the estimate did — and tightness is
    /// exactly the property the cluster layer is being measured on, so the correction matters more here than its size
    /// suggests.
    /// </remarks>
    public static readonly float[] LairSpawnRadius = [25f, 25f, 25f, 25f, 25f, 25f];
}
