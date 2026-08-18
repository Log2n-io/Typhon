using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SpaceBattle;

/// <summary>
/// Every tunable in one flat object, so a scenario is a JSON file and an experiment is a command line.
/// </summary>
/// <remarks>
/// Flat and public-field on purpose: <see cref="ApplyOverride"/> reflects over the fields, so adding a knob here
/// automatically makes it settable as <c>--fieldName=value</c> and dumpable by <c>--print-config</c>. No registration
/// list to forget to update.
/// </remarks>
public sealed class Config
{
    // ─── World & spatial grid ────────────────────────────────────────────────────────────────────────────────────
    // ONE UNIT IS ONE METRE. Every distance below is metres, and the whole set is internally consistent at that
    // scale: a 10 m ship in a 100 km world is 1:10,000 of the world width. That ratio is the thing that matters —
    // it decides how many pixels an entity gets at a given zoom, and therefore what the LOD tiers have to do.

    /// <summary>World is square, [0..WorldSize] on both axes. 100 km.</summary>
    public float WorldSize = 100000f;

    /// <summary>THE knob. Cells are meant to be huge relative to a cluster's footprint — see the research docs.</summary>
    /// <remarks>2 km cells over a 100 km world = a 50x50 grid, Morton-padded to 64x64.</remarks>
    public float CellSize = 2000f;

    /// <summary>Fraction of CellSize an entity may stray past its cell boundary before migration is flagged.</summary>
    public float MigrationHysteresis = 0.05f;

    /// <summary>
    /// Fat-AABB margin on the spatial field, world units (metres).
    /// </summary>
    /// <remarks>
    /// This is a SCALE-SENSITIVE constant, not a free parameter: it must stay small relative to the entity it pads.
    /// At the old 66 u ship radius a margin of 1.0 was 1.5% of a ship; carrying that same 1.0 over to a 5 m ship
    /// would have made it 20%, quietly inflating every entity bound — and therefore every cluster AABB — by a fifth.
    /// </remarks>
    public float SpatialMargin = 0.5f;

    // ─── Population ──────────────────────────────────────────────────────────────────────────────────────────────
    public int Factions = 2;
    public int StationsPerFaction = 3;

    /// <summary>Station half-size in metres — a 300 m structure, 30x a ship. Big enough to stay a landmark.</summary>
    public float StationRadius = 150f;

    // ─── Station defence ─────────────────────────────────────────────────────────────────────────────────────────
    // Stations shoot back, because without it the degenerate strategy is to park on an enemy spawn and delete ships
    // as they appear — which ends a run without ever fighting for anything.
    //
    // EVERYTHING here is evaluated by linear scan over the six cached station positions, never through the spatial
    // index. Stations are the only thing in the simulation that never move and number in single digits, so a
    // six-element scan (~6 comparisons) replaces a query that would otherwise examine ~1000 entities. Routing
    // projectile-vs-station through ClusterSpatialQuery would have roughly DOUBLED the hot path for six entities.

    public bool StationsShoot = true;

    /// <summary>
    /// Station weapon range, metres. Must EXCEED <see cref="WeaponRange"/> or campers simply stand off and out-range
    /// it, and the whole feature does nothing.
    /// </summary>
    public float StationWeaponRange = 2000f;

    /// <summary>Damage per station round. 6 one-shots a fighter — a station is meant to be a place you do not loiter.</summary>
    public int StationDamage = 6;

    public int StationCooldownTicks = 8;

    /// <summary>Shield pool. Absorbs damage first and comes back; sized so a raid bounces and a siege does not.</summary>
    public int StationShieldMax = 6000;

    /// <summary>Shield restored per tick once the station has been calm for <see cref="StationRegenDelayTicks"/>.</summary>
    public int StationShieldRegen = 30;

    /// <summary>Ticks without being hit before the shield starts coming back.</summary>
    public int StationRegenDelayTicks = 120;

    /// <summary>Structural hit points behind the shield. Only depletes once the shield is gone.</summary>
    public int StationHpMax = 20000;

    /// <summary>
    /// Hit points rebuilt per tick while disabled. Deliberately slow — losing a station should hurt for a while.
    /// </summary>
    /// <remarks>
    /// A destroyed station is <b>disabled, not removed</b>. Two reasons: the simulation is meant to be endless, and
    /// permanent station loss on top of the existing runaway would terminate runs; and miners cache
    /// <c>HomeX/HomeY</c> at spawn, so deleting a station would leave every one of its miners flying home to a
    /// place that no longer exists.
    /// </remarks>
    public int StationHpRegen = 3;

    /// <summary>Radius within which a fighter will break off to defend its own station under attack.</summary>
    // Effectively global: larger than the world diagonal (100 000 x 100 000 => ~141 500), so a threatened station is
    // ALWAYS a candidate no matter where the defender is. It was 14 000 — 14 % of the map — which meant a faction could
    // watch a base of its own die on the far side while its fleet, out of range of the check, never even considered
    // going. A siege is the most important thing happening on the map; distance should decide who is nearest to answer
    // it, not whether anyone answers at all.
    public float StationDefendRadius = 200000f;

    /// <summary>How long a station counts as "under attack" after the last hit, for the purpose of pulling defenders.</summary>
    public int StationThreatTicks = 240;

    /// <summary>
    /// Radius searched around a threatened station to find the attacker the defenders should fly at.
    /// </summary>
    /// <remarks>
    /// Must exceed the longest ship weapon range (<see cref="DestroyerWeaponRange"/> is currently the largest), or a
    /// besieger can damage the station from beyond the radius that looks for it: the base would be flagged under
    /// attack with no attacker found, defenders would rally to an empty point and the siege would proceed unopposed.
    /// <see cref="Validate"/> enforces the inequality. Sized with margin above it so an attacker manoeuvring at the
    /// edge of its own range does not flicker in and out of the defenders' picture from tick to tick.
    /// </remarks>
    public float StationThreatScanRadius = 3000f;

    /// <summary>
    /// Radius of the ring a defender holds around its station when it was recalled but no attacker is visible.
    /// </summary>
    /// <remarks>
    /// Sits between <see cref="StationRadius"/> and <see cref="StationWeaponRange"/>: far enough out that the
    /// garrison is not stacked on the structure, close enough that it is inside the station's own covering fire and
    /// can reach anything that arrives. Set to 0 to disable the hold and let defenders park on the centre — the old
    /// behaviour, and the one that let a garrison drift off its own base.
    /// </remarks>
    public float StationGarrisonRadius = 900f;

    /// <summary>
    /// The longest journey, in ticks, a ship will undertake to answer a siege. Reach, not distance.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="StationDefendRadius"/> was reaching for and could not express. A radius is a distance;
    /// what decides whether a defender is useful is TIME, and time is distance over the hull's own speed — so the
    /// same 20 km is a 25-second trip for an interceptor and a 3-minute one for a destroyer. Measured against the
    /// global radius, the average defender was 43 km from its objective: a 6 450-tick journey against a 240-tick
    /// threat flag, which it never completed.
    /// <para>
    /// 1 800 ticks is 30 seconds, which at <see cref="ShipMaxSpeed"/> is about 13 km — close to the 14 km radius
    /// this mechanism originally used, but arrived at from reachability rather than picked, and now scaling
    /// correctly per hull. Raising it does not make defence better: past the point where a ship arrives to a fight
    /// that has already resolved, a longer leash only removes ships from the battle they were actually in.
    /// </para>
    /// </remarks>
    public float StationDefendMaxTravelTicks = 1800f;

    /// <summary>
    /// When true a station at zero hull is DESTROYED — the entity is despawned and never comes back. When false it
    /// is merely disabled and rebuilds once left alone for <see cref="StationRegenDelayTicks"/>.
    /// </summary>
    /// <remarks>
    /// Destruction makes the map a one-way ratchet: a faction that loses every station cannot spawn, cannot deliver
    /// ore, and is finished. That is the point — it gives the war a terminal state instead of an equilibrium. The
    /// disable-and-rebuild behaviour is kept behind this flag because it is the better setting for watching a long
    /// endless run, where a permanently eliminated faction would leave half the map empty for the rest of the
    /// session. Note the two are not merely cosmetic: with rebuild ON, parking a garrison on a wreck to suppress it
    /// is a real tactic; with destruction ON, there is nothing left to suppress.
    /// </remarks>
    public bool StationsDestructible = true;

    /// <summary>
    /// How the stations are arranged: <c>circle</c> spaces them around one ring with factions alternating;
    /// <c>lattice</c> interleaves them on a jittered grid; <c>edges</c> is the old opposing-columns layout.
    /// </summary>
    /// <remarks>
    /// <para><b>circle</b> is the default. It distributes stations over ANGLE, which is the one arrangement that
    /// cannot band: each station's two neighbours around the circumference are enemies, and the map interior is
    /// equidistant from every base rather than belonging to none of them.</para>
    /// <para><b>lattice</b> assigns factions in a checkerboard over a jittered grid. Correct in principle and it
    /// does produce several simultaneous fronts, but it cannot escape banding at small counts: six stations resolve
    /// to a 3x2 grid, and two rows are two lanes wherever you place them. Observed directly — two dense horizontal
    /// clouds with the middle half of the map dead, and the ore that spawned there mined by nobody.</para>
    /// <para><b>edges</b> is kept because it is the cleaner case for watching a battle LINE form and migrate, which
    /// is a different thing worth being able to see.</para>
    /// </remarks>
    public string StationLayout = "circle";

