using System;

namespace SwgTatooine.Server;

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
    /// How far from its lair a creature WANDERS, metres (Loïc, 2026-10-24: "they should be in a 20 m radius max of the lair"). A chase may still take it
    /// out to <see cref="LeashRadiusM"/>; the next leg after one brings it back.
    /// </summary>
    /// <remarks>
    /// The leash used to be the wander bound too — legs drawn out to 0.7 × 192 m — which spread a lair's creatures over 130 m and left the nest standing
    /// alone. A lair reads as a place only if what lives in it stays round it.
    /// </remarks>
    public const float LairWanderRadiusM = 20f;

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

    /// <summary>
    /// [EST] The share of the scripted crowd that fights back when a player attacks it (Loïc, 2026-10-26: "either run away or fight back, but not standing
    /// waiting to die"); the rest run. A body's temperament is its own, from its id, so it answers every attack the same way.
    /// </summary>
    public const float CrowdBraveShare = 0.6f;

    /// <summary>[EST] Under this share of its health a body that fought back breaks off and runs.</summary>
    public const float CrowdFleeBelowHealth = 0.3f;

    /// <summary>[EST] How far a body runs from its attacker on each blow, metres: past a pistol's 30 m, inside the crowd's own 75 m.</summary>
    public const float CrowdFleeM = 60f;

    /// <summary>
    /// [EST] How long a reaction holds before the activity mix decides again, seconds; every blow renews it. Also what makes a blow the start of a new
    /// incident: one landing after this long without a hit shouts and calls the crowd round it.
    /// </summary>
    public const float CrowdReactSeconds = 20f;

    /// <summary>[EST] How far round the body attacked the crowd joins in, metres: a group standing together, not the street.</summary>
    public const float CrowdAllyRadiusM = 20f;

    /// <summary>[EST] The most bodies that join one attack: a fight, not a riot — and a bound on what one blow writes.</summary>
    public const int CrowdAllyMax = 6;

    /// <summary>[EST] How far from a city's edge a body running from an attacker still makes for the city rather than straight away, metres.</summary>
    public const float CrowdFleeToCityM = 400f;

    /// <summary>
    /// How far each combat stance reaches, metres, between the two bodies' EDGES (Loïc, 2026-10-24): unarmed 1, one-handed 3, pistol 30. Indexed by
    /// <c>Stances</c>; no stance reaches nothing, because it does not fight.
    /// </summary>
    /// <remarks>
    /// <b>Edge to edge, not centre to centre</b>: a creature's body keeps the player's at a distance of its own size — a spider is 1.8 m long — so a reach of
    /// one metre between centres could not be met by a fist on anything bigger than a rat.
    /// </remarks>
    public static readonly float[] StanceReachM = [0f, 1f, 3f, 30f];

    /// <summary>
    /// How often a stance strikes, seconds: about its attack clip's length and a short pause (the clips are <c>Punch</c> 0.75 s, <c>SwordSlash</c>
    /// 1.04 s, <c>Shoot_OneHanded</c> 0.54 s), so a client that plays a clip per hit shows one blow after another. Indexed by <c>Stances</c>.
    /// </summary>
    public static readonly float[] StanceDelaySec = [0f, 1.2f, 1.5f, 1f];

    /// <summary>How long a knocked-out player lies before it gets up, seconds (Loïc, 2026-10-24).</summary>
    public const float KnockOutSec = 20f;

    /// <summary>The share of its health a knocked-out player gets up with (Loïc, 2026-10-24).</summary>
    public const float KnockOutRecoveryFraction = 0.2f;

    /// <summary>How long a dead player lies before it wakes as a clone, seconds.</summary>
    public const float CloneDelaySec = 10f;

    /// <summary>
    /// The share of its health a player out of a fight gets back each second: a hundredth, so a nearly dead player is whole again in under two minutes of
    /// standing about. "Out of a fight" is no stance for a person's player and anything but <c>Combat</c> for the crowd's.
    /// </summary>
    public const float RegenPerSecFraction = 0.01f;

    /// <summary>
    /// How long a player must go unhit before it heals, seconds (Loïc, 2026-10-24: "only when I'm not being attacked"). A player out of a stance that
    /// something is still hitting is being attacked, and heals nothing.
    /// </summary>
    public const float RegenQuietSec = 5f;

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

    /// <summary>Half the width of a player's body, in metres: what collision pushes out of a wall.</summary>
    /// <remarks>
    /// <para>
    /// <b>0.3, from the published character's own capsule</b> (`casual2_male`: radius 0.3, half-height 0.525 for a
    /// 1.65 m body). It was 1 — a literal written when nothing in the world had an interior, so a body was a
    /// person-sized thing that had to not overlap a box, and two metres across was as good an answer as any.
    /// </para>
    /// <para>
    /// <b>It stops being as good an answer as any the moment a building has a door.</b> The published doorways are
    /// 1.2 m wide, which leaves 0.6 m from the centre line to each jamb, and the portal volume in front of a door is
    /// 0.8 m deep: a body of half-extent 1 is pushed out before it reaches either, so it could never enter a building
    /// and could never trigger a portal. At 0.3 it fits with 0.3 m of clearance each side.
    /// </para>
    /// <para>
    /// <b>Smaller is also cheaper, which is why this needed no measurement to justify.</b> The clear-disc test is the
    /// distance to the nearest surface minus the body's own half-extent, so a smaller body holds more slack and the
    /// cached disc survives more steps — the 83.2 % of steps that already skip the spatial layer can only go up. The
    /// push-out does less work for the same reason.
    /// </para>
    /// <para>
    /// The 2-D sim gives a body a SQUARE, not a circle: a half-extent of 0.3 is a 0.6 m square, which circumscribes the
    /// 0.6 m-diameter capsule the asset declares. Creatures keep their own 1.5 (<c>WorldBuilder.SpawnCreature</c>)
    /// until something non-human has a reason to go indoors.
    /// </para>
    /// </remarks>
    public const float PlayerBodyHalfM = 0.3f;

    /// <summary>
    /// The outfits a player character may wear, by the published asset id; <see cref="Character.Outfit"/> is one plus an index into this. Published to the
    /// client (<c>AssetOrigin.DemoJson</c>, <c>players</c>) for the reason <see cref="CreatureTemplates.Bodies"/> is: the server says what a body wears.
    /// </summary>
    /// <remarks>
    /// The pack's civilian characters. Not its enemies' — a ninja or a soldier walking a town would read as a camp's — and not <c>base_character</c>, which is
    /// the rig's reference body rather than a person. <b>Append only</b>: a character stores an index, so reordering this re-dresses every saved character.
    /// </remarks>
    public static readonly string[] PlayerOutfits =
    [
        "casual2_male",
        "casual2_female",
        "casual3_male",
        "casual3_female",
        "casual_male",
        "casual_female",
        "casual_bald",
        "kimono_male",
        "kimono_female",
    ];

    /// <summary>
    /// The outfit a new character is given: one plus an index into <see cref="PlayerOutfits"/>, mixed from the account so it is stable for an account and spread
    /// across accounts created in a run.
    /// </summary>
    /// <param name="account">The account the character is created for.</param>
    /// <returns>A <see cref="Character.Outfit"/> value, never 0.</returns>
    /// <remarks>
    /// <b>Mixed, not taken modulo</b>: an entity id is a packed value whose low bits are the engine's to lay out, so <c>raw % count</c> would tie a look to
    /// how ids happen to be allocated. The MurmurHash3 finaliser makes it depend on the whole id, as the client's <c>outfitFor</c> does for a netId.
    /// </remarks>
    public static byte OutfitFor(EntityId account, byte chosen)
        => IsOutfit(chosen) ? chosen : OutfitFor(account);

    /// <summary>Whether <paramref name="outfit"/> names one of <see cref="PlayerOutfits"/>, one-based. 0 does not: it is "none".</summary>
    public static bool IsOutfit(int outfit) => outfit >= 1 && outfit <= PlayerOutfits.Length;

    /// <summary>The outfit a new character is given when its account chose none; see <see cref="OutfitFor(EntityId)"/>.</summary>
    /// <param name="account">The account the character is created for.</param>
    /// <returns>A <see cref="Character.Outfit"/> value, never 0.</returns>
    public static byte OutfitFor(EntityId account)
    {
        var h = account.RawValue;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return (byte)(1 + (int)(h % (ulong)PlayerOutfits.Length));
    }

    /// <summary>
    /// How high a step the body climbs without being stopped by it, in metres.
    /// </summary>
    /// <remarks>
    /// <b>This is the number that decides what a wall IS.</b> A collider whose top is within a step of the feet is
    /// something the body steps onto; anything taller is something it walks into. Measured against the published
    /// assets by the `blender-models` session: the house door steps are 0.30-0.36 m and the shuttleport's deck stands
    /// 1.26 m above the plaza, reached by its own stairs and ramps rather than in one step. 0.4 is the testbench's
    /// value (`physics/character-motor.ts`), so the two sides agree about what is climbable.
    /// </remarks>
    public const float PlayerStepM = 0.4f;

    /// <summary>How tall the body is, in metres: a collider starting above this is walked under.</summary>
    /// <remarks>From the published character capsule, and the testbench's `height`.</remarks>
    public const float PlayerHeightM = 1.65f;

    /// <summary>How far a body follows the ground down in one tick, in metres — walking down steps and ramps.</summary>
    /// <remarks>
    /// Without it a body walking off a ledge leaves the ground for a tick and the altitude it replicates jumps. The
    /// testbench calls it `snapDistance` and uses the same figure.
    /// </remarks>
    public const float PlayerSnapM = 0.45f;

    /// <summary>[WIKI] Player run speed on foot, unencumbered.</summary>
    public const float PlayerRunSpeedMps = 5f;

    /// <summary>
    /// What a SESSION's gait ladder is multiplied by: 2, so a player walks, runs and rides at twice the sourced figures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked for to make the demo quicker to move around in</b> (Loïc, 2026-10-21), and applied to the ladder a session
    /// steers with rather than to the speeds above. Scripted players, creatures, shuttles and portal arrivals read
    /// <see cref="PlayerRunSpeedMps"/> and <see cref="PlayerMountSpeedMps"/> directly and keep their own pace, so the world this demo exists to
    /// MEASURE moves exactly as it did — only the person playing is faster.
    /// </para>
    /// <para>
    /// <b>It scales the teleport ceiling with it, and that is not optional.</b>
    /// <see cref="Replication.TatooineReplication.MaxSpeedMps"/> is sized from the top speed, so a ladder scaled without it
    /// would have the engine read a legitimate boosted swoop as a teleport. The width that ceiling buys was computed when
    /// the ladder was last raised: 75.6 m/s needs 12 387 of a 16-bit code's 32 767, which leaves 2.6x — and the swoop at
    /// the largest boost, 168 m/s with its headroom, spends 2.2x of it.
    /// </para>
    /// </remarks>
    public const float PlayerGaitScale = 2f;

    /// <summary>[WIKI] Mount and speeder speed, the middle of the 11.8-12.5 range across mount types.</summary>
    /// <remarks>
    /// The scripted crowd's mount: the community's measured figure for the X-34, XP-38, V-35 and AB-1 is 765 m/min = 12.75 m/s,
    /// and the swoop's 882 m/min = 14.7 m/s is the fastest thing anyone rode. A session's swoop is its own figure,
    /// <see cref="PlayerSwoopSpeedMps"/>.
    /// </remarks>
    public const float PlayerMountSpeedMps = 12f;

    /// <summary>A player riding a swoop: four times a run (Loïc, 2026-10-24: "we'll adjust if needed"), 20 m/s before the gait scale.</summary>
    public const float PlayerSwoopSpeedMps = PlayerRunSpeedMps * 4f;

    /// <summary>
    /// [WIKI] Weapon delay floor. Combat is continuous and gated per weapon by
    /// <c>delay = baseSpeed x (1 - speedSkillMod/100)</c>, floored at one second; a pistol at +50 skill hits this floor
    /// and a rifle lands near three seconds. There is no discrete combat round.
    /// </summary>
    public const float MinAttackDelaySec = 1f;

    public const float MaxAttackDelaySec = 3f;

    // ── Missions (claude/design/SwgTatooine/design/missions.md § 2.2) ──────────────────────────────────────────────────
    //
    // Every number here is [EST] and tunable: missions.md lists the reasons, and none of them is a measurement.

    /// <summary>How many missions a character holds at once (Loïc, 2026-10-25). The wiki's SWG allowed two; this demo three.</summary>
    public const int MaxConcurrentMissions = 3;

    /// <summary>How many offers each mission terminal lists, always: one taken is replaced in the same tick (missions.md, R1 and R8).</summary>
    public const int MissionOffersPerTerminal = 20;

    /// <summary>
    /// How far an offer's anchor lies from its terminal, metres.
    /// </summary>
    /// <remarks>
    /// [CORE3] places a destroy mission 1-2 km from the PLAYER (<c>randomizeGenericDestroyMission</c>: base 1 000, random 1 000, both difficulty terms zero
    /// in the shipped configuration), twenty tries, outside every city. Here the origin is the terminal, which stands inside a city of up to 456 m radius, so
    /// the near bound is the city's edge rather than a kilometre.
    /// </remarks>
    public const float MissionMinDistanceM = 500f;

    public const float MissionMaxDistanceM = 2000f;

    /// <summary>How far round its anchor a mission's lair is drawn, metres: "about there" (missions.md, R2).</summary>
    public const float MissionScatterM = 150f;

    /// <summary>Credits per difficulty level, plus as many again per kilometre: 150-300 at difficulty 1 (missions.md § 2.2).</summary>
    public const int MissionRewardBase = 100;

    public const int MissionRewardPerKm = 100;

    /// <summary>How long an untaken offer stays listed, seconds, before it is replaced: the shortest, and the spread above it.</summary>
    public const float MissionOfferLifeMinSeconds = 300f;

    public const float MissionOfferLifeSpanSeconds = 600f;

    /// <summary>How long a mission's lair stands before it moves somewhere else round its anchor, seconds (missions.md, R6).</summary>
    public const float MissionLairLifeSeconds = 1200f;

    /// <summary>
    /// How close to a mission terminal's centre a body must stand to take one of its missions, metres. The client's own gate is the tighter one.
    /// </summary>
    public const float MissionTerminalReachM = 6f;

    /// <summary>
    /// The credits a mission pays: <see cref="MissionRewardBase"/> plus <see cref="MissionRewardPerKm"/> per kilometre, per difficulty level, to the nearest 5.
    /// </summary>
    public static int MissionReward(int difficulty, float distanceM)
        => (int)(MathF.Round(difficulty * (MissionRewardBase + (MissionRewardPerKm * distanceM / 1000f)) / 5f) * 5f);

    /// <summary>Which of the eight directions a point lies in from another: 0 north (+Z), then clockwise — 2 east (+X), 4 south, 6 west.</summary>
    public static byte OctantOf(float dx, float dz)
    {
        var bearing = MathF.Atan2(dx, dz);   // 0 north, +π/2 east
        var eighths = (int)MathF.Round(bearing / (MathF.PI / 4f));
        return (byte)(((eighths % 8) + 8) % 8);
    }

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
        //                  name                       x        z     radius  lairs/km²  the lairs it spawns
        new SpawnRegionDef("Northern Dune Sea",     -1200f,   5600f,  2600f,   18f,   [CreatureTemplates.Frog, CreatureTemplates.Snake, CreatureTemplates.Wasp, CreatureTemplates.SandNinja]),
        new SpawnRegionDef("Western Dune Sea",      -6200f,   4200f,  2100f,   15f,   [CreatureTemplates.SandNinja, CreatureTemplates.Viper]),
        new SpawnRegionDef("Jundland Wastes",       -3600f,  -1400f,  2400f,   22f,   [CreatureTemplates.Spider, CreatureTemplates.Viper, CreatureTemplates.Rat, CreatureTemplates.Ninja]),
        new SpawnRegionDef("Xelric Draw",            5400f,  -1500f,  1900f,   20f,   [CreatureTemplates.Rat, CreatureTemplates.Wasp, CreatureTemplates.Soldier]),
        new SpawnRegionDef("Eastern Wastes",         6100f,   3000f,  2200f,   14f,   [CreatureTemplates.Soldier, CreatureTemplates.Spider]),
        new SpawnRegionDef("Southern Wastes",         800f,  -6800f,  2300f,   17f,   [CreatureTemplates.Rat, CreatureTemplates.Snake, CreatureTemplates.SandNinja]),
        new SpawnRegionDef("Mos Espa Approach",     -3400f,    600f,  1500f,   21f,   [CreatureTemplates.Ninja, CreatureTemplates.Frog]),
        new SpawnRegionDef("Arid Flats",             2200f,    400f,  2000f,   13f,   [CreatureTemplates.Frog, CreatureTemplates.Wasp, CreatureTemplates.Soldier]),
    ];

    /// <summary>
    /// The share of a region's lairs that are camps of foes rather than beasts' lairs: half (Loïc, 2026-10-24 — drawn uniformly over a region's kinds, beasts
    /// outnumbering camps two or three to one in every region, nine lairs in ten were beasts'). Every region names at least one camp.
    /// </summary>
    public const float CampShare = 0.5f;

    /// <summary>
    /// The lairs a point of interest spawns: humanoid camps, because a landmark is where somebody has settled — Core3 puts the Tuskens at their fort for the
    /// same reason. Drawn from per lair, as a region's are.
    /// </summary>
    public static readonly int[] PoiLairTemplates = [CreatureTemplates.Ninja, CreatureTemplates.SandNinja, CreatureTemplates.Soldier];

    /// <summary>
    /// The level the scripted crowd counts as (content-levels.md § 5): its 1 400 hp is CL 10 health and its 95 damage per randomised delay about CL 10
    /// on-band DPS. It fights only lairs and creatures at or below it — the city fringes — and the B4 level factor reads it for a scripted body.
    /// </summary>
    public const int CrowdLevel = 10;
}

