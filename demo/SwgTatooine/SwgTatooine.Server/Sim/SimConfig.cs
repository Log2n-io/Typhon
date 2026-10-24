using System;

namespace SwgTatooine.Server;

/// <summary>
/// Everything the simulation is parameterised by. Two axes are swept for the partitioning study — <see cref="WorldEdgeKm"/>
/// and <see cref="PopulationScale"/> — and everything else is either a fact about Star Wars Galaxies or a knob the study
/// tunes.
/// </summary>
/// <remarks>
/// <para><b>The two axes are deliberately independent.</b> Growing the world at a fixed population makes the grid sparser
/// without changing how much work the systems do; growing the population at a fixed world makes cells denser without
/// changing how many of them exist. A single "make it bigger" axis would move both at once and no measurement taken on it
/// could say which one the engine reacted to.</para>
/// </remarks>
public sealed class SimConfig
{
    // ── Axis 1: the world ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Edge of the square planet surface, in kilometres. <b>16.384 is the real Tatooine</b> — every SWG planet is
    /// 16 384 m square, built from one-metre tiles, with coordinates running -8192..+8192 on X and Z (Core3's
    /// <c>coordinateMin</c>/<c>coordinateMax</c>). 64 and 128 are inflations that keep the same content and spread it
    /// further apart, which is how the study reaches a sparsity SWG itself never had.
    /// </summary>
    /// <remarks>
    /// <b>Inflation is the direction this axis supports for STRUCTURES, and compressing it costs buildings.</b> A city's
    /// building count is a fact about the game while its radius is a coordinate, so <see cref="TatooineMap.Build"/>
    /// scales the radius and leaves the count alone — which is what keeps the agent population per cell comparable
    /// across the axis, and what makes the same city impossible to build once the disc shrinks. Anchorhead is already at
    /// 44.8 % of its ground at 16.384 km, and packing goes as 1/scale², so <em>any</em> value below the real planet size
    /// puts a site over the limit measured in <see cref="StructureFootprint.MaxPacking"/>: at 8 km all seven cities are
    /// over, at 2 km thirteen sites are and 786 structures cannot be planted. The counts are capped to what fits and the
    /// build says so once (<see cref="TatooineMap.ClampedSites"/>), so a compressed world is still a valid world — but
    /// its structure population is not comparable with the real planet's, and a measurement that spans the two is
    /// measuring the clamp as well as the engine.
    /// </remarks>
    public float WorldEdgeKm = TatooineData.PlanetEdgeM / 1000f;

    /// <summary>
    /// How the world inflation is applied to content. <c>true</c> scales every coordinate, so cities stay in the same
    /// relative places and simply get further apart; <c>false</c> keeps the original coordinates and leaves the extra
    /// area empty.
    /// </summary>
    /// <remarks>
    /// Scaling is the default because the alternative produces a world that is mostly void with all the content in one
    /// corner — which is a legitimate shape but tests the grid's sparsity handling rather than the cell layer, and the
    /// swarm scenario in <c>GameScenarios</c> already covers that.
    /// </remarks>
    public bool ScaleContentWithWorld = true;

    // ── Axis 2: the population ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Multiplier on every agent population — creatures, NPCs, players, mission camps. <c>1</c> is the faithful baseline
    /// drawn from what a live SWG galaxy actually held; higher values are volumetry, not fidelity, and the report says so.
    /// </summary>
    /// <remarks>
    /// Static content (cities, buildings, points of interest) is NOT scaled by this: those are the planet, and multiplying
    /// them would be inventing geography rather than adding load. Player structures ARE scaled, because how many houses a
    /// planet carries is a function of how many players it has.
    /// </remarks>
    public float PopulationScale = 1f;

    // ── The partitioning knobs under study ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Grid cell edge in metres. The headline tuning knob. <c>0</c> means "derive from the entity density" — see
    /// <see cref="ResolveCellSize"/>.
    /// </summary>
    public float CellSizeM;

    /// <summary>
    /// Cluster count at which a cell half promotes to a per-cell R-Tree. The engine's default, <see cref="int.MaxValue"/>, never promotes; a positive
    /// count forces the count-and-tightness gate.
    /// </summary>
    public int CellTreePromoteThreshold = SpatialOptions.DefaultCellTreePromoteThreshold;

    /// <summary>Mean cluster extent, as a fraction of the cell edge, at or below which a cell may promote. Read by the count gate only.</summary>
    public float CellTreePromoteTightness = SpatialOptions.DefaultCellTreePromoteTightness;

    /// <summary>Per-tick repair budget in milliseconds. A cliff, not a dial — see the spatial tuning guide.</summary>
    public float ReclusterBudgetMs = 1.0f;

    /// <summary>Multiplier on the per-cell packing bound that derives the intra-cell target extent.</summary>
    public float ClusterTargetPackingSlack = 1.5f;

    /// <summary>Floor of the drift gate as a fraction of the cell edge — the engine's <c>ClusterTargetExtentRatio</c>.</summary>
    public float ClusterTargetExtentRatio = 0.25f;

    /// <summary>Floor of the repair-nomination gate as a fraction of the cell edge — the engine's <c>ClusterRepairExtentRatio</c>.</summary>
    public float ClusterRepairExtentRatio = 0.75f;

    /// <summary>
    /// The repair safety valve's threshold — the engine's <c>ClusterRepairCriticalExtentRatio</c>. <c>0</c> disables the valve, which a repair ratio of
    /// 1 or more requires: the valve must sit strictly between the repair ratio and 1.2.
    /// </summary>
    public float ClusterRepairCriticalExtentRatio = 1.0f;

    /// <summary>Clusters per repair unit — the engine's <c>RepairWorstClustersPerUnit</c>; <c>0</c> re-sorts a whole cell as one unit.</summary>
    public int RepairWorstClustersPerUnit = 8;

    /// <summary>Ticks a just-repaired cell waits before it can be repaired again — the engine's <c>RepairCooldownTicks</c>; <c>0</c> disables it.</summary>
    public int RepairCooldownTicks = 50;