    /// <summary>Radius of the station ring as a fraction of WorldSize (<c>circle</c> layout only).</summary>
    /// <remarks>
    /// At 0.34 the ring sits comfortably inside the map with room for fights to spill outward, and leaves a 34 km
    /// interior that every base can reach — which is where the ore now goes.
    /// </remarks>
    public float StationRingRadiusPct = 0.34f;

    /// <summary>Per-station radial variation on the ring, as a fraction of the radius. Breaks the perfect circle.</summary>
    public float StationRingRadiusJitter = 0.08f;

    /// <summary>Random offset applied to each slot. For <c>lattice</c> a fraction of the slot spacing; for
    /// <c>circle</c> a fraction of the angular step. Keeps the layout from looking mechanical without letting
    /// stations collide or, on the ring, reorder.</summary>
    public float StationJitter = 0.22f;

    /// <summary>How far in from the left/right edge a faction's stations sit, as a fraction of WorldSize
    /// (<c>edges</c> layout only).</summary>
    public float StationEdgeInset = 0.06f;

    /// <summary>Top/bottom inset for the station column, as a fraction of WorldSize. Stations span the rest.</summary>
    public float StationVerticalInset = 0.10f;

    /// <summary>
    /// Extra inset from the world border for the <c>lattice</c> layout, as a fraction of WorldSize. Small on
    /// purpose — cell-centre placement already leaves half a step of margin, so an inset on top of it is charged
    /// twice and squeezes the whole arrangement toward the middle. At 0.13 a two-row lattice collapsed into the
    /// central 37 % of the map's height.
    /// </summary>
    public float StationLatticeInset = 0.04f;
    public int MaxShipsPerFaction = 20000;
    public int InitialShipsPerFaction = 6250;

    /// <summary>
    /// Initial ships are scattered this fraction of the world around their stations, so combat starts at once.
    /// </summary>
    /// <remarks>
    /// Raised for the 100 km world. Transit time is the hidden cost of a big map: at 800 m/s it takes ~55 s of
    /// simulated time — 3,300 ticks — just to reach the middle from a station, so a tight starting cluster means
    /// minutes of an empty screen before anything happens. Spreading the opening formation is the cheap fix; the
    /// alternatives are a faster ship or a smaller world, and both give up something real.
    /// </remarks>
    /// <remarks>
    /// <b>Cut from 0.5 once the stations were interleaved.</b> A wide opening scatter was needed when factions sat
    /// in opposing columns 88 km apart — without it the first two minutes were an empty screen while everyone
    /// commuted. On a lattice there are enemy stations everywhere, so nobody has far to go, and a 50 km scatter
    /// instead starts every ship already mixed with the enemy: the opening is an immediate mutual slaughter that
    /// the economy then has to rebuild from. Starting each fleet concentrated near its own bases is what lets the
    /// initial population mean something.
    /// </remarks>
    public float InitialSpread = 0.08f;

    /// <summary>Ticks between spawn pulses at a station.</summary>
    public int SpawnIntervalTicks = 6;

    /// <summary>
    /// Ships produced per pulse, per station. Scaled with the fleet: 3 stations x 1 ship / 6 ticks caps a faction's
    /// replacement rate at 30 ships/s, which cannot refill a 12 500 fleet against working guns however much ore is
    /// banked. Production throughput is a separate ceiling from production COST.
    /// </summary>
    public int SpawnBatch = 3;

    // ─── Ship ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Metres per second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised to 800 when the world went to 100 km, on the reasoning that crossing the map would otherwise take
    /// two minutes. Back to 667 now that the stations are interleaved, because nothing crosses the map any more:
    /// the interleaved lattice restored almost exactly the distances the 32 km world had.
    /// </para>
    /// <para>
    /// Station to nearest enemy station was 28.2 km then and is 29-31 km now; station to its contested ore field
    /// was 14.1 km and is ~15 km. At 667 m/s a miner reaches ore in 22.5 s against 21.1 s before. The map got three
    /// times wider and the journeys did not, so the original speed is right again — it is the layout that changed,
    /// not the scale.
    /// </para>
    /// <para>
    /// Costs ~17 % of the migration rate (fewer cell crossings per tick), which is one of the things this demo
    /// exists to stress. Use <c>--shipMaxSpeed=</c> to wind it back up when that is the point of the run.
    /// </para>
    /// </remarks>
    /// <para>
    /// Cut to 450 because engagements read better at that pace. Note it is no longer load-bearing for whether a
    /// duel can resolve — once projectiles fly at 3000 m/s the hit rate only moves 38 % to 41 % across this cut,
    /// where at 1500 m/s the same change was worth 9.7 % to 12.5 %. Speed is now a feel knob again.
    /// </para>
    public float ShipMaxSpeed = 450f;

    /// <summary>Matched to <see cref="ShipMaxSpeed"/> so time-to-top-speed stays ~0.6 s.</summary>
    public float ShipAccel = 750f;

    /// <summary>Ship radius in metres — a 10 m hull. 1:10,000 of the world width.</summary>
    public float ShipRadius = 5f;
    public int ShipHp = 20;
    public int ShipDamage = 2;

    /// <summary>
    /// Per-ship shield pool, drained before <see cref="ShipHp"/> and regenerating after a lull.
    /// </summary>
    /// <remarks>
    /// The regeneration rate is bounded from above by ONE attacker's damage output, not by taste: at damage 2, a
    /// 26-tick cooldown and a 38 % hit rate a single attacker deals ~1.75 damage/s, so regen much above that means
    /// a lone attacker can never finish a kill and skirmishes become endless rather than longer.
    /// </remarks>
    public int ShipShieldMax = 12;

    /// <summary>Ticks per point of shield regenerated. 30 = 2/s, comfortably under one attacker's ~1.75 dmg/s.</summary>
    public int ShipShieldRegenTicks = 30;

    /// <summary>Ticks per point of hull regenerated. Deliberately far slower than the shield — damage should stick.</summary>
    public int ShipHpRegenTicks = 300;

    /// <summary>Ticks without being hit before either pool starts recovering.</summary>
    public int ShipRegenDelayTicks = 180;

    /// <summary>Weapon range, metres. 80 hull-lengths; fits comfortably inside the tactical (LOD 0) view.</summary>
    public float WeaponRange = 800f;
    public int WeaponCooldownTicks = 26;

    /// <summary>
    /// Ticks a ship is held stationary after firing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A firing ship plants itself, which makes it a far easier target for whatever is shooting back. Without it,
    /// two fighters at 667 m/s simply cannot resolve an engagement: a projectile is aimed where the enemy WAS and
    /// arrives where the enemy is not.
    /// </para>
    /// <para>
    /// Must stay well below <see cref="WeaponCooldownTicks"/>, or a fighter with a target is rooted for its entire
    /// firing cycle and never moves at all. At 12 against a 26-tick cooldown a fighter is mobile a little over half
    /// the time.
    /// </para>
    /// <para>
    /// Note what this does and does not fix. It removes the target's motion during a shot's FLIGHT — but only for
    /// a target that happens to be reloading. It does nothing about the aim point being stale by up to
    /// <see cref="TargetReacquireTicks"/>, which is the larger of the two errors.
    /// </para>
    /// </remarks>
    public int FireRootTicks = 10;

    /// <summary>
    /// How often a ship re-runs its target-acquisition spatial query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is also the AIM STALENESS: a ship fires at the position recorded at its last acquisition, so at 40
    /// ticks and 667 m/s the aim point could be 445 m out of date against an 800 m weapon range and a 35 m hit
    /// radius. It was the single largest source of misses, and it is not a prediction problem — the shot was aimed
    /// at stale data, not at a badly-estimated future.
    /// </para>
    /// <para>
    /// The query was assumed to be too expensive to run often. Measured, the opposite: 40 to 8 ticks took the hit
    /// rate from 9.7 % to 18.3 % and the tick from 3.46 ms to 2.60 ms. Five times the acquisition queries cost
    /// LESS, because engagements that resolve stop accumulating ships that keep querying.
    /// </para>
    /// </remarks>
    public int TargetReacquireTicks = 8;

    /// <summary>Ticks a ship glows red after being hit.</summary>
    public int HitFlashTicks = 10;

    /// <summary>
    /// How long a fighter stays in "defend" mode after taking damage. While it lasts the fighter engages the
    /// nearest enemy of any type instead of pushing on toward enemy miners.
    /// </summary>
    public int ThreatMemoryTicks = 150;

    /// <summary>Radius of the target-acquisition query. Larger = more spatial work per acquisition.</summary>
    public float AcquireRadius = 1600f;

    /// <summary>Max hits examined per acquisition. Bounds query cost independently of local density.</summary>
    public int AcquireScanCap = 48;

    /// <summary>Ships steer toward the enemy centre of mass when they have no target, to keep the battle joined.</summary>
    public float WanderStrength = 0.25f;

    // ─── Stand-off ───────────────────────────────────────────────────────────────────────────────────────────────
    // Fighters hold their distance and circle rather than flying into their target. FIGHTERS ONLY: miners must
    // close to MineDockRange to work a rock, and a stand-off rule would fight the docking behaviour directly.

    /// <summary>
    /// Distance a fighter tries to hold from its target, metres. Must sit inside <see cref="WeaponRange"/> or
    /// ships would stand off beyond their own guns and fights would never resolve.
    /// </summary>
    public float StandoffRange = 600f;

