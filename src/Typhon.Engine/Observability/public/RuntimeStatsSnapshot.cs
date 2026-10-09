using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A point-in-time read of the numbers an operator watches: tick percentiles, overruns, the durability wait, per-system cost, entities per archetype, and
/// the session figures when replication is running. Produced by <see cref="TyphonRuntime.ReadStats"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside the <c>STATS</c> wire block.</b> The same values were already computed once a second and sent to subscribing game clients, and
/// to nothing else: the encoder holds them in a private array and its only exit is a frame. So a <c>curl</c>, a CLI verb, a dashboard or a log line could
/// not read one of them. Worse, the computation is gated twice over — it happens only inside a subscriptions runtime, and only when the APPLICATION's
/// catalog declares metrics — so an engine with replication off, or one whose app declared none, computed nothing at all. This reads the sources directly
/// and therefore answers on any running engine.
/// </para>
/// <para>
/// <b>Read at human rate, not on the tick path.</b> It allocates (one snapshot, a few arrays) and walks the telemetry window, so it belongs behind an HTTP
/// endpoint, a CLI verb or a log timer — not in a system. Every figure outside <see cref="RuntimeStatsSnapshot.Realms"/> is either an instantaneous level or
/// a percentile over the window the caller asked for, so it needs no differencing.
/// </para>
/// <para>
/// <b><see cref="RuntimeStatsSnapshot.Realms"/> is the exception, and deliberately.</b> Its work figures are cumulative since the process started, because the
/// question they exist to answer is "has this realm EVER cost anything" — a realm nobody is in is supposed to contribute exactly zero, and a per-tick level
/// cannot tell "zero this tick" from "zero always". Difference two reads for a rate.
/// </para>
/// <para>
/// <b>The telemetry ring is a single-writer diagnostic structure with no publication protocol</b>, so a sample read while the tick driver is writing it may
/// be torn. The consequence is one perturbed sample in a percentile, which is why the ring is read here rather than mirrored into something that would have
/// to be written on the tick path to gain nothing. The <c>STATS</c> encoder accepts the same trade for the same reason.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class RuntimeStatsSnapshot
{
    /// <summary>The tick the window ends at — the newest tick the ring had recorded when the snapshot was taken.</summary>
    public long Tick { get; init; }

    /// <summary>How many recorded ticks the percentiles below were computed over. Zero means the ring was empty and every percentile is zero.</summary>
    public int TicksInWindow { get; init; }

    /// <summary>The configured tick period in milliseconds — what <see cref="TickP50Ms"/> and <see cref="TickP99Ms"/> are read against.</summary>
    public double TargetTickMs { get; init; }

    /// <summary>Median tick duration over the window, in milliseconds.</summary>
    public double TickP50Ms { get; init; }

    /// <summary>99th-percentile tick duration over the window, in milliseconds.</summary>
    public double TickP99Ms { get; init; }

    /// <summary>Ticks in the window whose duration exceeded the target. A count, not a rate — read it against <see cref="TicksInWindow"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Against <see cref="TargetTickMs"/>, the 1× base-rate target — not against the multiplier-adjusted budget a throttled tick is actually given.</b> That
    /// is the same display semantic the scheduler's <c>OverrunRatio</c> carries, and deliberately so (the #289 follow-up in
    /// <c>DagScheduler.Telemetry.cs</c>): the effective ratio exists to drive the overload detector, because a workload that fits comfortably at multiplier N
    /// still exceeds the 1× target and a detector reading it that way never deescalates. An operator's question is the other one — "is the engine holding the
    /// rate it was configured for" — and the answer to that is measured against the configured rate.
    /// </para>
    /// <para>
    /// The consequence to know: while <see cref="TickMultiplier"/> is above 1, ticks counted here include ones doing exactly what the overload manager told
    /// them to. A rising count with a rising multiplier is load being shed, not the engine falling behind; a rising count at multiplier 1 is the engine falling
    /// behind. The two readings need each other, which is why the multiplier is on this snapshot at all.
    /// </para>
    /// </remarks>
    public int Overruns { get; init; }

    /// <summary>
    /// The tick-rate multiplier the newest tick in the window ran under: 1 at the configured rate, 2 or more while the overload manager is modulating.
    /// </summary>
    /// <remarks>
    /// Present so <see cref="Overruns"/> can be read correctly — see its remarks. The newest tick's rather than the window's maximum, because it answers "what
    /// is the engine doing now"; a window that changed multiplier mid-way is visible as a mismatch between this and the overrun count, which is a truer signal
    /// than either a maximum or a mean would give.
    /// </remarks>
    public int TickMultiplier { get; init; }

    /// <summary>
    /// 99th-percentile per-tick durability wait over the window, in milliseconds: the Unit-of-Work flush, which in WAL mode is <c>RequestFlush</c> followed
    /// by <c>WaitForDurable</c>.
    /// </summary>
    /// <remarks>
    /// Read beside <see cref="TickP99Ms"/>, this is the "compute versus durability" split an operator needs: a tick at 12 ms of which 9 is this one is a
    /// disk problem, and the same 12 ms with 0.2 here is a compute problem. It is a per-TICK figure, not per-commit.
    /// </remarks>
    public double DurabilityWaitP99Ms { get; init; }

    /// <summary>Mean duration of each system over the window, in microseconds, in schedule order. Empty when the ring is empty.</summary>
    public SystemStat[] Systems { get; init; } = [];

    /// <summary>Live entity count per registered archetype, at the moment of the read.</summary>
    public ArchetypeStat[] Archetypes { get; init; } = [];

    /// <summary>
    /// Archetypes the application declared for replication. <b>Zero is how a reader knows the figures below are zero because nothing is replicated</b>, rather
    /// than because a replicating server happens to be idle.
    /// </summary>
    /// <remarks>
    /// A count rather than a <c>bool</c>, and deliberately not "is the subscriptions runtime alive": a subscriptions runtime is built on every
    /// <see cref="TyphonRuntime.Start"/> whether or not an application declared anything, so its existence answers "has this runtime started", which the
    /// caller already knows. What it wants to know is whether there is anything to replicate.
    /// </remarks>
    public int ReplicatedArchetypes { get; init; }

    /// <summary>Sessions currently open — CCU.</summary>
    public int Sessions { get; init; }

    /// <summary>Bytes the send pump has written since the process started. <b>Cumulative</b>: difference two reads to get a rate.</summary>
    public long NetOutBytesTotal { get; init; }

    /// <summary>99th-percentile cost of the replication track itself over the window, in milliseconds.</summary>
    public double ReplicationTrackP99Ms { get; init; }

    /// <summary>
    /// One row per <b>registered</b> realm, in registration order: what it is, what it is doing, and what its sessions have cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per registered realm, not per served one, and that distinction is the point.</b> A realm nobody is in has no replication state at all — the hub
    /// drops it — so its counters do not read zero, they do not exist. Reporting only the served realms would make "this realm cost nothing" indistinguishable
    /// from "this realm is not in the list", which is exactly the claim a realm world lives or dies on: an SWG galaxy registers a realm per enterable building
    /// and a thousand of them are empty at any moment. <see cref="RealmStat.Served"/> says which case a zero row is.
    /// </para>
    /// <para>Empty when the engine has one realm, where every figure above already describes it.</para>
    /// </remarks>
    public RealmStat[] Realms { get; init; } = [];

    /// <summary>
    /// Realms visited by a <b>serial</b> per-realm replication stage since the engine opened: each stage adds the number of realms
    /// <see cref="RealmStat.Served">served</see> when it ran. <b>Cumulative — difference two reads.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Realms D-7 asks whether the serial share of a tick grows with the realms being SERVED or with the realms merely registered. This counts the answer
    /// instead of inferring it: hold the served set at one realm, register two thousand more, and this number must not move. Nothing derivable from
    /// <see cref="Realms"/> can make that check — a stage that walked the registry would serve the same set while doing two thousand times the work.
    /// </para>
    /// <para>
    /// <b>The parallel per-chunk stages are not in it</b> (the index merge's and far fold's chunk dispatch, and the projection's per-chunk sort), because a
    /// counter on a path several workers run at once measures the counter. Their cost is read from the clock, in the D-7 bench.
    /// </para>
    /// <para>
    /// Divided by the ticks in the same interval, a steady state reads <c>(stages + 1/SweepEvery) × served</c> — the unplaced sweep is the one stage that
    /// runs one tick in <c>SweepEvery</c> rather than every tick. <b>How many stages is deliberately not promised here</b>: two of them are skipped on a tick
    /// with no bound push session, and one runs once per pushed archetype, so a caller that needs the constant measures it at one served count and holds later
    /// counts to it rather than writing a number down.
    /// </para>
    /// <para>
    /// <b>Zero means replication is off, not that the engine has one realm.</b> The hub serves realm 0 from construction, so a single-realm engine that
    /// replicates anything still counts it. That is the opposite of <see cref="Realms"/>, which IS empty for a single-realm engine.
    /// </para>
    /// </remarks>
    public long RealmPassSteps { get; init; }

    /// <summary>
    /// Realm-policy evaluations since the engine opened — one per tick that ran, each deciding every <b>registered</b> realm's run state and divisor.
    /// <b>Cumulative — difference two reads.</b>
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="RealmPassSteps"/> and the honest half of it: policy is <c>O(registered realms)</c> by design, not by accident, because a
    /// dormant realm is precisely one whose state has to be re-decided in case a session arrived. What must hold is that it runs <b>once per tick</b>: a
    /// second evaluation per dispatch, or per realm, would multiply the one term here that is allowed to scale with registration.
    /// </remarks>
    public long RealmPolicyEvaluations { get; init; }

    /// <summary>
    /// Milliseconds spent in the frame stage's <b>single-threaded</b> prologue since the engine opened, and the ticks it was measured over.
    /// <b>Cumulative — difference two readings.</b> Both zero unless phase timing is enabled, which it is not by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="ReplicationTrackP99Ms"/> and a different quantity: the track's percentile covers the whole stage, most of which is chunks on
    /// worker threads, while this is the part no second core can help with. The difference between N sessions in one realm and the same N sessions in N realms,
    /// divided by N, is the per-realm fixed cost of everything inside this window — which is what Realms D-7 was measured with.
    /// </para>
    /// <para>
    /// <b>It does not contain every serial per-realm step, and a D-7-style reading has to say so.</b> The frame prologue holds the overload step, the index
    /// build and world order, the far flushes, the LOD census and the unplaced sweep. Three more serial per-realm stages sit outside it, in other tracks'
    /// prepare phases: the index merge's plan, the index finish and far plan, and the queued shadow checks. So this is a lower bound on the serial per-realm
    /// cost, not the whole of it; the upper bound is the whole tick.
    /// </para>
    /// <para>
    /// <b>Cumulative on purpose, and the tick count comes with it.</b> The engine also keeps this as a mean over the whole run, which is the wrong shape for a
    /// measurement: a run whose configuration changed half way through reports one number for both halves. The ticks here are the ticks the frame stage was
    /// <i>busy</i>, not every tick the engine ran, so they are the right denominator and they are not derivable from <see cref="Tick"/>.
    /// </para>
    /// </remarks>
    public double ReplicationPrologueMsTotal { get; init; }

    /// <summary>Ticks the frame stage was busy, the denominator for <see cref="ReplicationPrologueMsTotal"/>. Zero unless phase timing is enabled.</summary>
    public long ReplicationPrologueTicks { get; init; }
}