    /// <summary>
    /// How far above the best candidates per hit the queries may drift before maintenance gets the whole budget — the engine's
    /// <c>QueryEfficiencyTolerance</c>; <c>0</c> grants the configured budget every tick.
    /// </summary>
    public float QueryEfficiencyTolerance = 0.1f;

    // ── Realms (G1, #1019) ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Planets, each a realm with today's flat grid: planet 0 is Tatooine as it always was (realm 0); a further planet is its twin — the same map,
    /// populated from its own seed. One reproduces the single-world simulation, realm keys included.
    /// </summary>
    public int Planets = 1;

    /// <summary>
    /// NPCs standing in each interior.
    ///
    /// <para><b>Every city building has an interior; it is not a switch any more.</b> It was <c>--interiors</c>, off, because interiors were a realms
    /// feature being measured against a run without them — and a client that drew buildings as boxes had nothing to walk into anyway. Both ended: the client
    /// draws the published building, the published asset carries a doorway and a room behind it, and a world whose doors open onto nothing is the one
    /// version of this demo nobody wants to look at. Set <see cref="InteriorNpcs"/> to 0 for an empty room; there is no way to have no room.</para>
    /// </summary>
    /// <remarks>Per interior, NOT scaled by the population scale: an interior's crowd is a room's, whatever the planet's population. [EST]</remarks> [EST] Per interior,</summary>
    public int InteriorNpcs = 3;

    /// <summary>
    /// Re-check every colliding body's written position against a fresh spatial query, and count the ones that ended up
    /// inside a structure (the static neighbourhood's independent oracle).
    /// </summary>
    /// <remarks>
    /// <b>Off by default and only ever on in a test, because its whole point is to not trust the disc.</b> The audit asks the
    /// spatial layer from scratch, every tick, for every body that collided — which is exactly the work the disc exists to
    /// avoid, so it is not a thing to leave on. What it buys is an oracle that cannot be graded by the mechanism it is
    /// grading: a disc that wrongly decided nothing was near, or a push that left a body half inside a wall, is caught here
    /// whether or not the neighbourhood agrees.
    /// </remarks>
    public bool CollisionAudit;

    /// <summary>
    /// Bazaars, and separately mission terminals, per square kilometre of a city's disc, on top of the one of each beside its shuttleport's gate. [EST]
    /// </summary>
    /// <remarks>
    /// A city gets <c>1 + round(density × area)</c> of each, spread over the disc. At the default 8, Mos Eisley (456 m) has 6 of each, Bestine (336 m) 4,
    /// Anchorhead (125 m) and the two smallest towns 1 or 2 — enough that a player in a city of any size finds one within a short walk.
    /// </remarks>
    public float FacilityDensityPerKm2 = 8f;

    /// <summary>Share of in-city idle decisions that walk into a building instead. [EST]</summary>
    public float InteriorShare = 0.25f;

    /// <summary>Shortest stay in an interior, seconds; a stay lasts one to four times this. [EST]</summary>
    public float InteriorStayS = 30f;

    /// <summary>With more than one planet, the share of shuttle boardings bound for another planet's port — a realm change. [EST]</summary>
    public float InterPlanetShare = 0.2f;

    /// <summary>
    /// A space realm after the interiors: a deep 3D grid over a 16 km cube, holding <see cref="Starships"/> × population scale AI starships with f64
    /// bounds, flying between waypoints and scanning for each other. Off reproduces the simulation without space.
    /// </summary>
    public bool Space;

    /// <summary>Starships at population scale 1. [EST]</summary>
    public int Starships = 250;

    // ── Per-realm policy (Realms G2, phase D) ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unobserved seconds before an interior goes dormant — no system, no maintenance — until a player walks in (an entry wakes it, and a player inside
    /// pins it active). 0 keeps interiors simulated always.
    /// </summary>
    public float InteriorSleepS = 10f;

    /// <summary>
    /// How often a serving process counts what is standing in each realm, for the client's realm inventory. 0 = never.
    /// </summary>
    /// <remarks>
    /// <b>It applies only to a serving run, whatever this says.</b> The census system is added to the DAG beside the session system, under the same
    /// condition, because the measured runs are where this demo's CPU numbers come from and a once-a-second serial walk of every cluster is exactly the kind
    /// of term that makes two A/B arms incomparable while looking like nothing. A viewer pays it; a measurement does not.
    /// <para>
    /// The cadence is <c>TickRateHz / RealmCensusHz</c> ticks, truncated: 3 at 40 Hz is 13 ticks, which is 3.08 Hz. Near
    /// enough for a panel, and stated rather than left as a surprise.
    /// </para>
    /// </remarks>
    public float RealmCensusHz = 1f;

    /// <summary>Planets after the first are simulated at this divisor: each of their clusters once every N ticks, over N ticks' delta time. 1 = full rate.</summary>
    public int PlanetDivisor = 1;

    /// <summary>The space realm's divisor, as <see cref="PlanetDivisor"/>.</summary>
    public int SpaceDivisor = 1;

    /// <summary>Dungeon instances over the run: each a realm registered at run time when a party enters, and unregistered once emptied. 0 = none.</summary>
    public int Dungeons;

    /// <summary>Seconds between two dungeon openings.</summary>
    public float DungeonIntervalS = 10f;

    /// <summary>Seconds a party stays in its dungeon.</summary>
    public float DungeonStayS = 20f;

    /// <summary>Players in one party.</summary>
    public int DungeonParty = 8;

    /// <summary>Mobs spawned in one dungeon.</summary>
    public int DungeonMobs = 24;

    // ── Shuttles (#910) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Players also travel between cities by shuttle. Off reproduces the pre-shuttle simulation exactly.</summary>
    public bool Shuttles = true;

    /// <summary>Share of in-city travel decisions taken by shuttle rather than on foot or mounted. [EST]</summary>
    public float ShuttleShare = 0.5f;

    /// <summary>Seconds between two landings of one port's shuttle. [EST]</summary>
    public float ShuttleIntervalS = 120f;

    /// <summary>Seconds a landed shuttle takes passengers. [EST]</summary>
    public float BoardingWindowS = 30f;