    /// <summary>
    /// Width of the dead band around <see cref="StandoffRange"/>. Approach only beyond the outer edge, retreat only
    /// inside the inner one, orbit between.
    /// </summary>
    /// <remarks>
    /// A single threshold would chatter — the ship crosses it, reverses, crosses back. That exact failure has
    /// already appeared three times in this simulation (the escort orbit that never crossed the map, the miner
    /// parked at the edge of mine range, the fighter flipping rally targets), so the dead band is not optional.
    /// 200 m at 450 m/s is ~27 ticks wide, which is ample resolution.
    /// </remarks>
    public float StandoffBand = 200f;

    /// <summary>
    /// Tangential weight while approaching or retreating. Above zero the ship spirals in rather than charging
    /// straight down the radius, which is both what keeps it from overshooting and what makes the motion read as
    /// a dogfight rather than a collision course.
    /// </summary>
    public float OrbitStrength = 0.6f;

    /// <summary>
    /// Distance within which fighters push apart from each other, metres. 0 disables separation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed during the target-acquisition walk, which already visits every ship inside
    /// <see cref="AcquireRadius"/> — so the neighbour search costs nothing extra. A dedicated per-ship neighbour
    /// query would have added ~20 000 spatial queries per tick against the ~20 000 projectile hit tests that
    /// already dominate, roughly doubling the frame. Separation is only expensive if you pay for the search twice.
    /// </para>
    /// <para>
    /// It goes stale between re-acquisitions — 8 ticks, ~60 m at 450 m/s — which is irrelevant against a 250 m
    /// separation distance.
    /// </para>
    /// </remarks>
    public float SeparationRadius = 250f;

    /// <summary>
    /// Weight of the separation push relative to the unit steering vector.
    /// </summary>
    /// <remarks>
    /// The falloff is linear in distance (<c>1 - d/R</c>) and therefore bounded. An inverse-square law would be
    /// more physical and is the wrong choice here: it is unbounded as two ships approach, and with no collision to
    /// stop them a pair that happens to coincide would fling each other across the map.
    /// </remarks>
    public float SeparationStrength = 0.9f;

    // ─── Economy: miners and asteroids ───────────────────────────────────────────────────────────────────────────
    /// <summary>Fraction of spawned ships that are miners rather than fighters.</summary>
    public float MinerRatio = 0.35f;

    /// <summary>
    /// Hard ceiling on miners as a fraction of a faction's live fleet. Above it every spawn is a fighter.
    /// </summary>
    /// <remarks>
    /// <see cref="MinerRatio"/> cannot hold a population mix, because it governs the FLOW of new ships while the
    /// standing army is flow × lifetime — and miners live far longer (2x hull and shield, and <c>Damage = 0</c>, so they
    /// are never the ones trading fire). Measured before this cap: a 35 % spawn share settled at ~96 % of the fleet, and
    /// with the population at its cap the loop is self-reinforcing — nearly every death is a fighter, each is replaced by
    /// a miner a third of the time, so the fighter pool bleeds down and takes the kill rate with it.
    /// </remarks>
    public float MinerMaxShare = 0.40f;

    /// <summary>Seconds between score-trend samples. The arrow shows the change over one such window.</summary>
    public float ScoreTrendIntervalSeconds = 5f;

    // ─── Comeback (underdog catch-up) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a trailing faction gets economic and industrial help to climb back.</summary>
    public bool UnderdogEnabled = true;

    /// <summary>
    /// Score ratio against the leader below which help starts. At or above it a faction is on its own.
    /// </summary>
    /// <remarks>
    /// A threshold rather than a continuous curve, so an even match is completely unassisted and the mechanic cannot
    /// be accused of deciding a close game. It only engages once a side is measurably behind.
    /// </remarks>
    public float UnderdogThreshold = 0.85f;

    /// <summary>
    /// Extra income and production at total collapse (score 0), as a fraction. 1.0 = double, reached only in the limit.
    /// </summary>
    /// <remarks>
    /// Deliberately economic and industrial rather than combat. A damage or hull bonus would make the losing side's
    /// ships individually better than the winning side's, which inverts the thing the score is measuring and would
    /// undo the hull balance rather than compensate for position. Income and build rate let a beaten faction rebuild
    /// and re-contest, which is a comeback; stronger ships would just be a handicap race.
    /// </remarks>
    public float UnderdogMaxBonus = 1.0f;

    /// <summary>
    /// A faction's LAST surviving station can be disabled but never destroyed.
    /// </summary>
    /// <remarks>
    /// The one guard that stops a run becoming a formality. Production is per-station and miners can only bank ore at a
    /// live station, so at zero stations a faction has no income AND no output — it is mathematically dead and nothing
    /// can bring it back, however generous the catch-up. Measured across four seeds: every game was a blowout, the
    /// loser had always lost stations first, and on one seed a faction was finished by tick ~4 000 with 14 000 ticks
    /// left to play out. Station loss stays permanent everywhere else; this only refuses the final one.
    /// </remarks>
    public bool LastStationIndestructible = true;

    /// <summary>
    /// Whether the catch-up bonus also speeds a trailing faction's station repair.
    /// </summary>
    /// <remarks>
    /// The economic half of the bonus arrives too late to matter, because it multiplies an output the loser no longer
    /// has. Repair acts one step earlier — on whether the station survives at all — which is where the spiral actually
    /// starts.
    /// </remarks>
    public bool UnderdogStationRepair = true;

    /// <summary>
    /// Ceiling on the per-station output multiplier a trailing faction gets to offset a station deficit. 1 disables it.
    /// </summary>
    /// <remarks>
    /// Production is per-station, so a faction holding one base against three builds a third of the ships no matter how
    /// rich it is — and the score-based bonus, capped at double, only ever took that to two thirds. Measured: it never
    /// closed a single game. Scaling each surviving station's output by the deficit is what actually restores parity,
    /// because it compensates in the same currency the loss happened in. Capped so a faction reduced to its last base
    /// gets equality, not an advantage.
    /// </remarks>
    public float UnderdogStationDeficitCap = 3f;

    /// <summary>
    /// Score ratio below which a faction's ships cost no material. 0 disables it.
    /// </summary>
    /// <remarks>
    /// The last dependency to break. Every other part of the catch-up multiplies something a collapsed faction no
    /// longer has: production parity needs material, material needs live miners, and miners need fighters to survive
    /// long enough to bank a load. Measured — with production compensation alone, a faction reduced to 180 ships
    /// against 18 000 stayed there, because it could not pay for the ships its stations were now entitled to build.
    /// Free hulls cut the loop at the point it actually breaks; the faction still has to fly them, hold its base and
    /// re-take the map, which is a comeback rather than a gift.
    /// </remarks>
    public float UnderdogFreeShipsBelow = 0.25f;

    // ─── Destroyer (underdog capital ship) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Material a destroyer costs. Only a trailing faction may build one.</summary>
    /// <remarks>
    /// Expensive enough that it is a decision rather than a default, and pointedly NOT covered by
    /// <see cref="UnderdogFreeShipsBelow"/>: a collapsing faction gets free fighters to stay alive, but a capital ship
    /// has to be earned by mining. That keeps it a comeback tool rather than a consolation prize, and it means the
    /// underdog must protect its miners long enough to afford one.
    /// </remarks>
    public int DestroyerCost = 500;

    /// <summary>Destroyer hull. Twenty light fighters' worth, so a fleet has to commit to killing one.</summary>
    public int DestroyerHp = 400;

    /// <summary>Destroyer shield.</summary>
    public int DestroyerShield = 240;

    /// <summary>Destroyer damage per shot.</summary>
    /// <remarks>
    /// Cut from 40 to trim the hull's combat value by a quarter. Combat value goes as effective-HP x DPS, so taking
    /// the whole 25 % out of damage and none out of the hull is the reading that leaves the design intact: the
    /// destroyer was specified as slow, very resistant and deadly, and shaving the tank instead would have made it a
    /// worse version of a heavy rather than a gentler capital ship.
    /// </remarks>
    public int DestroyerDamage = 30;

    /// <summary>
    /// Destroyer top speed — deliberately crawling.
    /// </summary>
    /// <remarks>
    /// Slow enough that it cannot take the war to the enemy's bases in any reasonable time, which is the whole point:
    /// it defends the ground its owner still holds and shreds anything that comes to it. A fast one would simply be a
    /// win condition handed to the losing side.
    /// </remarks>
    public float DestroyerMaxSpeed = 110f;

    /// <summary>Destroyer weapon reach. Outranges every other hull, so it opens fire first.</summary>
    public float DestroyerWeaponRange = 1600f;

    /// <summary>Whether a trailing faction may build destroyers at all.</summary>
    public bool DestroyersEnabled = true;

    /// <summary>
    /// Material a station must hold to spawn one ship. Spawning is gated on mining.
    /// </summary>
    /// <remarks>
    /// Lowered when the fleet was scaled 5x, along with <see cref="CargoMax"/>, <see cref="MineRate"/> and
    /// <see cref="AsteroidCapacity"/>. Raising the ship cap alone did not raise the fleet: the steady state is set
    /// by deaths against production, not by the cap. At 5x ships the loss rate was ~17 ships/s while ore funded
    /// 1.9/s, so the population simply decayed back to where the economy could hold it. The ore SUPPLY has to scale
    /// with the fleet or the cap is decoration.
    /// </remarks>
    /// <summary>Material a MINER costs. Unchanged at 60 — miners are the economy, not the war.</summary>
    public int MinerCost = 60;

    /// <summary>Material a light fighter costs.</summary>
    public int LightCost = 120;

    /// <summary>Material a heavy costs. Lumpy on purpose: one bad trade is worth three light fighters.</summary>
    public int HeavyCost = 400;

    /// <summary>Material an interceptor costs.</summary>
    public int FastCost = 80;