/// <summary>
/// One realm: what it is, what it is doing, and the replication work its sessions have been served.
/// </summary>
/// <param name="Realm">The realm's id.</param>
/// <param name="Generation">
/// Which incarnation of that id this is. A realm's identity is the PAIR (12-realms § 1.1): ids are reused across an open, so a reader comparing by id alone
/// can take a reused id for the realm it replaced.
/// </param>
/// <param name="Kind">The realm kind its replication declared, which picks its sessions' profile variants; <c>""</c> for the default kind.</param>
/// <param name="State">Active, Simulated, Dormant or Closing, as the realm policy last evaluated it.</param>
/// <param name="Served">
/// Whether the realm has replication state at all. <b>False is not an error and is the usual case at scale</b>: a realm no session is in is dropped by the
/// hub, so every work figure below is structurally zero rather than measured zero.
/// </param>
/// <param name="Divisor">
/// Its simulation rate divisor this tick: 1 while it is observed, its <c>RealmConfig.UnobservedTickDivisor</c> otherwise. A realm at 4 has its clusters
/// dispatched once every fourth tick.
/// </param>
/// <param name="Sessions">Sessions in the realm at the moment of the read — a level, not a total.</param>
/// <param name="Enters">Entities delivered into its sessions' views, cumulative.</param>
/// <param name="Updates">Updates delivered to its sessions, cumulative.</param>
/// <param name="Leaves">Entities dropped from its sessions' views, cumulative.</param>
/// <param name="CellsDelivered">Replication cells its sessions were filled from, cumulative.</param>
/// <param name="Resets">Views re-sent whole to its sessions — a realm switch, a slot reuse, a gap, cumulative.</param>
/// <param name="Events">Events routed to its sessions, cumulative.</param>
[PublicAPI]
public readonly record struct RealmStat(
    ushort Realm,
    int Generation,
    string Kind,
    RealmRunState State,
    bool Served,
    int Divisor,
    int Sessions,
    long Enters,
    long Updates,
    long Leaves,
    long CellsDelivered,
    long Resets,
    long Events)
{
    /// <summary>
    /// Every work figure summed: zero means this realm has cost its sessions nothing at all since the process started.
    /// </summary>
    /// <remarks>
    /// One number to assert against, because the claim "an unobserved realm is free" is about all of them at once and a check that named five of six would be
    /// a check that passed when the sixth moved.
    /// </remarks>
    public long Work => Enters + Updates + Leaves + CellsDelivered + Resets + Events;
}

/// <summary>One system's mean cost over the window.</summary>
/// <param name="Name">The system's name, as the schedule declares it.</param>
/// <param name="MeanUs">Mean duration over the window, in microseconds.</param>
[PublicAPI]
public readonly record struct SystemStat(string Name, double MeanUs);

/// <summary>One archetype's live entity count.</summary>
/// <param name="Name">The archetype's name.</param>
/// <param name="Entities">Live entities at the moment of the read.</param>
[PublicAPI]
public readonly record struct ArchetypeStat(string Name, long Entities);