    /// <summary>
    /// Board a port's whole queue on the landing tick. Off (the default), each queued passenger boards on its own tick inside the window — a
    /// ticket used while the shuttle is down transports that player at once — so arrivals trickle rather than land together.
    /// </summary>
    public bool ShuttleBurst;

    /// <summary>Time radius queries at shuttleports after arrivals against a steady port, and read those cells' tightness (#910's measurement).</summary>
    public bool Probe;

    /// <summary>
    /// Count the work of a sample of interest queries — cell halves, clusters scanned and opened, entities tested, hits, distinct pages — per queried
    /// archetype. The counting runs inside <c>Awareness</c>, so a run with it on is a count, not a timing.
    /// </summary>
    public bool WorkProbe;

    /// <summary>Time every chunk of the awareness system and report how evenly the chunks shared the work and the pool.</summary>
    public bool ChunkStats;

    /// <summary>Spawn count at which a transaction places its entities in per-cell Morton order.</summary>
    public int BatchSpawnSortThreshold = 128;

    /// <summary>
    /// Run the AABB2F narrowphase sixteen entities at a time with SIMD (the engine default). <c>--scalar-narrowphase</c> turns it off, so one binary
    /// is both arms of an A/B.
    /// </summary>
    public bool SimdNarrowphase = true;

    /// <summary>
    /// Write the move systems' positions one call per cluster (<c>ClusterRef.WriteSpatial</c>'s slot-set form, the default) or one per entity
    /// (<c>--per-entity-writespatial</c>), so one binary is both arms of an A/B.
    /// </summary>
    public bool BatchedSpatialWrites = true;

    // ── Runtime ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the scripted crowd is collided against structures as well. <b>On</b>; <c>--no-collide-crowd</c> turns it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It was off, as a measuring instrument, and it is on because the thing it was measuring is the thing being
    /// simulated.</b> The scripted crowd is 320 × <c>--pop</c> player-shaped bodies whose whole purpose is to stand for
    /// a player population — and a real player collides. A crowd that walks through walls was therefore under-stating
    /// what that population costs, in the one demo that exists to say what it costs. It also looks wrong the moment
    /// anybody watches it, which since 2026-10-22 they can: the client draws those bodies as characters.
    /// </para>
    /// <para>
    /// <b>Measured on 2026-10-22, 3 202 bodies against 101 blocking primitives across seven towns</b>, 16 km world at
    /// <c>--pop 10</c>, 168 458 entities, one live session at a 1 280 m radius: tick p50 <b>4.32 ms → 6.06 ms</b> of a
    /// 100 ms budget, no overlap across eight samples an arm, zero overruns. <c>PlayerMove</c> took 168 µs → 1 709 µs,
    /// which is 89 % of the delta and exactly where the loop is — about <b>0.48 µs per body per tick</b>. That figure
    /// is against THIS collider set, with the static-neighbourhood disc rejecting most candidates; a denser set, or
    /// bodies packed tightly enough to churn their discs, will not scale from it linearly.
    /// </para>
    /// <para>
    /// <b>Every tick figure published before that date was taken with this off</b>, so they are not comparable to a
    /// default run any more. <c>--no-collide-crowd</c> reproduces the old arm, and a measurement that compares against
    /// a number from the archive must pass it.
    /// </para>
    /// <para>
    /// <b>Why it is a config flag and not an environment variable like <c>SWG_NO_COLLISION</c>.</b> That one is an A/B
    /// switch and is a <see langword="static readonly"/> so both arms are the same binary and the JIT folds the branch.
    /// This one selects a POPULATION rather than toggling a code path, it belongs in the run's reported configuration,
    /// and its branch sits beside a controller-kind test the loop already performs — so it costs nothing to read here.
    /// </para>
    /// </remarks>
    public bool CollideCrowd = true;

    /// <summary>
    /// Simulation rate. SWG's server ran its main loop at roughly 10 Hz for AI and movement broadcast; this is the tick
    /// the system DAG is driven at and what a per-tick cost is measured against.
    /// </summary>
    public int TickRateHz = 10;

    /// <summary>
    /// Run ticks back to back rather than at <see cref="TickRateHz"/>, with the runtime's overload response off (<c>--unpaced</c>), so a count-only
    /// comparison (candidates per hit, migrations, budget granted) takes the time the ticks take, not the time the clock allows. The simulation's own
    /// intervals are unchanged — distance per tick, AI and shuttle intervals all come from <see cref="TickRateHz"/> — but the engine prices maintenance on
    /// measured cost, and busy workers measure differently from parked ones, so admissions can shift: validated once against paced runs (16×, Creature's
    /// candidates per hit within 1 %, migrations equal), not guaranteed. Not for a timing A/B. The run's last tick may still be in flight when the
    /// summary prints.
    /// </summary>
    public bool Unpaced;

    /// <summary>
    /// Restore the pre-#927 packing bound: computed from <c>CellState.EntityCount</c>, the grid-wide sum over every archetype sharing the cell, rather than
    /// from this archetype's own population. The A/B arm, so the comparison is one binary and one switch rather than two builds.
    /// </summary>
    public bool GridWideBound;

    /// <summary>
    /// Restore the pre-#949 behaviour: re-rank the repair queue on every planning tick, even when the granted budget cannot pay for the cheapest unit and
    /// only the safety valve can be admitted. The A/B arm, so the comparison is one binary and one switch rather than two builds.
    /// </summary>
    public bool RankWhenStarved;

    /// <summary>Worker threads for the system DAG and the parallel fence. <c>0</c> means <see cref="Environment.ProcessorCount"/>.</summary>
    public int WorkerCount;

    /// <summary>Ticks to run before measurement starts, so JIT and the first checkpoint are not in the numbers.</summary>
    public int WarmTicks = 40;

    /// <summary>Measured ticks.</summary>
    public int MeasuredTicks = 200;

    /// <summary>Page cache size in MiB. The static world alone is large, so this is not the place to be frugal.</summary>
    public int PageCacheMiB = 1024;

    /// <summary>
    /// Where the database file lives. The simulation runs on a REAL on-disk database with a real WAL — most archetypes
    /// declare <c>ClusterDurability.Checkpoint</c> so their per-tick values ride the checkpoint rather than the WAL, but
    /// the durability machinery is genuinely in the loop rather than stubbed out.
    /// </summary>
    public string DatabaseDirectory;