    // ─── Hull mix (fractions of NON-miner spawns; light takes the remainder) ────────────────────────────────────────

    /// <summary>Share of non-miner spawns that are heavies.</summary>
    public float HeavyShare = 0.15f;

    /// <summary>Share of non-miner spawns that are interceptors.</summary>
    public float FastShare = 0.25f;

    // ─── Interceptor ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Interceptor top speed. Nearly twice a light fighter's, which is its entire reason to exist.</summary>
    public float FastMaxSpeed = 800f;

    /// <summary>Interceptor hull.</summary>
    public int FastHp = 10;

    /// <summary>Interceptor shield. Together with <see cref="FastHp"/> that is 20 effective HP — under two thirds of a light fighter's.</summary>
    public int FastShield = 10;

    /// <summary>
    /// How far an interceptor will notice a contested pickup, against <see cref="PickupAttractRadius"/> for everyone else.
    /// </summary>
    /// <remarks>
    /// This is what "priority to take the powerup" means mechanically. The engagement rule is already parameter-free —
    /// a ship shoots whichever is nearer, the pickup or an enemy — so the only lever that makes a hull a specialist
    /// racer is how early it hears about the objective. At 60 km an interceptor commits from most of the map while a
    /// light fighter three times closer is still unaware.
    /// </remarks>
    public float FastPickupAttractRadius = 60000f;

    /// <summary>Weapon range for heavies, metres. Everyone else uses <see cref="WeaponRange"/>.</summary>
    public float HeavyWeaponRange = 1000f;
    public int StartingMaterial = 4000;

    /// <summary>
    /// Below this many miners, a faction gets free ones. Without a floor the simulation is not actually endless:
    /// lose your last miner and you earn no material, so you build no ships, so you never recover — a run reliably
    /// ended 135 v 2 with the loser at zero miners. This is the only rule in the sim that exists to keep it running
    /// rather than to model anything.
    /// </summary>
    public int MinerFloor = 8;

    /// <summary>
    /// Optional rubber-band: also floor a faction's miners at this fraction of the LEADING faction's.
    /// <b>Off by default — a decisive winner is allowed.</b>
    /// </summary>
    /// <remarks>
    /// Fighters hunt miners by design, so the economy runaway is intentional and strong: more fighters kill more
    /// enemy miners, which funds more fighters. Measured runs settle around 250 v 15 and stay there. Set this to
    /// ~0.4 if you want a sustained two-sided battle instead; <see cref="MinerFloor"/> alone does not achieve it,
    /// because the loser's miners are farmed as fast as they are replaced (identical 250 v ~11 at absolute floors
    /// of 4, 12, 25 and 40).
    /// </remarks>
    public float MinerFloorRatio = 0f;

    /// <summary>Ticks between free-miner top-ups for a faction below <see cref="MinerFloor"/>.</summary>
    public int MinerFloorIntervalTicks = 240;

    /// <summary>
    /// Target number of live asteroids.
    /// </summary>
    /// <remarks>
    /// This is the single most influential number in the whole simulation, and it is not obvious why. Miners go to
    /// ore; fighters hunt miners. So <b>wherever the ore is, is where the war is</b> — the asteroid layout decides
    /// the shape of the conflict far more than the station layout does. Three asteroids in a ring at the map centre
    /// produced exactly one battle, in the middle, no matter where the stations sat.
    /// </remarks>
    public int AsteroidCount = 8;

    /// <summary>
    /// Where asteroids are placed: <c>scatter</c> uniformly inside a disc, <c>contested</c> between opposing
    /// stations, <c>ring</c> on a circle around the map centre.
    /// </summary>
    /// <remarks>
    /// <para><b>scatter</b> is the default, and it is the only one of the three where a RESPAWN lands somewhere new.
    /// The other two draw from a fixed anchor list computed once at world build, so a depleted field reappeared a
    /// few hundred metres from where it died — the map's ore geography never changed for the life of a run, however
    /// long you watched it.</para>
    /// <para><b>contested</b> anchors each asteroid at the midpoint of a nearest enemy station pair, so every ore
    /// field is equidistant between two hostile bases. It buys guaranteed contest at the price of a static and
    /// fully predictable ore map. With stations on a ring the interior is already equidistant from every base, so
    /// scattered ore is contested by geometry rather than by construction.</para>
    /// </remarks>
    public string AsteroidLayout = "scatter";

    /// <summary>
    /// Per-asteroid ore. Scaled with the fleet each time it grows — the steady state is deaths against production,
    /// so a bigger ship cap without a bigger ore supply just decays back to where the economy can hold it.
    /// </summary>
    public int AsteroidCapacity = 300000;
    /// <summary>Asteroids drift, slowly. Non-zero so their clusters still churn and occasionally migrate.</summary>
    public float AsteroidSpeed = 30f;
    /// <summary>Ticks between respawn attempts once below AsteroidCount. Deliberately slow: material is scarce.</summary>
    public int AsteroidRespawnTicks = 200;
    /// <summary>
    /// Drawn radius of a FULL asteroid, world units. The rendered size is <c>AsteroidRadius × (Capacity/MaxCapacity)</c>,
    /// i.e. normalised to each asteroid's own starting capacity — so raising <see cref="AsteroidCapacity"/> makes an
    /// asteroid last longer without making it draw any bigger, and the square still shrinks as it is mined out.
    /// </summary>
    /// <remarks>
    /// 800 m across — deliberately 80x a ship. Asteroids are LANDMARKS: they must still be visible one LOD tier
    /// after ships have collapsed into the density field, or a zoomed-out view has nothing to navigate by.
    /// </remarks>
    public float AsteroidRadius = 400f;

    /// <summary>
    /// Distance from the map centre at which asteroids sit, as a fraction of WorldSize. With
    /// With <see cref="AsteroidLayout"/> = <c>ring</c> this is the radius of the ring; with <c>scatter</c> it is
    /// the radius of the disc they are scattered inside. Ignored by <c>contested</c>, which derives its positions
    /// from the stations — except as the fallback ring when there are more asteroids than enemy station pairs.
    /// </summary>
    /// <remarks>
    /// Widened from 0.10 when <c>scatter</c> became the default. At 0.10 the "scatter" disc was a 10 km circle on a
    /// 100 km map: every asteroid inside it, which is not a scatter at all but the single central ore field this
    /// layout is supposed to avoid — and with ore in one place there is one war, wherever the stations are. At 0.40
    /// the disc spans the station ring (0.34) and a little beyond, so ore falls inside, on and outside the ring.
    /// </remarks>
    public float AsteroidFieldRadiusPct = 0.40f;

    /// <summary>
    /// Anchored layouts (<c>contested</c>, <c>ring</c>) place asteroids on a fixed set of points, so a respawn
    /// returns to the VACANT anchor rather than appearing at random. That keeps each ore field a stable place worth
    /// contesting instead of a lottery that relocates the war every few minutes.
    /// </summary>
    public float AsteroidAnchorJitter = 0.10f;

    /// <summary>Material mined per tick while in range.</summary>
    /// <remarks>
    /// Integer, and consumed as <c>(int)(MineRate * multiplier)</c>, so the rate quantises: 6 -> 7 is +16.7 %, and
    /// 7.2 would truncate straight back to 7. Buying the last 3 % would mean carrying a fractional remainder per
    /// miner, which is real per-entity state for a difference no one can see against the trip time that dominates a
    /// mining cycle.
    /// </remarks>
    public int MineRate = 7;

    /// <summary>
    /// Distance at which a miner can extract ore. Must be close to <see cref="MineDockRange"/>.
    /// </summary>
    /// <remarks>
    /// The subtle failure is not "too far to reach" but "far enough that arriving is unnecessary": at 1 000 m a
    /// miner began extracting the moment it entered range, filled a 750-unit boosted hold in ~42 ticks, and turned
    /// for home at ~780 m — having covered only 220 m of the approach. It never touched the rock, and looked from
    /// outside like mining at a distance. Extraction range has to be comparable to the docking distance, or the
    /// last stretch of the approach is simply optional.
    /// </remarks>
    public float MineRange = 520f;

    /// <summary>
    /// Distance at which a miner stops closing and parks on the rock. Must be well inside <see cref="MineRange"/>.
    /// </summary>
    /// <remarks>
    /// Without a separate docking distance, a miner parks the moment it enters mining range — at the very edge —
    /// and the asteroid's 30 m/s drift then carries the rock back out of range, so the miner thrusts, re-enters,
    /// parks, and drifts out again. Measured, it pinned them at 970 m of a 1000 m range: 1 992 miners "seeking",
    /// 4 mining and ZERO ever filling a hold. Separating "close enough to extract" from "close enough to stop"
    /// gives the approach the hysteresis it needs, and puts miners visibly ON the asteroid.
    /// </remarks>
    public float MineDockRange = 430f;

    /// <summary>
    /// Distance from a station's centre at which a laden miner unloads. Scaled off <see cref="StationRadius"/>, not
    /// off any mining constant — this is station geometry, and nothing about an asteroid should move it.
    /// </summary>
    /// <remarks>
    /// The drop-off was originally <c>MineRange * 2</c>, which is 1 040 m against a 150 m station: miners jettisoned
    /// cargo nearly seven station-radii out, in open space, and the delivery read as a stream of ships turning
    /// around for no visible reason. The magnitude was the symptom; the real fault was deriving a STATION threshold
    /// from a constant that describes how close you must get to an ASTEROID, so tuning one silently moved the other.
    /// <para>
    /// 200 m puts the miner on the hull — the station is drawn at 150 m half-size, so this is just past the flat of
    /// the box and inside its diagonal corner. Safe against tunnelling by a wide margin: a miner covers 7.5 m per
    /// tick at <see cref="ShipMaxSpeed"/> (11.25 boosted), so the band is 18-27 ticks deep. Delivery is also a
    /// one-shot event rather than a sustained dwell, so it cannot oscillate the way the asteroid approach did and
    /// needs no <see cref="MineDockRange"/>-style hysteresis partner.
    /// </para>
    /// </remarks>
    public float StationDockRange = 200f;