/// <summary>
/// The kinds of lair the world spawns, and what lives in each: six beasts from the creature pack and three humanoid camps dressed in the character pack's
/// enemy outfits. Every statistic is [EST] — placeholders until the mission gameplay is designed (Loïc, 2026-10-24) — and the names are generic fauna, the
/// IP stance <c>design/gameplay.md</c> § 7 sets for anything a player reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which asset a template wears is the server's, and it is published rather than mirrored</b> (<see cref="Bodies"/>, <see cref="LairAsset"/>; the client
/// reads them from <c>/typhon/demo.json</c>). It is the same arrangement the structures have: a client holding its own copy of "a ninja hideout is drawn in
/// these outfits" would eventually disagree with the world about what it is looking at.
/// </para>
/// <para>
/// <b>A template is a lair kind, not a species.</b> A mission title will read "destroy a ninja hideout" or "destroy a lair of rats", so the unit the world
/// counts is the lair, and the creatures are what it contains.
/// </para>
/// </remarks>
public static class CreatureTemplates
{
    public const int Rat = 0;
    public const int Frog = 1;
    public const int Snake = 2;
    public const int Viper = 3;
    public const int Spider = 4;
    public const int Wasp = 5;
    public const int Ninja = 6;
    public const int SandNinja = 7;
    public const int Soldier = 8;