    /// <summary>
    /// The database's file name, without a directory or an extension. <c>--db-name</c>; <see cref="DefaultDatabaseName"/> by default (P-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The process id used to be in here, and that is the 154 GB incident.</b> The name was
    /// <c>SwgTatooine_{Environment.ProcessId}</c>, so every run — and every run KILLED before it could delete its own file — left a database of its own. A
    /// campaign of a few dozen interrupted runs accumulated 154 GB of multi-gigabyte files that nothing would ever clean up, because the only process that knew
    /// the name was gone.
    /// </para>
    /// <para>
    /// A single stable name makes that structural rather than a rule to remember: a killed run leaves ONE database, and the next run reuses or deletes it.
    /// That is also the precondition for <see cref="Persist"/> meaning anything — a world you cannot name is a world you cannot reopen.
    /// </para>
    /// </remarks>
    public string DatabaseName = DefaultDatabaseName;

    /// <summary>The database name a run uses when <c>--db-name</c> is not given.</summary>
    public const string DefaultDatabaseName = "SwgTatooine";

    /// <summary>
    /// <c>--persist</c>: keep the database across runs and REOPEN it instead of building a fresh world (P-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Delete-on-start stays the default, and the asymmetry of the two failure modes is the whole reason.</b> A run that wrongly persists measures a world
    /// some earlier run left behind — a different population, a different cluster geometry, creatures that have already wandered — and says nothing about it,
    /// so an A/B pair silently compares two different worlds. A run that wrongly starts fresh loses a demonstration world and is obvious the moment anybody
    /// looks. The reproducibility of every number in this repository depends on the first of those not happening quietly, which is why the opt-in is the
    /// persisting direction and not the other one.
    /// </para>
    /// <para>
    /// There is deliberately no <c>--fresh</c>. Two flags with opposite senses is how the two source items (SWG-04 and CLI-05) came to propose opposite
    /// defaults, and a surface with both would let a command line say the same thing twice or contradict itself.
    /// </para>
    /// </remarks>
    public bool Persist;

    // ── Live profiling ──────────────────────────────────────────────────────────────────────────────────────────────
    //
    // These three are READ HERE and ACTED ON ELSEWHERE, which is unlike every other field in this class. The profiler's master gate is a static readonly bool
    // in the engine, baked at assembly load, so the only layer that can still open it is the environment — and `ProfilerArgs`, called from `Program.Main`
    // before any engine type is touched, is what writes it. They live in SimConfig anyway so that `--help` lists them and the strict parse accepts them from
    // one declaration, as it does for every other flag (SWG-07).

    // ── Hosting ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--bind</c>: the address the web host binds, or <see langword="null"/> for loopback.
    /// </summary>
    /// <remarks>
    /// <b>Loopback is the default, and it is a change.</b> The host used to call <c>ListenAnyIP</c> unconditionally, so
    /// <c>--serve 8080</c> on a laptop published the world to whatever network the laptop was on. That is survivable
    /// while a session can only watch; it stops being survivable the day there is an account to create. Exposing the
    /// server is now something an operator says out loud.
    /// </remarks>
    public string BindAddress;

    /// <summary>
    /// <c>--origin</c>: origins a browser may connect from, comma-separated. Empty allows any, which is refused on a non-loopback bind.
    /// </summary>
    /// <remarks>
    /// Any origin is right for a dev box, where the Vite port is not the server's. On a reachable bind it would let any
    /// page a visitor opens drive this server from their browser, so there it is required rather than defaulted.
    /// </remarks>
    public string[] Origins = [];

    /// <summary>
    /// <c>--client-dir</c>: where the built browser client is, or <see langword="null"/> to look for it in the repository.
    /// </summary>
    /// <remarks>
    /// <b>Without this a published server serves no client at all.</b> The fallback walks up from the binary looking for
    /// <c>demo/SwgTatooine/SwgTatooine.Client/dist</c>, which exists in a dev tree and in no deployment — so the artefact
    /// <c>scripts/publish-swg.ps1</c> builds would answer 404 for the root and leave nothing but a WebSocket. The flag is
    /// what the deployed unit passes.
    /// </remarks>
    public string ClientDirectory;

    /// <summary>
    /// <c>--assets</c>: the published asset folder to serve at <c>/assets</c> and collide against, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SwgTatooine.Assets/build/assets</c> (fetched by its <c>scripts/assets.py fetch published</c>, or built there): flat,
    /// revision-named GLBs, descriptors and shared KTX2 textures, with a <c>catalogue.json</c> pointer. The server pins the immutable catalogue that pointer
    /// names and announces it in <c>/typhon/demo.json</c>, so the client never has to discover a version — see <see cref="AssetOrigin"/>.
    /// </para>
    /// <para>
    /// <b>A served run always has one.</b> Unset, <c>Program</c> finds it beside the binary or in the repository, and refuses to serve when it cannot
    /// (<see cref="TatooineSim.ServedAssetRoot"/>). Unset on a measured run means no assets: footprint boxes only, the shape the checks build directly.
    /// </para>
    /// </remarks>
    public string AssetDirectory;

    /// <summary>
    /// <c>--live</c>: the TCP port the profiler serves the Workbench on; 0 is off.
    /// </summary>
    /// <remarks>
    /// Off by default, and the default matters: the exporter costs tick time and the numbers in this repository are taken without it. The engine binds it to
    /// loopback unless <see cref="ProfilerBindAddress"/> says otherwise, so turning it on does not publish telemetry off-box.
    /// </remarks>
    public int ProfilerLivePort;

    /// <summary>
    /// <c>--profiler-bind</c>: the address <see cref="ProfilerLivePort"/> binds, or <see langword="null"/> for the engine's loopback default.
    /// </summary>
    /// <remarks>
    /// There is no validation of the address here because the engine's own parser is the authority on what an address is
    /// (<c>IPAddress.TryParse</c>, in <c>ProfilerLaunchConfig</c>), and it ignores one it cannot read — which leaves the loopback default, the safe direction.
    /// </remarks>
    public string ProfilerBindAddress;