    /// <summary>
    /// Ore a miner carries per trip. The real throughput knob at this scale: a 100 km map makes the round trip
    /// long, so delivery is limited by TRIPS rather than by how much ore exists or how fast it is extracted.
    /// </summary>
    public int CargoMax = 200;
    /// <summary>Radius within which a miner looks for an asteroid. Must reach across most of the map or miners idle.</summary>
    public float OreSearchRadius = 40000f;
    /// <summary>Fighters with no enemy in sight rally to friendly miners inside this radius instead of the map centre.</summary>
    public float EscortRadius = 12000f;

    // ─── Super-power pickups ─────────────────────────────────────────────────────────────────────────────────────
    public bool PickupsEnabled = true;

    /// <summary>
    /// Mean ticks between pickup spawns.
    /// </summary>
    /// <remarks>
    /// <para><b>The formula.</b> With one alive at a time, the fraction of the match during which SOME faction has
    /// an effect is roughly <c>duration / interval</c> plus however long the contest itself takes. Pick the uptime
    /// you want, then set the interval.</para>
    /// <para>2000 ticks = ~33 s mean. Note the ceiling: the timer only fires when nothing is alive and is not
    /// pushed forward while a contest runs, so once the interval drops below the time a race takes to resolve, the
    /// CONTEST duration becomes the real cadence and shortening this further does nothing.</para>
    /// <para><see cref="MaxPickupsAlive"/> stays 1: two concurrent objectives split the contest, and a single
    /// contested point is the entire source of the engage-or-race decision.</para>
    /// </remarks>
    public int PickupSpawnIntervalTicks = 2000;

    /// <summary>
    /// Hits one faction must land on a pickup to win it. Each faction has its own tally; first to this number takes
    /// the effect and the pickup is destroyed.
    /// </summary>
    public int PickupHitsToWin = 200;

    /// <summary>
    /// Ticks between one point of progress decaying off every faction's tally. 0 disables decay.
    /// </summary>
    /// <remarks>
    /// Without decay a side that reaches 190 and is then wiped out keeps that 190 banked for the pickup's whole
    /// life, so the next few hits decide it on history rather than on the fight in front of you. Decay makes the
    /// tally a measure of *sustained* pressure — you have to hold the ground, not have held it once.
    /// </remarks>
    public int PickupProgressDecayTicks = 30;

    /// <summary>
    /// How far a pickup pulls fighters, in metres. About HALF a lattice spacing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT global. A pickup every fighter on the map converges on would empty every other front for
    /// the duration of the contest — undoing the interleaved station layout, whose whole purpose is several
    /// simultaneous local wars.
    /// </para>
    /// <para>
    /// <b>Size this against the POPULATED area, not the world.</b> This was first set to 30 km on the reasoning
    /// that a lattice spacing is ~30 km, and it pulled the whole fleet. The mistake is that ships occupy a band of
    /// roughly 72 x 46 km, not the full 100 x 100 km: a 30 km circle is 2 830 km² against a ~3 310 km² populated
    /// band, so "one lattice spacing" reached about 85 % of every ship on the map. At 15 km it covers ~21 % of the
    /// band and reaches the two bases flanking the ore anchor the pickup spawned on, which is what was intended.
    /// </para>
    /// </remarks>
    public float PickupAttractRadius = 15000f;

    /// <summary>Uniform jitter applied to the interval, as a fraction. Prevents metronomic spawns.</summary>
    public float PickupSpawnJitter = 0.4f;

    public int MaxPickupsAlive = 1;

    /// <summary>Ticks a pickup survives uncontested before despawning. Long enough for a 200-hit race to resolve.</summary>
    public int PickupLifeTicks = 5400;

    // ─── Effect durations, per type ──────────────────────────────────────────────────────────────────────────────
    // Separate knobs rather than one shared duration, because the four effects are not equally strong and the
    // faction that wins a 200-hit race is usually the one already ahead.

    /// <summary>Weapon power: every shot does <see cref="PowerDamageMultiplier"/>x damage. 1800 ticks = 30 s.</summary>
    public int PickupPowerDurationTicks = 1800;

    /// <summary>
    /// Shield: total immunity. HALF the others' duration on purpose — it is the strongest of the four, and handing
    /// the leading faction a long invulnerability window compounds a runaway that is already decisive.
    /// </summary>
    public int PickupShieldDurationTicks = 900;

    /// <summary>Speed: every ship moves faster, miners included. 1800 ticks = 30 s.</summary>
    public int PickupSpeedDurationTicks = 1800;

    /// <summary>
    /// Mining: ore per tick and cargo capacity both multiplied. The longest, because it is the only effect that
    /// pays off over time rather than instantly — and the only one worth more to the faction that is BEHIND, which
    /// makes it the one real comeback lever in the game.
    /// </summary>
    public int PickupMiningDurationTicks = 2700;

    /// <summary>
    /// Ticks a won PRODUCTION pickup doubles the winning faction's ship output for.
    /// </summary>
    /// <remarks>
    /// The only pickup that acts on the <i>rate</i> a faction converts material into ships rather than on the ships
    /// themselves. Production is hard-capped by station count and cooldown, so with material piling up unspent this is
    /// the one boost that can actually move the fleet size — which also makes it the most contested.
    /// </remarks>
    public int PickupProductionDurationTicks = 3600;

    /// <summary>Speed multiplier applied to every ship while the speed effect is active.</summary>
    /// <remarks>
    /// Bounded by the tunnelling constraint on <see cref="ShotSpeed"/>, not by taste: a boosted ship closing
    /// head-on with a projectile adds its own displacement to the projectile's, and the sum must stay under
    /// <c>2 x ShotHitRadius</c> or shots pass through. At 1.5x that is 25 m (shot) + 16.7 m (ship) = 42 m per tick,
    /// which is why <see cref="ShotHitRadius"/> went to 25 m — a 50 m hit diameter — alongside this.
    /// </remarks>
    public float SpeedBoostMultiplier = 1.5f;

    /// <summary>Ore-per-tick and cargo-capacity multiplier while the mining effect is active.</summary>
    public float MiningBoostMultiplier = 3f;

    /// <summary>Spawn area for pickups, as a fraction of WorldSize from the centre. Wider than the asteroid ring so
    /// they are not always on top of the mining fight, close enough that both factions can contest them.</summary>
    public float PickupSpawnRadiusPct = 0.30f;

    /// <summary>
    /// Probability, at total collapse, that a pickup spawns in the TRAILING faction's territory rather than at a
    /// neutral ore anchor. Scales from 0 at <see cref="UnderdogThreshold"/> to this value at score 0. Set 0 to disable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The powerup contest has the same runaway shape as everything else, but for a reason none of the other catch-up
    /// levers touch: pickups spawn at neutral ore anchors, and the faction holding more of the map simply has ships
    /// nearer to more of them. Winning wins powerups, powerups help you win. It is a COVERAGE advantage, so paying the
    /// loser more (shorter effects for the leader, bonus duration for the trailing side) treats the symptom — the
    /// leader still gets there first and still collects most of them.
    /// </para>
    /// <para>
    /// Moving where the objective appears cancels the advantage at its source. A pickup in the loser's own space is one
    /// its remaining ships are already close to, and one the leader has to cross contested ground to contest. It stays
    /// a fight — nothing is handed over, and the leader may well still take it — but the loser is no longer structurally
    /// out of position for every objective on the map.
    /// </para>
    /// </remarks>
    public float PickupUnderdogBiasMax = 0.75f;

    /// <summary>Radius around the trailing faction's station within which a biased pickup appears, as a fraction of the world.</summary>
    public float PickupUnderdogSpawnRadiusPct = 0.10f;

    public float PickupRadius = 250f;

    /// <summary>Damage multiplier while the weapon-power effect is active.</summary>
    public int PowerDamageMultiplier = 2;

    /// <summary>Radius of the shield ring drawn around a protected ship, as a multiple of ShipRadius.</summary>
    public float ShieldRingScale = 2.2f;

    // ─── Projectiles ─────────────────────────────────────────────────────────────────────────────────────────────
    public bool ProjectilesEnabled = true;

    /// <summary>
    /// Metres per second. Bounded from above by TUNNELLING, not by taste.
    /// </summary>
    /// <remarks>
    /// Hit detection is a discrete point-vs-radius test once per tick, so a projectile that advances further than
    /// <c>2 x ShotHitRadius</c> in one tick can step straight over a target and never register. At 60 Hz this
    /// travels 25 m/tick against a 40 m hit diameter — inside the limit with room to spare. Raising ShotSpeed or
    /// shrinking ShotHitRadius without re-checking that inequality silently drops hits, and the symptom (ships that
    /// occasionally refuse to die) looks nothing like its cause. It is a real hazard specifically BECAUSE of the
    /// rescale: at the old 66 u ship the margin was comfortable; at a 5 m ship it is not automatic.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>This is the gunnery knob, not ship speed.</b> Ships fire straight at the target's last known position
    /// with no lead, so the miss distance is <c>flightTime x targetSpeed</c> — and flight time is
    /// <c>range / ShotSpeed</c>. What decides whether an engagement can resolve is therefore the RATIO of ship
    /// speed to projectile speed, and raising the projectile is the half of that ratio which costs no pace.
    /// </para>
    /// <para>
    /// Measured at 8 000 ticks: 1500 m/s gave a 9.7 % hit rate, 3000 m/s gives 38 % with ships unchanged.
    /// </para>
    /// </remarks>
    public float ShotSpeed = 3000f;

