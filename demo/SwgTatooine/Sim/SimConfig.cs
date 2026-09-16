using System;

namespace SwgTatooine;

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

    /// <summary>Run the tick fence on the worker pool rather than serially on the tick driver.</summary>
    public bool ParallelFence = true;

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
    public CombatApi CombatApi = CombatApi.MoveNext;

    /// <summary>
    /// Which side of combat runs the range test: each creature asks which players are in range, or each player publishes the creatures in its range to an
    /// event queue that the creature side applies. Both decide exactly the same hits.
    /// </summary>
    public CombatModel CombatModel = CombatModel.Pull;

    /// <summary>
    /// Push only: every creature ready to take fire also runs pull's query, and the run counts creatures whose two answers differ. A correctness check, not
    /// a measurement — it pays for both models.
    /// </summary>
    public bool CombatVerify;

    /// <summary>Seed for every random decision, so a run is reproducible and two arms see the same world.</summary>
    public int Seed = 20260907;

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
    /// <remarks>Deliberately omits <see cref="AwarenessApi"/>, <see cref="CombatApi"/>, <see cref="CombatModel"/> and <see cref="SimdNarrowphase"/>: none
    /// changes the workload, and the label keys sweep results.</remarks>
    public string Label =>
        $"{WorldEdgeKm:N0}km x{PopulationScale:N1} cell={ResolveCellSize():N0}m floors={ClusterTargetExtentRatio:G}/{ClusterRepairExtentRatio:G} "
        + $"unit={RepairWorstClustersPerUnit} shuttles={(!Shuttles ? "off" : ShuttleBurst ? "burst" : "trickle")}{(Unpaced ? " unpaced" : "")}";
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

/// <summary>How the creature-combat system asks which players are in range.</summary>
public enum CombatApi
{
    /// <summary>One radius query per creature, drained with <c>MoveNext</c> up to four hits.</summary>
    MoveNext,

    /// <summary>One <c>ForEachInRadius</c> per creature cluster, each creature retiring at four hits.</summary>
    Batch,
}

/// <summary>Which side of combat runs the range test.</summary>
public enum CombatModel
{
    /// <summary>Every creature ready to take fire runs one radius query for players and applies its own damage.</summary>
    Pull,

    /// <summary>
    /// Every player runs one radius query for creatures and pushes one event per creature in range; a serial drain folds them into per-creature shooter
    /// counts, which the creature side applies.
    /// </summary>
    Push,
}