    /// <summary>
    /// <c>--trace</c>: where to write the capture, or <see langword="null"/> for the database's own <c>profilings/</c> directory.
    /// </summary>
    public string ProfilerTracePath;

    /// <summary>Run the tick fence on the worker pool rather than serially on the tick driver.</summary>
    public bool ParallelFence = true;

    /// <summary>
    /// <c>--dormancy N</c>: consecutive clean ticks before a cluster is put to sleep. 0 disables cluster dormancy, which is the engine default.
    /// </summary>
    /// <remarks>
    /// <b>A sleeping cluster is skipped by system dispatch AND by the replication projection pass.</b> The engine computes this at the tick fence from
    /// the per-entity dirty bitmap, so it costs replication nothing to consult and nothing to maintain.
    /// <para>
    /// <b>It requires the simulation to mark its spatial writes.</b> <c>ClusterRef.WriteSpatial</c> deliberately raises no dirty bit, so a cluster whose
    /// creatures only MOVE looks clean to the sweep and would be put to sleep while still in motion — after which nothing dispatches it and the creatures
    /// freeze. <see cref="SimBridge"/> therefore marks a moved cluster dirty whenever this is non-zero. That is the documented contract for combining
    /// <c>WriteSpatial</c> with dormancy, not a workaround.
    /// </para>
    /// </remarks>
    public int DormancyTicks;

    /// <summary>
    /// <c>--subs-phases</c>: accumulate per-phase timings inside the frame stage (gather, sort, encode, publish).
    /// </summary>
    /// <remarks>
    /// Off by default because it costs one static read per phase boundary. It exists because a sampling profiler cannot answer the question: the frame
    /// stage is one method and every callee that matters is inlined in Release, so the profile attributes the whole stage to one frame.
    /// </remarks>
    public bool SubscriptionsPhaseTiming;

    /// <summary>
    /// <c>--subs-mode push|push-auto</c>: who detects a change. <c>push</c> (default) is the simulation's <c>Replicate</c> calls (ADR-067);
    /// <c>push-auto</c> is the engine comparing every live entity (<see cref="Typhon.Engine.PushDetection.Automatic"/>, experimental), which makes those
    /// calls redundant.
    /// </summary>
    public bool SubscriptionsPushAutomatic;

    /// <summary>Each player session's outbound byte budget, bytes per second (<c>--session-budget</c>); 0 for none. Drives the per-session LOD level.</summary>
    public int SessionBudgetBytesPerSecond;

    /// <summary>
    /// <c>--ingress-budget B</c>: each session's inbound budget, bytes per second. 16 KiB/s: a god camera's region every few frames and a player's
    /// commands, with room to spare — the demo's own sizing of a rail the engine will not default.
    /// </summary>
    public int IngressBytesPerSecond = 16 * 1024;

    /// <summary>
    /// <c>--max-clients N</c>: how many player sessions are admitted at once; 0 (the default) is unlimited.
    /// </summary>
    /// <remarks>
    /// <b>A cap is what makes the refusal path exist at all (SWG-07).</b> Without one the demo accepted every connection, so the one thing every real server
    /// does under load — say no, with a reason a client can act on — was never exercised, and neither was the engine's admission hook. The cap counts
    /// admitted sessions of each role separately because a full house of spectators must not lock players out of their own world.
    /// </remarks>
    public int MaxClients;

    /// <summary><c>--max-spectators N</c>: how many god-camera sessions are admitted at once; 0 (the default) is unlimited.</summary>
    public int MaxSpectators;

    /// <summary>
    /// <c>--avatar-save-interval S</c>: how often a live avatar's position is written back to its character, seconds; 0 disables it.
    /// </summary>
    /// <remarks>
    /// <b>This number is the bound on what a hard kill costs a player</b> (A6, acceptance criterion A-5): a logout always saves, but
    /// a <c>SIGKILL</c>, a crash or an operator stopping the instance runs no logout, so without the cadence a whole session of
    /// walking is lost. Ten seconds is about seventy metres at the run speed — recognisable but not a session. Zero is for a
    /// measurement run that wants the write out of its numbers, and says so by being explicit.
    /// </remarks>
    public double AvatarSaveIntervalS = TatooineReplication.DefaultSaveIntervalSeconds;

    /// <summary>
    /// <c>--spawn-spread S</c>: a scale on every town's own radius, over which new characters are spread.
    /// <c>1</c> is each town as the map draws it; <c>0</c> puts every new character on one point in the capital.
    /// </summary>
    /// <remarks>
    /// <b>Arrivals are distributed across ALL SEVEN towns by the map's own <c>playerWeight</c>, not stacked in the capital.</b> Zero was
    /// the original behaviour and it is pathological: every new character at exactly one point means each one sits inside every other
    /// one's 192 m sphere, so each replica holds the whole population and the client's per-session cost grows with the crowd instead of
    /// with the world. It also looks like a bug in the client, because a player logs in to a world reporting ten thousand players and
    /// sees nobody — the crowd is one tile wide and they are not standing on it.
    /// <para>
    /// One town is not enough either, and that is what this scale is for. Ten thousand arrivals in the capital's 456 m disc still put
    /// ~1 770 bodies in every session's sphere: 124 records a frame, 54 GB of traffic in ninety seconds, and a fleet process that died
    /// of it while the server held its tick budget. Seven towns divide the AVERAGE by about three — not by seven, because the weights
    /// are population shares and the radii are physical sizes, and they are not proportional. The scale is the honest knob: density is
    /// what a crowd test varies, so it should be a number somebody chose.
    /// </para>
    /// <para>
    /// Which town and where inside it are both derived from the account id, so the same account returns to the same doorstep and a test
    /// gets a reproducible world without pinning a seed.
    /// </para>
    /// </remarks>
    public double SpawnSpread = 1d;