    /// <summary>
    /// Ticks before a projectile expires. 26 ticks x 50 m = 1300 m, just past the LONGEST weapon range.
    /// </summary>
    /// <remarks>
    /// Was 20 (= 1000 m) when 800 m was the only range in the game. A heavy fires at 1000 m, so at 20 ticks its shots
    /// expired at the exact instant they arrived and it could never land a hit at its own maximum — the range increase
    /// would have been worth nothing, silently.
    /// </remarks>
    public int ShotLifeTicks = 26;

    /// <summary>
    /// Hit radius in metres. See the tunnelling note on <see cref="ShotSpeed"/> before lowering this — and note it
    /// must clear the SUM of the projectile's step and a speed-boosted ship's step, not the projectile's alone.
    /// </summary>
    /// <remarks>
    /// Raised with <see cref="ShotSpeed"/> to keep the tunnelling margin. Per tick the projectile advances 50 m and
    /// a speed-boosted ship closing head-on adds up to 17 m; 67 m against a 70 m hit diameter still clears.
    /// </remarks>
    public float ShotHitRadius = 35f;
    public int MaxShots = 40000;

    // ─── Simulation ──────────────────────────────────────────────────────────────────────────────────────────────
    public int TickRate = 60;
    public float StartSpeed = 1.0f;
    public bool StartPaused = false;

    /// <summary>
    /// Wall-clock allowance, in milliseconds, for advancing the simulation within one frame. The catch-up loop runs
    /// fixed <c>1/TickRate</c> steps until the backlog drains or this is spent, whichever comes first.
    /// </summary>
    /// <remarks>
    /// This replaced a tick COUNT cap of <c>ceil(speed * 2)</c>. A count is a prediction about tick cost baked in at
    /// startup: fine at 500 ships, wrong at 20 000. Measured at 45 ms/tick it still authorised two ticks per frame,
    /// producing a 90 ms frame that was 100 % simulation — and because the frame was already sim-bound, the second
    /// tick bought no extra ticks-per-second at all. It only doubled latency, and the overload hid as an invisible
    /// 0.37x world speed rather than as an honest frame rate.
    /// <para>
    /// The step size is NOT affected and must never be: every tick is exactly <c>1/TickRate</c> of simulated time.
    /// Only the NUMBER of steps per frame varies. Feeding the frame delta straight into the step would make shot
    /// travel per step scale with frame time — at 90 ms a 3 000 m/s round moves 270 m against a 35 m hit radius and
    /// passes through every ship on the map — and would untether every tick-denominated duration in this file.
    /// </para>
    /// </remarks>
    public float SimBudgetMs = 12f;

    /// <summary>
    /// Largest simulated-time debt the catch-up loop will carry, in seconds. Anything beyond it is discarded: the
    /// world runs slow rather than owing time it can never repay.
    /// </summary>
    /// <remarks>
    /// Scaled by the speed multiplier so fast-forward still drains as fast as <see cref="SimBudgetMs"/> allows, and
    /// matched to the 0.25 s clamp the main loop already applies to a single frame delta — so the ceiling is "one
    /// maximum frame's worth of debt". Note this cannot spiral the way a count cap could: per-frame work is bounded
    /// by wall clock now, so a large backlog can never translate into a catch-up burst.
    /// </remarks>
    public float MaxBacklogSeconds = 0.25f;

    /// <summary>Deterministic seed so a scenario replays identically. Override with <c>--seed=N</c>.</summary>
    public int Seed = 1234;

    // ─── WAL ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The demo deletes its database at boot, so it has no durability requirement at all — but the WAL is still the
    // narrowest pipe it runs through. Every ship writes Pos, Motion, Combat and Miner every tick; at the 25 000-ship
    // cap that is roughly 2 MB per tick, 100+ MB/s at 60 Hz, through a 2 MB commit buffer. When the writer cannot
    // keep up the tick fence blocks in TryClaim and eventually throws WalBackPressureTimeout.

    /// <summary>Force each WAL write durable on return. Off here: this database is deleted at boot.</summary>
    /// <remarks>
    /// The engine default is ON, which is right for a real database and wrong for a disposable one — it makes the
    /// writer's throughput a function of platter/flush latency rather than of bandwidth. Turn it back on with
    /// <c>--walUseFua=true</c> to measure the demo against realistic durability cost.
    /// </remarks>
    public bool WalUseFua = false;

    /// <summary>WAL segment size in MB (engine default 64). Bigger means rarer rollovers.</summary>
    /// <remarks>
    /// At ~100 MB/s a 64 MB segment rolls over about every 0.6 s, and each rollover has to find a pre-allocated
    /// file ready. Widening the segment and deepening the pre-allocation queue makes that far less frequent.
    /// </remarks>
    public int WalSegmentSizeMB = 256;

    /// <summary>Aligned staging buffer for WAL writes, KB (engine default 256). Must stay a multiple of 4.</summary>
    public int WalStagingBufferKB = 1024;

    /// <summary>Segments pre-allocated ahead of the write position (engine default 4).</summary>
    /// <summary>
    /// Checkpoint cadence in ms (engine default 30000). Exposed because the page cache can only reclaim a page once a
    /// checkpoint has written it, so this knob sets the ceiling on how much writeback debt the cache accumulates before
    /// anything drains it — and at this demo's write rate a 30-second interval accrues more than the cache holds.
    /// </summary>
    public int CheckpointIntervalMs = 30000;

    public int WalPreAllocateSegments = 8;

    /// <summary>
    /// Page cache size in MB (was hardcoded at 256). Exposed to make cache-fill pressure a controlled variable.
    /// </summary>
    /// <remarks>
    /// A long run's per-unit engine cost was measured to double as the cache went from a third full to completely full,
    /// while the sim's own workload FELL — but occupancy and run length advance together, so neither can be blamed from
    /// one run. Re-running the identical workload against a different cache size separates them: if the degradation is
    /// driven by cache fill, its onset moves with this number; if it tracks ticks elapsed regardless, it is something
    /// else and the cache is a bystander.
    /// </remarks>
    public int PageCacheMB = 256;

    /// <summary>
    /// Print per-segment allocated/free chunk counts every N ticks (0 = off).
    /// </summary>
    /// <remarks>
    /// Answers "does storage track live entities or cumulative spawns?" — which a file size cannot, because a large
    /// world and a leaking one look identical at one point in time. Separate from <see cref="CensusEveryTicks"/> and
    /// deliberately coarse: enumerating segments copies every page list.
    /// </remarks>
    public int SegmentDumpEveryTicks = 0;

    /// <summary>Auto mode only: select the station nearest the map centre and dump its info panel to the console.</summary>
    public bool AutoSelectStation = false;

    /// <summary>
    /// Force a checkpoint every N ticks (0 = off, use the engine's own 30 s timer). Diagnostic knob for #817.
    /// </summary>
    /// <remarks>
    /// The engine's idle interval is 30 s, so a two-minute run yields two cycles — far too few to tell a page
    /// pinned by a leaked writer (a streak that grows without bound) from a merely hot page (a streak that stays
    /// low because a retry pass eventually catches it quiet). Forcing the cadence buys dozens of cycles per run
    /// without writing tens of GB of WAL to get them.
    /// </remarks>
    public int ForceCheckpointEveryTicks = 0;

    /// <summary>Trace ACW increments/decrements for this memory page and report which call stacks don't balance
    /// (-1 = off). Diagnostic knob for #817; the leaked page indices are deterministic across runs.</summary>
    public int AcwTracePage = -1;

    /// <summary>Trace DirtyCounter mutations for this memory page and report the increments never released (-1 = off).</summary>
    public int DirtyTracePage = -1;

    /// <summary>
    /// Append a census line every N ticks to <see cref="CensusFile"/> (0 = off). Exists because the failure being
    /// studied (#824) kills the process: an end-of-run report tells you nothing when there is no end of run, and a
    /// single post-mortem exception is one bit of information for half an hour of machine time.
    /// </summary>
    public int CensusEveryTicks = 0;

    /// <summary>Where the census goes. CSV, appended, flushed per line so a hard crash keeps everything before it.</summary>
    public string CensusFile = "census.csv";

    /// <summary>Record the first DirtyCounter increment's call stack for every page, to group leaked pages by origin.</summary>
    public bool DirtyTraceAll = false;

    /// <summary>At end of run, force this many checkpoints back-to-back and report dirty pages after each.</summary>
    /// <remarks>
    /// Decides whether the dirty residue is purely the per-cycle K-1 imbalance of #824 — in which case repeated
    /// cycles drain it toward zero — or whether a second, frequency-independent component exists underneath, which
    /// would plateau above zero and mean #824's scope is wrong.
    /// </remarks>
    public int QuiesceCheckpoints = 0;

    // ─── Window / render ─────────────────────────────────────────────────────────────────────────────────────────
    public int WindowW = 1600;
    public int WindowH = 900;
    public bool VSync = true;

    /// <summary>Restore each window's position and size from the previous run, and save them on exit.</summary>
    public bool RememberWindowLayout = true;
    /// <summary>Open the database file-map window at startup. Off by default — press M to bring it up.</summary>
    public bool FileMapWindow = false;
    public int FileMapW = 620;
    public int FileMapH = 660;