    /// <summary>A camp of blue soldiers: a mission's kind only, which no wild site draws (missions.md). It was the destroy-mission pool's defenders.</summary>
    public const int BlueSoldier = 9;

    public const int Count = 10;

    /// <summary>Creatures a lair of each template keeps alive. [EST], shaped by how SWG lairs felt: 4-6 typical, more for a camp.</summary>
    public static readonly int[] SpawnLimit = [6, 5, 4, 4, 4, 6, 5, 5, 6, 3];

    /// <summary>Hit points. [EST].</summary>
    public static readonly int[] Health = [180, 120, 220, 300, 520, 160, 640, 700, 760, 420];

    /// <summary>Whether a template is a camp of foes — people — rather than a beasts' lair.</summary>
    public static bool IsCamp(int template) => template is Ninja or SandNinja or Soldier or BlueSoldier;

    /// <summary>
    /// A lair's hit points: four of its own occupants', and never under 1 500 — harder to destroy than any foe it holds (Loïc, 2026-10-24), and only once
    /// they are all dead (<c>Lair.Guards</c>).
    /// </summary>
    public static int LairHealth(int template) => System.Math.Max(1500, 4 * Health[template]);

    /// <summary>
    /// A lair kind for a site: a camp with <see cref="TatooineData.CampShare"/>'s odds when the site names one, a beasts' lair otherwise, uniform within each.
    /// </summary>
    public static int PickLair(int[] kinds, ref Rng rng)
    {
        var camps = 0;
        foreach (var k in kinds)
        {
            camps += IsCamp(k) ? 1 : 0;
        }

        var beasts = kinds.Length - camps;
        var wantCamp = camps > 0 && (beasts == 0 || rng.NextFloat() < TatooineData.CampShare);
        var pick = rng.NextInt(0, wantCamp ? camps : beasts);
        foreach (var k in kinds)
        {
            if (IsCamp(k) == wantCamp && pick-- == 0)
            {
                return k;
            }
        }

        return kinds[0];
    }