    /// <summary>
    /// <c>--max-sessions N</c>: how many clients may be connected at once — the engine's session table, allocated once at start-up.
    /// </summary>
    /// <remarks>
    /// <b>Restated here rather than left at the engine's default, because the default is what a 10 000-bot run hits.</b>
    /// <c>SubscriptionsOptions.MaxSessions</c> is 8 192 and nothing in this demo set it, so a fleet asking for ten thousand opened
    /// 8 192 and then simply stopped: admission past the table is refused at connect time, the server said nothing about why, and the
    /// generator reported "8 191 of 8 191 opened" as though that were the number it had asked for. For a demo whose own bot document
    /// targets 5-10 k sessions, the one option that bounds exactly that should not be unreachable from the command line.
    /// <para>
    /// 65 535 is structural and not a policy: a session identity packs a 16-bit slot and a 16-bit generation into 32 bits, so a slot
    /// wider than a <c>ushort</c> does not exist. Between the two, the cost is memory — several arrays of this many entries inside
    /// the frame assembler, allocated whether or not the sessions arrive.
    /// </para>
    /// </remarks>
    public int MaxSessions = 8192;

    /// <summary>
    /// <c>--resume-grace S</c>: how long a link-dead session's avatar stays standing for its client to come back to, seconds; 0 disables resume entirely.
    /// </summary>
    /// <remarks>
    /// <b>This number is what a dropped connection costs a player, and what it costs everyone else</b> (A13). Within it, a client that reconnects is handed
    /// the same body where it stood, with no login and no respawn; past it the avatar is saved and destroyed as an ordinary logout. The cost is that the body
    /// is in the world the whole time, visible and standing still, and that its account cannot be logged in by a client that has lost its token without
    /// taking that body over. The engine's own default is 60 s, which is what a mobile client losing a tower needs; this passes it through so that a
    /// measurement run, or a test that needs a grace to elapse, can say otherwise.
    /// </remarks>
    public double ResumeGraceS = 60d;

    /// <summary>
    /// <c>--auth-burst N</c>: how many <c>/auth</c> attempts one key may make at once; <c>0</c> keeps <see cref="AuthRateLimiter.Capacity"/>.
    /// </summary>
    /// <remarks>
    /// <b>This exists for a bot fleet and the default must not move for anybody else.</b> The limiter keys on the client's address prefix
    /// — a /24 for IPv4 — so an entire bot box is ONE key, and ten attempts with one refilling every six seconds means a thousand bots take
    /// about ninety-nine minutes to sign up
    /// or log in. That is correct behaviour against a dictionary run and useless against a load generator you own. Raised only on a server that is being
    /// loaded on purpose, which is why it is a flag and not a different default: every real deployment keeps ten.
    /// </remarks>
    public int AuthBurst;

    /// <summary>
    /// <c>--auth-refill-ms M</c>: how long one <c>/auth</c> attempt takes to come back, in milliseconds; <c>0</c> keeps the six-second default.
    /// </summary>
    /// <remarks>See <see cref="AuthBurst"/>. The pair of them is the whole knob, and the sustained rate per key is <c>1000 / M</c> per second.</remarks>
    public int AuthRefillMs;

    /// <summary>
    /// The shared secret a request from outside the VPC must carry in <c>X-Tatooine-Origin</c>; empty disables the check.
    /// </summary>
    /// <remarks>
    /// <b>From the environment, never from the command line.</b> A secret in an argument is visible in <c>ps</c> to every
    /// user on the box and is written to the shell's history; <c>TATOOINE_ORIGIN_SECRET</c> goes in the unit file's
    /// <c>EnvironmentFile=</c>, which is mode 0640 and owned by root.
    /// <para>
    /// Empty is correct on a developer's machine, where the bind is loopback and there is no front door. On a reachable
    /// bind the server says loudly that it is open; see <c>TatooineHost.ServeAsync</c> for why the security group alone
    /// is not enough.
    /// </para>
    /// </remarks>
    public string OriginSecret = Environment.GetEnvironmentVariable("TATOOINE_ORIGIN_SECRET") ?? string.Empty;

    /// <summary>
    /// <c>--tls-chain &lt;path&gt;</c>: a PEM chain to serve HTTPS with; empty serves plain HTTP.
    /// </summary>
    /// <remarks>
    /// certbot's <c>fullchain.pem</c>. Paired with <see cref="TlsKeyPath"/>, and both are required together — the parse refuses
    /// one without the other, because a server that quietly fell back to HTTP after being asked for TLS is an outage that reads
    /// as a browser problem.
    /// </remarks>
    public string TlsChainPath = string.Empty;

    /// <summary><c>--tls-key &lt;path&gt;</c>: the PEM private key matching <see cref="TlsChainPath"/>.</summary>
    public string TlsKeyPath = string.Empty;

    /// <summary><c>--tls-port N</c>: the port HTTPS is served on. 443 needs <c>CAP_NET_BIND_SERVICE</c>.</summary>
    public int TlsPort = 443;

    /// <summary><c>--redirect-port N</c>: a port serving nothing but a 308 to the TLS one; 0 maps none.</summary>
    /// <remarks>80 in a deployment, so somebody typing the bare hostname arrives at the right scheme.</remarks>
    public int RedirectPort;

    /// <summary>
    /// <c>--auth-local-only</c>: answer <c>/auth</c> only for callers on a private address.
    /// </summary>
    /// <remarks>
    /// <b>Required on a public bind that has no CDN in front of it.</b> A signup is PBKDF2 at 600 000 iterations behind a
    /// semaphore of <c>ProcessorCount / 4</c>, so junk attempts cost this process ~51 ms of a core each while costing the caller
    /// one HTTP request — a few hundred a second and the tick loop stops getting CPU. Nothing public needs the endpoint: god
    /// sessions are anonymous, and the bot fleet signs up over the private address. See
    /// <c>TatooineHost.UseAuthInsideVpcOnly</c>.
    /// </remarks>
    public bool AuthLocalOnly;