    /// <summary>File-map refresh period, in simulation ticks. Higher = cheaper.</summary>
    public int FileMapEveryNTicks = 10;

    /// <summary>How fast a file-map cell's brightness decays per refresh, 0..1.</summary>
    public float FileMapDecay = 0.90f;

    // ─── Level of detail ─────────────────────────────────────────────────────────────────────────────────────────
    // The renderer has three tiers. Which one is active is decided from PIXELS PER ENTITY, never from camera
    // distance: distance knows nothing about the window size, so a distance threshold that reads well at 900 px is
    // wrong at 1400 px. Everything here is expressed in screen pixels for that reason.

    public bool LodEnabled = true;

    /// <summary>Force a tier: -1 auto, 0 detail, 1 point, 2 density. Debug/screenshot aid.</summary>
    public int ForceLod = -1;

    /// <summary>
    /// An entity spanning at least this many pixels gets its real sprite (LOD 0 — orientation, shields, hit flash).
    /// </summary>
    /// <remarks>Below ~3 px a rotated triangle is an indistinct blob, so drawing one costs vertices and buys nothing.</remarks>
    public float LodDetailPixels = 3f;

    /// <summary>
    /// Screen size, in pixels, that a point-tier entity is clamped to. Also the unit of the saturation estimate.
    /// </summary>
    public float LodPointPixels = 2f;

    /// <summary>
    /// Minimum on-screen size, in pixels, for LANDMARKS — stations and asteroids. They keep this size at every
    /// zoom, so they never disappear.
    /// </summary>
    /// <remarks>
    /// <para>The reasoning that collapses ships into a density field does not apply to these. There are six
    /// stations and eight asteroids: their count is fixed by the scenario, not by the population, so drawing them
    /// costs the same at 1,000 ships as at 100,000 and no argument from cost justifies dropping them.</para>
    /// <para>Nor does the honesty argument. A 2 px marker for a 10 m ship overstates it by 22x and there are
    /// hundreds of them, so the picture lies about density; a clamped marker for the one station in that region
    /// says "a station is here", which is true, and there is nothing for it to be confused with. Landmarks are what
    /// make a far view navigable rather than an abstract heat map — without them the aggregate has no anchors.</para>
    /// </remarks>
    public float LandmarkPixels = 12f;

    /// <summary>Landmark marker size on the minimap, in pixels. Smaller: the minimap is 260 px across.</summary>
    public float MinimapLandmarkPixels = 6f;

    /// <summary>
    /// Below this many pixels per entity, collapse to the density field regardless of how few entities there are.
    /// </summary>
    /// <remarks>
    /// <para>The second, independent reason to stop drawing entities — and the one that actually fires in this
    /// world. A marker clamped to 2 px while the entity is 0.09 px wide is not a small ship, it is a 22x
    /// exaggeration of one, and a fleet drawn that way reads as far denser and far larger than it is. Below roughly
    /// a third of a pixel the marker has stopped being a depiction of the entity and become a claim about it.</para>
    /// <para>Saturation (<see cref="LodSaturationFraction"/>) is about TOO MANY entities; this is about entities
    /// too SMALL. At 1,000 ships in a 100 km world the first never triggers — 1,000 two-pixel markers cover 0.3% of
    /// a 1600x900 viewport — so without this the density tier would be unreachable at any zoom. Both conditions are
    /// real, they fire in different regimes, and encoding only one leaves a hole.</para>
    /// <para>0.35 px puts the boundary near a 26 km view height at 900 px tall, with 10 m ships.</para>
    /// </remarks>
    public float LodDensityPixels = 0.35f;

    /// <summary>
    /// Switch from points (LOD 1) to the density field (LOD 2) once clamped sprites would cover this fraction of
    /// the viewport.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a coverage fraction rather than a zoom threshold.</b> Clamping a sprite to a minimum pixel size
    /// is what makes a far-away entity visible at all — and it is also exactly what turns a dense scene into a
    /// uniform smear, because the clamp stops the sprites shrinking while the gaps between them keep shrinking. The
    /// zoom level at which that happens depends entirely on how many entities are on screen, so a hardcoded zoom
    /// threshold is right for one population and wrong for every other.</para>
    /// <para>Estimated coverage is <c>visibleEntities x LodPointPixels² / viewportPixels</c>. At 1,000 ships and
    /// 2 px points that is 0.3% — nowhere near saturation, so the point tier holds all the way out. At 100,000 it
    /// crosses 15% and the density field takes over. The rule scales itself.</para>
    /// </remarks>
    public float LodSaturationFraction = 0.15f;

    /// <summary>
    /// Ratio by which a threshold must be exceeded before the tier changes back. Prevents the tier flickering when
    /// the zoom sits exactly on a boundary — one wheel notch of jitter would otherwise strobe the whole scene.
    /// </summary>
    public float LodHysteresis = 1.25f;

    /// <summary>Blend tiers across this many octaves of zoom instead of switching hard. 0 disables.</summary>
    public float LodCrossfadeOctaves = 0.5f;

    // ─── Density field (the LOD 2 aggregate, and the minimap's data) ─────────────────────────────────────────────

    /// <summary>
    /// Bins per axis for the aggregate. 128 over a 100 km world is one bin per 780 m — about 7 screen pixels with
    /// the whole world in view, which is the resolution at which "void vs something" reads cleanly.
    /// </summary>
    public int DensityResolution = 128;

    /// <summary>
    /// Where the density field gets its numbers: <c>entities</c> bins every entity (O(N), knows factions), or
    /// <c>cells</c> reads the engine's own per-cell occupancy (O(cells), no faction split).
    /// </summary>
    /// <remarks>
    /// Both are kept deliberately. <c>cells</c> is what the aggregate SHOULD ultimately be built from — it never
    /// touches entity data, so its cost is independent of population — while <c>entities</c> is ground truth. Run
    /// them against each other and any disagreement is a real bug in the engine's occupancy accounting, not a
    /// rendering artefact. Step 2 is where the default moves.
    /// </remarks>
    public string DensitySource = "entities";

    /// <summary>Exponent applied to normalised bin counts. Below 1 lifts sparse bins so a single hot knot cannot
    /// black out everything else — the same reason the cell heat overlay uses a square root.</summary>
    public float DensityGamma = 0.45f;

    /// <summary>Opacity ceiling of the world-space density overlay.</summary>
    public float DensityAlpha = 0.85f;

    /// <summary>
    /// Frames between density rebuilds, for every consumer — the LOD 2 overlay as well as the minimap.
    /// </summary>
    /// <remarks>
    /// The aggregate does not need to be rebuilt at frame rate, and rebuilding it there is what makes the
    /// <c>entities</c> source expensive. A bin is ~780 m across and a ship covers 800 m/s, so in four frames at
    /// 60 Hz an entity moves about a tenth of a bin: the field is visually identical and the cost drops fourfold.
    /// The crossfade still animates every frame — the blend weight is applied at draw time, not at build time — so
    /// the transition stays smooth regardless of this.
    /// </remarks>
    public int DensityRefreshFrames = 4;

    // ─── Minimap ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Always-on world overview. Justified by area: a 3 km tactical view over a 100 km world shows 0.0009% of the
    /// map, so without one you are permanently lost.
    /// </summary>
    public bool ShowMinimap = true;
    public int MinimapSize = 260;
    public int MinimapMargin = 14;

    // ─── Culling ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Draw only what the camera can see, resolved through the engine's own two-level spatial index rather than by
    /// enumerating every cluster and rejecting.
    /// </summary>
    /// <remarks>
    /// Off, the renderer walks every cluster of every archetype every frame — which at a 0.0009% view means walking
    /// a thousand ships to draw ten. On, it takes the camera rectangle to a cell range (level 1), reads each visible
    /// cell's cluster-AABB index (level 2), and opens only the clusters that survive. Keep the toggle: switching it
    /// off is the A/B that shows what the index is actually worth.
    /// </remarks>
    public bool CullingEnabled = true;

    /// <summary>Camera rect is grown by this fraction before culling, so entities do not pop at the screen edge.</summary>
    public float CullMargin = 0.08f;

    // ─── Debug overlays (all toggleable at runtime) ───────────────────────────────────────────────────────────────
    public bool ShowCells = true;
    public bool ShowCellHeat = true;
    public bool ShowClusterAabb = true;
    public bool ShowClusterLinks = false;
    public bool ShowShips = true;
    public bool ShowShots = true;
    public bool ShowAsteroids = true;

    /// <summary>
    /// Colour every entity — and its cluster's AABB — by the CLUSTER it belongs to instead of by faction.
    /// This is the mode that makes membership visible: if clusters were spatially coherent you would see solid
    /// blocks of one colour, and what you actually see is every colour interleaved everywhere.
    /// </summary>
    public bool ClusterColorMode = false;

    /// <summary>Alpha of the cluster-AABB outline, 0..1.</summary>
    public float ClusterBorderAlpha = 0.3f;

    /// <summary>
    /// Alpha of the cluster-AABB fill, 0..1. Much lower than the border on purpose: fills compound where boxes
    /// overlap, so a low value keeps a single box nearly invisible while a pile of them still glows — which is the
    /// signal worth reading.
    /// </summary>
    /// <remarks>
    /// Alpha is 8-bit, so the smallest representable non-zero value is 1/255 ≈ 0.0039. Anything below that would
    /// quantise to zero and silently disable the fill, so a non-zero setting is floored at 1 — asking for 0.001
    /// means "the faintest fill the hardware can draw", not "no fill". Use exactly 0 to turn it off.
    /// </remarks>
    public float ClusterFillAlpha = 0.001f;