    /// <summary>Damage per attack. [EST].</summary>
    public static readonly int[] Damage = [12, 8, 16, 24, 34, 14, 38, 40, 32, 26];

    /// <summary>[EST] Credits a kill drops, roughly a quarter of the creature's hit points — big game pays more.</summary>
    /// <remarks>
    /// The value is arbitrary and says so; what is not arbitrary is that a kill writes it into <see cref="Inventory"/> on the tick the creature dies, which
    /// is the first steady-state <c>Versioned</c> write this world has ever made (gap G3).
    /// </remarks>
    public static readonly int[] LootCredits = [45, 30, 55, 75, 130, 40, 160, 175, 190, 105];

    /// <summary>Movement speed in metres per second. [EST], below a player's 5 m/s so a player can always disengage.</summary>
    public static readonly float[] SpeedMps = [3.4f, 2.6f, 2.0f, 2.4f, 3.0f, 4.0f, 4.2f, 4.2f, 3.8f, 3.6f];

    /// <summary>
    /// Whether the template attacks on sight. [EST]. The frog and the snake are passive, the viper is the snake's angry cousin, and every camp attacks. A
    /// passive creature never runs an aggro query, which is a real difference in cost.
    /// </summary>
    public static readonly bool[] Aggressive = [true, false, false, true, true, true, true, true, true, true];

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
    public static readonly float[] LairSpawnRadius = [25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f];