    /// <summary>
    /// <c>--auth-iterations N</c>: the PBKDF2 work factor for NEW passwords; 0 keeps <see cref="PasswordHash.DefaultIterations"/>.
    /// </summary>
    /// <remarks>
    /// <b>A throwaway demo may reasonably buy speed with it; nothing that holds real credentials should.</b> 600 000 is
    /// OWASP's current recommendation and costs ~51 ms of a core per attempt, which on a box also running a 50 Hz tick IS
    /// the denial-of-service surface. Lowering it cuts that proportionally and weakens only resistance to offline cracking
    /// of a stolen hash table -- online guessing is the rate limiter's job, not the work factor's.
    /// <para>
    /// Safe for existing accounts: the work factor is stored per account and verification uses the stored one, so only new
    /// passwords are affected. Floor of <see cref="PasswordHash.MinimumIterations"/>, because below that the hashing reads
    /// as protection without being any.
    /// </para>
    /// </remarks>
    public int AuthIterations;

    /// <summary><c>--player-leave M</c>: the players' leave radius, metres; 0 (the default) for none. The Phase 2 criteria run at 192/208 m.</summary>
    public double PlayerLeaveM;

    /// <summary>
    /// <c>--god-region E</c>: god sessions observe their client's hull (<c>ClientRegion</c>, largest edge E metres) with a near budget and an aggregate,
    /// instead of the whole world; 0 (the default) keeps the <c>World</c> god camera. The shape AC-3 measures.
    /// </summary>
    public double GodRegionMaxEdgeM;

    /// <summary><c>--god-near N</c>: the god region's near budget, entities; AC-3's 10 000 by default.</summary>
    public int GodNearBudget = 10_000;

    /// <summary>Idle iterations a scheduler worker spins inside a tick before yielding (<c>--idle-spin N</c>).</summary>
    public int WorkerIdleSpin = 100;

    /// <summary>
    /// <c>--sched-hot N</c>: idle workers that keep spinning inside a dispatch while the rest park; negative for the legacy spin-then-yield loop. Unset
    /// leaves the engine's default (<see cref="Typhon.Engine.DagScheduler.WorkerHotSpinners"/>).
    /// </summary>
    public int? WorkerHotSpinners;

    /// <summary><c>--sched-park-us N</c>: how long an idle worker spins before it may park. Unset leaves the engine's default.</summary>
    public int? WorkerParkAfterUs;

    /// <summary>
    /// <c>--idle-creatures F</c>: the fraction of spawned creatures that are ambient — never think, never move. 0 by default.
    /// </summary>
    /// <remarks>
    /// <b>This exists so the demo can express more than one workload shape.</b> Its default simulation writes <c>CreatureBrain</c> for every living
    /// creature on every tick (<c>ai.ThinkCooldown--</c>), which is one shape among many and happens to be the shape that defeats every change-detection
    /// mechanism. A real world has large ambient populations — vendors, civilians, distant spawns nobody has aggroed — and <see cref="AiMode.Idle"/>
    /// already names exactly that. Raising this makes whole clusters genuinely quiet, which is what dormancy is for.
    /// </remarks>
    public double IdleCreatureFraction;

    /// <summary>
    /// Smallest entity count the runtime will give a parallel chunk. The engine default is 64.
    /// </summary>
    /// <remarks>
    /// <para><b>This is what caps the awareness system's parallelism, and it is not obvious.</b> Chunk count is
    /// <c>min(round(WorkerCount x ChunksPerWorker), ceil(entityCount / ParallelQueryMinChunkSize))</c>. With 320 players
    /// the second term is <c>ceil(320 / 64) = 5</c>, so the system that is half the tick runs on five workers however
    /// high <c>ChunksPerWorker</c> is set. Raising the oversubscription factor does nothing; this is the knob.</para>
    /// <para>It is global rather than per-system, so lowering it splits every parallel system more finely — which is the
    /// cost side of the trade and the reason to measure rather than assume.</para>
    /// </remarks>
    public int ParallelQueryMinChunkSize = 64;

    /// <summary>
    /// <c>RuntimeOptions.CostBasedChunking</c>: parallel systems split by their measured cost (the default) or by entity count (<c>--entity-chunking</c>).
    /// </summary>
    public bool CostBasedChunking = true;

    /// <summary><c>--tick-log</c>: a file to write every measured tick's duration to, in ms, one per line in tick order. Null writes nothing.</summary>
    public string TickLogPath;

    /// <summary>
    /// Split interest management into one system per queried archetype instead of one system running four queries.
    /// </summary>
    /// <remarks>
    /// The other way at the same problem, and it needs no global setting: four systems over the same 320 players each
    /// get their own five chunks, and since they share no write they run concurrently — twenty workers busy instead of
    /// five, with the same total work.
    /// </remarks>
    public bool SplitAwareness;

    /// <summary>
    /// Keep the awareness system in the schedule even when replication is declared (<c>--awareness</c>).
    /// </summary>
    /// <remarks>
    /// <b>Under <c>serve</c>, awareness is a phantom pass and is dropped by default (#950, S0-6).</b> Replication computes each session's interest for real;
    /// <c>AwarenessSystem</c> then computed the same interest again and threw it away as a <c>Count()</c>. So the server's tick carried a whole second
    /// interest pass that nothing read, and every per-hit figure the README quotes describes a caller that counts rather than one that produces lists.
    /// <para>
    /// The system is still worth having as a <i>labelled</i> spatial-query benchmark — it is the cleanest interest-query workload in the demo — which is what
    /// this flag keeps. In <c>run</c> (no sessions) it is the interest system and is always scheduled.
    /// </para>
    /// </remarks>
    public bool ForceAwareness;

    /// <summary>Where <c>--sweep</c> writes its report (<c>--report-dir</c>). Empty picks the docs repo when present, else the binary's directory (#947).</summary>
    public string ReportDirectory;

    /// <summary>
    /// [CORE3] Seconds before a wild lair revives a killed creature (<c>--respawn-s</c>). A uniform 120-240 s in <c>LairObserver.idl</c>; the midpoint.
    /// </summary>
    /// <remarks>
    /// Configurable because at the shipped 180 s nothing revives inside a 200-tick measurement, so the revival path — the one teleport in the simulation, and
    /// the largest position jump it makes — is never exercised by a short run or by a test. A destroy-mission lair never respawns at all.
    /// </remarks>
    public float RespawnSeconds = 180f;