    /// <summary>Border alpha for the SELECTED entity's cluster AABB — opaque, so it reads through everything else.</summary>
    public float SelectedBorderAlpha = 1.0f;

    /// <summary>Fill alpha for the selected entity's cluster AABB.</summary>
    public float SelectedFillAlpha = 0.2f;

    /// <summary>
    /// Minimum on-screen size, in pixels, at which a cluster AABB is drawn. A cluster holding one entity has a
    /// ZERO-area box — entity bounds are point-form, so the union over a single member is a point — and it would
    /// otherwise be drawn underneath the sprite and look missing. The box is inflated symmetrically for display
    /// only; the true bounds are what the HUD and the selectivity probe report. 0 disables.
    /// </summary>
    public float MinClusterBoxPixels = 14f;

    /// <summary>
    /// Only fill cluster AABBs smaller than this many cell-areas; larger ones stay outline-only.
    /// Without this a single degenerate cluster — and projectile clusters routinely span the whole world, because
    /// membership is allocation-ordered — paints one translucent quad over the entire view and hides everything.
    /// The count of such clusters is reported in the HUD, because it is a symptom worth watching, not noise.
    /// </summary>
    public float FillMaxCellArea = 2.0f;
    /// <summary>
    /// Draw each moving entity's velocity as a line: direction by its heading, length by its speed. Off by default
    /// — at several thousand ships it is a wall of lines, and it is a diagnostic rather than a view.
    /// </summary>
    public bool ShowMotionVectors = false;

    /// <summary>
    /// Seconds of travel a motion vector represents. The line is <c>velocity x this</c>, so its length is a
    /// SPEED in world units and can be compared directly against distances on screen — a vector reaching an
    /// asteroid means the entity arrives there in this many seconds.
    /// </summary>
    public float MotionVectorSeconds = 1.5f;

    public bool ShowStations = true;
    public bool ShowHud = true;
    public bool ShowSelectivity = true;
    public bool ShowTargetLines = false;
    public bool ShowQueryProbe = false;

    /// <summary>Radius of the interactive query probe (right-drag) — visualises what a real query touches.</summary>
    public float ProbeRadius = 2000f;

    // ─── Headless / self-verification ────────────────────────────────────────────────────────────────────────────
    /// <summary>Run N ticks, dump a screenshot, print a frame report, exit. 0 = interactive.</summary>
    public int AutoTicks = 0;
    public string AutoShot = "";

    /// <summary>
    /// Camera height, in world units, for the automated screenshot. 0 frames the whole world.
    /// </summary>
    /// <remarks>
    /// This exists so each LOD tier can be verified from the command line instead of by eye at an unrecorded zoom.
    /// A tier boundary is a claim about pixels, and a claim about pixels has to be checked at a stated zoom or the
    /// check means nothing.
    /// </remarks>
    public float AutoViewHeight = 0f;

    /// <summary>Camera centre for the automated screenshot. Negative means "the world centre".</summary>
    public float AutoCenterX = -1f;
    public float AutoCenterY = -1f;
    /// <summary>Print the frame-probe report for this rect (x,y,w,h in window pixels) then exit. Empty = whole frame.</summary>
    public string AutoRect = "";
    public bool PrintConfig = false;

    /// <summary>Verify the speed-key wiring without an OS event, then exit.</summary>
    public bool SelfTestKeys = false;

    /// <summary>
    /// Headless research mode: comma-separated cell sizes. Boots a fresh engine per size, runs
    /// <see cref="AutoTicks"/> ticks, and prints the selectivity sweep for each — i.e. answers "what cell size
    /// should we use?" with measurements instead of assumptions. Example: --cellSweep=500,1000,2000,4000
    /// </summary>
    public string CellSweep = "";

    /// <summary>Query radii used by the selectivity sweep, comma-separated world units.</summary>
    public string SweepRadii = "25,50,100,200,400,800,1600";

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static Config Load(string[] args)
    {
        var cfg = new Config();

        // A JSON file first, so CLI always wins over file.
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--scenario=", StringComparison.Ordinal))
            {
                var path = args[i]["--scenario=".Length..];
                cfg.ApplyJson(File.ReadAllText(path));
            }
        }

        foreach (var a in args)
        {
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.StartsWith("--scenario=", StringComparison.Ordinal))
            {
                continue;
            }
            var body = a[2..];
            var eq = body.IndexOf('=');
            if (eq < 0)
            {
                cfg.ApplyOverride(body, "true");
            }
            else
            {
                cfg.ApplyOverride(body[..eq], body[(eq + 1)..]);
            }
        }
        return cfg;
    }

    private void ApplyJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var raw = prop.Value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => prop.Value.GetString(),
                _ => prop.Value.GetRawText(),
            };
            ApplyOverride(prop.Name, raw);
        }
    }

    private void ApplyOverride(string name, string value)
    {
        var f = FindField(name);
        if (f == null)
        {
            Console.Error.WriteLine($"[config] unknown option '{name}' — ignored. Use --help to list options.");
            return;
        }
        try
        {
            object parsed =
                f.FieldType == typeof(float) ? float.Parse(value, CultureInfo.InvariantCulture)
                : f.FieldType == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture)
                : f.FieldType == typeof(bool) ? (value is "1" or "true" or "True" or "yes" or "on")
                : value;
            f.SetValue(this, parsed);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] bad value for '{name}': '{value}' ({ex.Message})");
        }
    }

    private static FieldInfo FindField(string name)
    {
        foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            sb.Append(f.Name).Append(" = ").Append(Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    public static string Help()
    {
        var sb = new StringBuilder();
        sb.Append("SpaceBattle — Typhon spatial-partitioning observatory\n\n");
        sb.Append("Usage: SpaceBattle [--scenario=file.json] [--option=value ...]\n\n");
        sb.Append("Options (default):\n");
        var d = new Config();
        foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            sb.Append("  --").Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(d), CultureInfo.InvariantCulture)).Append('\n');
        }
        sb.Append("\nSelf-verification:\n");
        sb.Append("  --autoTicks=600 --autoShot=out.png            run headless-ish, screenshot, report, exit\n");
        sb.Append("  --autoRect=x,y,w,h                            restrict the frame report to a rect\n");
        return sb.ToString();
    }

    /// <summary>Sanity-check the combination before the engine sees it; bad grids throw deep inside otherwise.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (WorldSize <= 0)
        {
            errors.Add("WorldSize must be > 0");
        }
        if (CellSize <= 0)
        {
            errors.Add("CellSize must be > 0");
        }
        if (CellSize > 0 && WorldSize > 0)
        {
            var dim = (int)MathF.Ceiling(WorldSize / CellSize);
            var pow2 = 1;
            while (pow2 < dim)
            {
                pow2 <<= 1;
            }
            if (pow2 > 32768)
            {
                errors.Add($"WorldSize/CellSize yields KeySpaceDim {pow2} > 32768 (32-bit Morton limit). Raise CellSize.");
            }
        }
        if (Factions is < 1 or > 4)
        {
            errors.Add("Factions must be 1..4");
        }
        if (StationsPerFaction is < 1 or > 8)
        {
            errors.Add("StationsPerFaction must be 1..8");
        }
        if (TickRate is < 1 or > 1000)
        {
            errors.Add("TickRate must be 1..1000");
        }
        // A zero or negative dock range is not a degraded economy, it is a silently dead one: miners fill up, fly
        // home, and orbit their own station forever without ever satisfying the unload test. Worth a hard error
        // because nothing on screen says "unreachable threshold" — it just looks like miners that stopped working.
        if (StationDockRange <= 0f)
        {
            errors.Add("StationDockRange must be > 0 (miners would never unload)");
        }
        // A scan radius under the longest weapon range means a station can be shot from a place the defence never
        // looks. The failure is silent and looks like passive AI: the base flags itself under attack, every defender
        // is recalled, and they all rally onto a point with nothing there.
        var longestWeapon = MathF.Max(MathF.Max(WeaponRange, HeavyWeaponRange), DestroyerWeaponRange);
        if (StationThreatScanRadius <= longestWeapon)
        {
            errors.Add($"StationThreatScanRadius ({StationThreatScanRadius}) must exceed the longest ship weapon range ({longestWeapon}) "
                + "or defenders cannot see what is shooting their station");
        }
        // A non-positive budget does not stop the simulation — the deadline is tested after the first tick, so one
        // step always runs — but it does silently disable catch-up entirely. Reject it rather than let it look like
        // a mysterious speed cap.
        if (SimBudgetMs <= 0f)
        {
            errors.Add("SimBudgetMs must be > 0");
        }
        if (MaxBacklogSeconds < 0f)
        {
            errors.Add("MaxBacklogSeconds must be >= 0");
        }
        // The engine requires the staging buffer to be a multiple of 4096 bytes; catch it here with a message that
        // names the knob rather than letting it surface from inside the WAL writer's constructor.
        if (WalStagingBufferKB <= 0 || WalStagingBufferKB % 4 != 0)
        {
            errors.Add("WalStagingBufferKB must be positive and a multiple of 4 (the WAL staging buffer must be 4096-byte aligned)");
        }
        if (WalSegmentSizeMB <= 0)
        {
            errors.Add("WalSegmentSizeMB must be > 0");
        }
        if (WalPreAllocateSegments <= 0)
        {
            errors.Add("WalPreAllocateSegments must be > 0");
        }
        if (PageCacheMB < 8)
        {
            errors.Add("PageCacheMB must be >= 8 (the engine's minimum cache size)");
        }
        return errors;
    }
}