    /// <summary>
    /// The published bodies a template's creatures wear, one drawn per creature (<c>CreatureBrain.Skin</c> indexes it). A camp mixes its outfits; a beast is
    /// one model.
    /// </summary>
    public static readonly string[][] Bodies =
    [
        ["rat"],
        ["frog"],
        ["snake"],
        ["snake_angry"],
        ["spider"],
        ["wasp"],
        ["ninja_male", "ninja_female", "ninja_male_hair"],
        ["ninja_sand", "ninja_sand_female"],
        ["soldier_male", "soldier_female"],
        ["blue_soldier_male", "blue_soldier_female"],
    ];

    /// <summary>
    /// The prop at a lair's centre: the nest for a beast, a hideout for the ninjas and a camp for the soldiers. All three publish the same four
    /// <c>creature.N</c> spawn markers (ASSET_FORMAT § 5.4).
    /// </summary>
    public static readonly string[] LairAsset =
    [
        "creature_nest", "creature_nest", "creature_nest", "creature_nest", "creature_nest", "creature_nest",
        "ninja_hideout", "ninja_hideout", "soldier_camp", "soldier_camp",
    ];

    /// <summary>The template's name, for a census line and the client's label. Index order matches the tables above.</summary>
    public static string Name(int template) => template switch
    {
        Rat => "rats",
        Frog => "frogs",
        Snake => "snakes",
        Viper => "vipers",
        Spider => "spiders",
        Wasp => "wasps",
        Ninja => "ninja hideout",
        SandNinja => "sand-ninja hideout",
        Soldier => "soldier camp",
        BlueSoldier => "blue-soldier camp",
        _ => $"template {template}",
    };
}