    /// <summary>
    /// Per-system <c>MinChunkSize</c> for the awareness system only. <c>0</c> leaves it on the global floor.
    /// </summary>
    /// <remarks>
    /// The scoped form of <see cref="ParallelQueryMinChunkSize"/>. Lowering the GLOBAL floor won 1.57x at 320 players and
    /// lost 9 % at 5 120, because it split every system finer including the ones that did not need it; this setting is the
    /// same lever aimed at the one system whose per-entity cost justifies it.
    /// </remarks>
    public int AwarenessMinChunk;

    /// <summary>
    /// How the awareness system drains each interest query. <see cref="AwarenessApi.MoveNext"/> is one call per hit; <c>Count()</c> is one per query.
    /// </summary>
    public AwarenessApi AwarenessApi = AwarenessApi.Count;

    /// <summary>
    /// How the creature-combat system asks which players are in range: one query per creature, or one batch per creature cluster.
    /// </summary>

    /// <summary>Seed for every random decision, so a run is reproducible and two arms see the same world.</summary>
    public int Seed = 20260907;

    /// <summary>
    /// <c>--fault-at-tick N</c>: throw from a system on tick <c>N</c>, to exercise the crash artefact (P-3). Zero never faults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A real fault through the real path, because the artefact cannot be tested any other way.</b> AC-3 asks for an artefact produced by an induced
    /// <c>OnTickAborted</c>, and the only honest way to induce one is for a system body to throw the way a defect would: the runtime then decides the tick is
    /// aborted, raises the event, and the handler writes the directory. Calling the writer directly from a test would assert that a file-writing method writes
    /// files — which is not the claim.
    /// </para>
    /// <para>
    /// A measurement flag, and refused with <c>--serve</c> alongside the probes: a server that deliberately kills itself on a tick is not a server. It is also
    /// kept out of <see cref="Label"/>, because a run that faults produces no measurement to label.
    /// </para>
    /// </remarks>
    public int FaultAtTick;

    // ── Mode ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--serve P</c>: the port to serve the world on instead of measuring it; 0 (the default) measures.
    /// </summary>
    /// <remarks>
    /// Parsed with every other flag rather than separately in <c>Program</c>, so that the strict parser can account for it. A mode flag the argument reader
    /// does not know about is a token it would have to refuse — see <see cref="ArgReader"/>.
    /// </remarks>
    public int ServePort;

    /// <summary><c>--sweep</c>: run the partitioning matrix instead of one configuration.</summary>
    public bool RunSweep;

    /// <summary><c>--sweep-worlds</c>: the sweep's world-edge axis, km.</summary>
    /// <remarks>
    /// <b>Defaulted here as well as in the parser, because a configuration is not always parsed.</b> A test, or any code that builds a
    /// <see cref="SimConfig"/> directly and calls <c>Sweep.Run</c>, would otherwise hand it a null axis and get a <c>NullReferenceException</c> out of the
    /// matrix loop. The same three defaults appear in <see cref="CommandLine"/> so that <c>--help</c> can print them.
    /// </remarks>
    public float[] SweepWorlds = [TatooineData.PlanetEdgeM / 1000f, 64f, 128f];

    /// <summary><c>--sweep-pops</c>: the sweep's population-scale axis.</summary>
    public float[] SweepPops = [1f, 4f, 16f];

    /// <summary><c>--sweep-cells</c>: the sweep's cell-size axis, metres at the real planet's scale.</summary>
    public float[] SweepCells = [64f, 128f, 256f, 512f, 1024f];

    /// <summary>The flag list, set instead of a configuration when the command line asked for <c>--help</c>; null otherwise.</summary>
    public string HelpText;

    // ── Derived ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Planet edge in metres.</summary>
    public float WorldEdgeM => WorldEdgeKm * 1000f;

    /// <summary>
    /// Factor by which authentic Tatooine coordinates are stretched to fill this world. 1 at the real 16 km size.
    /// </summary>
    public float ContentScale => ScaleContentWithWorld ? WorldEdgeM / TatooineData.PlanetEdgeM : 1f;

    /// <summary>
    /// The cell size to configure the grid with. An explicit <see cref="CellSizeM"/> wins; otherwise 256 m scaled with
    /// the world — a 64 x 64 grid over the real planet.
    /// </summary>
    /// <remarks>
    /// The derivation is deliberately crude — it exists so that a run with no <c>--cell</c> argument is sane, not so that
    /// it is optimal. Finding the optimum is the whole point of the sweep, and a clever default would bias it.
    /// </remarks>
    public float ResolveCellSize() => CellSizeM > 0f ? CellSizeM : 256f * ContentScale;

    /// <summary>Worker count to actually use.</summary>
    public int ResolveWorkerCount() => WorkerCount > 0 ? WorkerCount : Environment.ProcessorCount;

    /// <summary>A short label identifying this configuration in a results table.</summary>
    /// <remarks>Deliberately omits <see cref="AwarenessApi"/> and <see cref="SimdNarrowphase"/>: none changes the workload, and
    /// the label keys sweep results.</remarks>
    public string Label =>
        $"{WorldEdgeKm:N0}km x{PopulationScale:N1} cell={ResolveCellSize():N0}m floors={ClusterTargetExtentRatio:G}/{ClusterRepairExtentRatio:G} "
        + $"unit={RepairWorstClustersPerUnit} shuttles={(!Shuttles ? "off" : ShuttleBurst ? "burst" : "trickle")}{(Unpaced ? " unpaced" : "")}"
        + $"{(Planets > 1 ? $" planets={Planets}" : "")}{(Space ? " space" : "")}";
}

/// <summary>How the awareness system drains its interest queries.</summary>
public enum AwarenessApi
{
    /// <summary>One <c>MoveNext</c> per hit.</summary>
    MoveNext,

    /// <summary>One <c>Count()</c> per query.</summary>
    Count,

    /// <summary><c>Fill(Span)</c> into a per-thread 64-result buffer until the query is exhausted.</summary>
    Fill,

    /// <summary>
    /// One <c>CountRadius</c> per source cluster and target archetype: the cluster's players share one cell walk. <c>--work-probe</c> still replays each
    /// player's single query, so it reports per-query work, not what the batch saved.
    /// </summary>
    Batch,
}
