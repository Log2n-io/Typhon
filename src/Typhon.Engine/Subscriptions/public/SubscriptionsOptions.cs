using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Operator configuration for engine-owned replication. It governs how much memory replication state may occupy, not how clients connect — the transport
/// carries its own options.
/// </summary>
/// <remarks>
/// <para>
/// This is the one piece of engine memory sized by <i>client</i> behaviour rather than by the database: the watched set grows with connections, observer
/// radii and client-supplied regions, all of which are untrusted input. Left unbounded it is a memory-exhaustion path the application cannot close, which
/// is why the budget is a hard ceiling rather than a hint.
/// </para>
/// <para>
/// <b>An unused subsystem allocates nothing.</b> No sessions means no watched set means no blocks, so the budget costs a non-subscriber exactly zero.
/// Nothing is reserved up front either: the first allocation is a single slab, and the pool grows one slab at a time until the budget binds.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class SubscriptionsOptions
{
    /// <summary>
    /// Ceiling on native memory held by per-cluster replication state blocks. Default: 256 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Size it from a formula, not from this default.</b> A block covers a whole cluster, so the requirement is
    /// <c>96 B × N × (watched clusters)</c>, where <c>N</c> is the archetype's cluster slot count — it is driven by cluster <i>occupancy</i>, not by the
    /// watched entity count alone. An operator sizes this from their own expected concurrent view, exactly as they size the page cache. The engine ships a
    /// rail, not an estimate of anyone's working set; the default matches the page cache's own default as the engine's scale for a significant pool, and is
    /// not derived from any application's workload.
    /// </para>
    /// <para>
    /// It is a ceiling, never a reservation. On exhaustion the pool evicts idle blocks and then degrades sessions (dropping a rate class or shrinking the
    /// near radius); it never allocates beyond the budget on the tick path, never throws there, and never leaves an in-view entity unwatched.
    /// </para>
    /// <para>
    /// <b>Not yet implemented:</b> the design also calls for a warning when utilization crosses 80 % — the resource graph's default health threshold — so an
    /// operator learns the budget binds before sessions degrade. The pool exposes the ratio, but emitting the warning needs a logger it does not yet have,
    /// and lands with the DI wiring that reads this option. Until then the budget binds silently.
    /// </para>
    /// </remarks>
    public long StatePoolBudgetBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Capacity of one session's inbound command ring, in bytes. Must be a power of two and at least 64. Default: 4 KiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ring holds the commands a client sent since the last tick drained it. Because a full ring <i>drops</i> rather than blocks, this size is the depth
    /// at which a client that is both past its rate limit and unlucky in the tick's timing starts losing commands — not a throughput limit. Raising it buys
    /// tolerance for a late tick; it does not buy a client more commands per second, which the token bucket governs.
    /// </para>
    /// <para>
    /// 4 KiB is a rail, not a measurement. The command-rate data that would justify a different number does not exist until sessions do.
    /// </para>
    /// </remarks>
    public int IngressRingBytes { get; init; } = 4 * 1024;

    /// <summary>
    /// Ceiling on native memory held by session ingress rings. Default: 64 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rings are carved from slabs and the pool grows one slab at a time, so nothing is committed until the first session connects. When the ceiling binds,
    /// <b>admission fails</b>: the session is refused at connect time, where refusing is cheap and honest. The pool never blocks and never throws for
    /// exhaustion — a transport thread waiting on the tick is the failure this whole path exists to avoid.
    /// </para>
    /// <para>
    /// This is the other piece of engine memory sized by client behaviour, and it divides: <c>budget / <see cref="IngressRingBytes"/></c> is the hard cap on
    /// concurrent sessions, which makes it the honest place to set that cap. Like the state budget it is a rail chosen as a fraction of that pool, and is not
    /// derived from any application's session count.
    /// </para>
    /// </remarks>
    public long IngressPoolBudgetBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// The session table's size: how many clients may be connected at once. Default: 8 192.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hard maximum is 65 535 and that one is structural — a session identity packs a 16-bit slot and a 16-bit generation into 32 bits, so a slot number
    /// wider than a <c>ushort</c> does not exist. 8 192 is the table the engine allocates by default: a number an operator raises when they run a bigger
    /// server, not an estimate of how many clients anybody has. Admission past it is refused at connect time, which is where refusing is cheap and honest.
    /// </para>
    /// <para>
    /// It is also the default cap on pending resumes, at roughly 64 B each.
    /// </para>
    /// </remarks>
    public int MaxSessions { get; init; } = 8192;

    /// <summary>
    /// Ceiling on native memory held by encoded frames waiting to be sent. Default: 256 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ceiling, never a reservation: nothing is allocated until a session produces a frame, and the pool grows one size class at a time. On exhaustion the
    /// engine <b>skips and counts</b> — it never allocates past the budget on the tick path, never blocks and never throws. A skipped frame costs that
    /// session nothing permanent, because records are absolute and the next frame it does receive carries everything that changed.
    /// </para>
    /// <para>
    /// The default matches <see cref="StatePoolBudgetBytes"/> and the page cache's own default: it is the engine's scale for a significant pool, chosen so
    /// that a server which never tunes anything has a rail rather than no rail. Size it from frames in flight — at most two per session — times the frame
    /// ceiling that sessions actually reach, not from this number.
    /// </para>
    /// </remarks>
    public long FramePoolBudgetBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// How long a session may be stalled — denied a frame it had something to put in — before it is closed with "try again later". Default: 3 s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A skip is normal: a client whose link is momentarily busy misses a frame and converges on the next one. A <i>run</i> of skips is the signal that the
    /// session is being served faster than it can consume, and the answer is to serve it less rather than to queue more — first a dropped rate class, two
    /// fifths of the way here, then a close. A session this far behind is not going to catch up, and the memory it holds — a known-set, a frame slot, an
    /// ingress ring — is worth more to a client that can keep up. The close code tells the SDK to reconnect with backoff rather than to give up.
    /// </para>
    /// <para>
    /// <b>A duration, not a tick count, because what it bounds is a duration.</b> At most two frames are ever outstanding for a session, so a stalled client
    /// is at most two frames behind however long it stalls: the backlog does not grow, the client is simply stuck, and what the policy measures is how long.
    /// Expressed in ticks it silently meant something different at every tick rate — the same 50 was 5 s at 10 Hz and half a second at 100 Hz, so a fast
    /// server shed clients that had missed five frames.
    /// </para>
    /// <para>
    /// <b>Why three seconds.</b> It is four times the silence bound, which is <c>3 s / PingHz</c> and therefore 750 ms at the default ping rate, and the
    /// separation is the point: a client still sending <c>PING</c> is demonstrably alive and has earned more patience than one that has gone quiet, so
    /// the two policies must not close at nearly the same instant. The tick count this option replaced was 833 ms at 60 Hz — 83 ms apart from the
    /// silence bound, which left 1013 and 4001 carrying the same information. What bounds it from below is measured rather than assumed: with a world
    /// changing every tick and real sockets, the longest run of consecutive skips a HEALTHY session ever reached was ZERO, at 10, 50 and 110 sessions
    /// and at both 10 Hz and 60 Hz, so there is no natural stall for this to cut into. What bounds it from above is the quarantine below, and the memory
    /// a stuck session holds — at most two frames, a known-set and a ring — none of which is worth being impatient over when the close costs its client
    /// a full reconnect and a fresh baseline.
    /// </para>
    /// <para>
    /// It also sets how long a released network identity is quarantined before it can be reissued: the converted bound plus one tick, so no identity can be
    /// reused while a session that might still be holding it is alive.
    /// </para>
    /// <para>
    /// <b>It must be positive.</b> Zero or negative reads as "never close a stalled session", and the quarantine would then be sized from a window with no
    /// bound at all — a session could be skipped for a thousand ticks while an identity it still holds was reissued after two, which is the leave-then-enter
    /// collision SUB-06 exists to prevent. A runtime built with one refuses to start rather than replicating something subtly wrong. A positive value that
    /// converts to very few ticks is floored rather than refused, because a bound below the skip run a fully degraded session reaches on its own would close
    /// healthy sessions for having been degraded.
    /// </para>
    /// </remarks>
    public TimeSpan CloseStalledAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How often a connected client must exchange a keepalive, in hertz. Default: 4.
    /// </summary>
    /// <remarks>
    /// It is mandatory in both directions, and it is not only a liveness check: the acknowledgement it carries is how the server learns which tick a client
    /// has actually applied, which is what the lag-skip policy is computed from. Four per second gives the round-trip estimate something to work with inside a
    /// quarter of a second while costing eight tiny messages per session per second.
    /// </remarks>
    public int PingHz { get; init; } = 4;

    /// <summary>
    /// What the lag-skip bound allows for a round trip, in milliseconds, since the wire gives the server no way to measure one. Default: 250.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is <c>producedTick − ackedTick &gt; max(5, ⌈(this + PING period) / tick⌉ + 2)</c>. The design writes the first term as the measured round
    /// trip, but <c>PING.clientMs</c> is opaque to the server and echoed back unread, so it is the client that measures the round trip and no message carries
    /// the result back. Naming the allowance rather than pretending to a measurement is the honest form of that gap.
    /// </para>
    /// <para>
    /// 250 ms is chosen to cover an intercontinental round trip with margin, not from any one application's numbers: too small and a distant but healthy
    /// client is skipped for the distance alone; too large and a genuinely stalled client keeps its slot for seconds. Operators serving one region only should
    /// lower it.
    /// </para>
    /// </remarks>
    public int LagSkipRttAllowanceMs { get; init; } = 250;

    /// <summary>
    /// The most full enter records one session may receive in one frame. Default: 500.
    /// </summary>
    /// <remarks>
    /// <b>This, not <see cref="FrameBytes"/>, is what keeps frames small.</b> A session that has just connected, switched profile or turned around wants
    /// thousands of entities at once; sending them all would produce one enormous frame and then nothing. The budget spreads that fill over consecutive
    /// frames, nearest first, and nothing is lost — deferred records wait and rise in priority. 500 is an engine rail: it is the granularity at which a view
    /// fills smoothly at any tick rate, and it bounds a frame to a few tens of kilobytes for any plausible record size.
    /// </remarks>
    public int EnterBudgetPerFrame { get; init; } = 500;

    /// <summary>
    /// The most observers one session may hold at once. Default: 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An observer is a query per tick, so this is the rail that bounds a session's <i>query</i> cost the way
    /// <see cref="KnownEntitiesPerSession"/> bounds its memory. Four is the engine's rail — a near tier, a far tier and two spare
    /// (<c>design/Subscriptions/01-model.md § 9</c>) — not an estimate of what any application uses.
    /// </para>
    /// <para>
    /// It exists because <c>SessionLimits.MaxObservers</c> comes from an application's admission hook, which decides on untrusted input. Without an
    /// operator ceiling a hook could hand one connection a hundred thousand observers and bound the tick's cost by nothing at all; with it, the worst a hook
    /// can do is be generous inside a rail it does not control, which is the same contract every other per-session limit has.
    /// </para>
    /// </remarks>
    public int ObserversPerSession { get; init; } = 4;

    /// <summary>
    /// The largest known-set one session may hold, in entities. Default: 65 536.
    /// </summary>
    /// <remarks>
    /// The known-set is what makes "do I know this entity, and has it changed since my baseline?" one probe, at roughly 23 B per known entity — so this
    /// number is a memory bound per session, about 1.5 MiB at the default. It is the largest size class the engine sizes its hash for, not a guess at how
    /// much any application shows a client; a session whose interest would exceed it is degraded rather than allowed to grow without limit.
    /// </remarks>
    public int KnownEntitiesPerSession { get; init; } = 65536;

    /// <summary>
    /// The largest frame the engine will send, in bytes. Default: 256 KiB.
    /// </summary>
    /// <remarks>
    /// A ceiling that catches a runaway frame, not a target — <see cref="EnterBudgetPerFrame"/> is what keeps frames small in normal operation. It is
    /// exported in the catalog so an SDK can size its receive buffer once and never reallocate, which is why it is a rail rather than a suggestion: a client
    /// that has sized its buffer from it must never be handed more.
    /// </remarks>
    public int FrameBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// The largest message a client may send, in bytes. Default: 1 KiB.
    /// </summary>
    /// <remarks>
    /// Commands are intents — a direction, a target, a short line of text — so a kilobyte is generous for a batch of them, and an over-cap message is
    /// refused by its length before a byte of it is decoded. It is exported in the catalog so the SDK batches under it rather than discovering the limit by
    /// being disconnected. The first message is the exception and has its own, larger protocol constant, because an authentication token has to fit in it.
    /// </remarks>
    public int ClientMessageBytes { get; init; } = 1024;

    /// <summary>
    /// How many ticks a replication state block may go unwatched before it is freed. Default: 50.
    /// </summary>
    /// <remarks>
    /// A block covers a whole cluster, so a session looking away for a moment should not cost the rebuild of everything it was watching — and a cluster that
    /// nobody has looked at for a while should not hold memory in case somebody does. The delay is what separates the two: long enough that a camera sweeping
    /// back and forth re-uses its blocks, short enough that a view moved on releases them in under a second at any normal tick rate.
    /// </remarks>
    public int IdleBlockTicks { get; init; } = 50;

    /// <summary>
    /// Below this much replication work — <c>connected sessions × watched blocks</c> — the pipeline runs as one dispatched system with no internal barriers
    /// instead of as four. <b>Default: 0, which means never: the staged pipeline always runs.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default is zero because the crossing point is a measurement nobody has taken yet.</b> The staged pipeline's critical path is three sequential
    /// dispatches and a dispatch is a worker wake/barrier cycle — ≈ 0.1 ms measured, so ≈ 0.3 ms of scheduling before any replication work happens. Below
    /// some amount of work those barriers cost more than the parallelism they buy, and the collapsed shape pays one dispatch instead of three. WHERE that
    /// crossing point is depends on the worker count, the machine and the projection, and the only honest way to find it is to measure both shapes on the
    /// same binary (<c>design/Subscriptions/09-phase1-build-plan.md</c>, Q-M1). A number shipped here before that measurement would be an engine default
    /// derived from nothing, so this ships as an opt-in and the collapsed path stays unreachable until an operator or a benchmark names a value.
    /// </para>
    /// <para>
    /// The unit is a product of two counts, not a time: it is the quantity the shape is chosen on, and it is deliberately coarse. The watched-block count is
    /// the previous tick's, because this tick's is not known until the interest pass — one of the stages being shaped — has run.
    /// </para>
    /// <para>
    /// Both shapes produce the same frames, byte for byte: the collapsed one runs the same stage bodies over the same chunk partition, serially, and a
    /// differential test asserts it. So this is a performance knob and never a behavioural one, and <c>int.MaxValue</c> — collapse whatever the load — is a
    /// legitimate setting for a small server, not an abuse of the option.
    /// </para>
    /// </remarks>
    public int CollapseBelowWorkUnits { get; init; }

    /// <summary>
    /// Whether interest is expressed as a DIFFERENCE against what each session reached on the previous tick, rather than re-derived whole every tick
    /// (15 § 3.2). On by default: it is the algorithm, and the switch exists so that it can be measured against the shape it replaced on one binary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the temporal half of the interest design.</b> Each session keeps a
    /// snapshot of the clusters and slots its interest reached last tick, and the identity it found in each. This tick's runs are then split three ways: a
    /// slot new to the session is classified in full, a slot it already held is classified only when S1 named it in the block's change mask, and a slot it
    /// held and no longer reaches becomes a leave named by the snapshot. At the density 13 § 6 measures — 2 056 hits per session per tick producing ~293
    /// records — the middle case is the overwhelming majority, and it costs an array copy instead of two random cache misses.
    /// </para>
    /// <para>
    /// <b>Why this is sound where the change mask alone is not.</b> C-1 masks before probing and therefore cannot tell a slot that is unchanged from a slot
    /// that is unchanged AND unknown to this session; the second produces no enter and the entity is lost permanently. The difference knows which slots are
    /// new because it has last tick's set, so it never skips one. The copy of an identity is in turn justified by an invariant of S1 rather than by an
    /// assumption about the world: <c>ProjectionPass</c> re-initializes any slot whose entity differs from the one its entry describes, and an initialized
    /// slot joins <c>ChangedSlots</c> unconditionally, so an unmasked retained slot holds the entity it held last tick.
    /// </para>
    /// <para>
    /// <b>It also retires the leave sweep for the sessions that take it.</b> Leaves come from the slots the snapshot held and this tick does not, plus the
    /// slots whose occupant was replaced, both filtered against the identities the tick actually read so that an entity which merely moved between slots
    /// owes none. That replaces a walk of the whole known-set — 14 § 5.2, the worst-scaling function measured — with work proportional to what moved.
    /// </para>
    /// <para>
    /// A session that is behind, still filling, resetting, or owed anything by the frame before takes the full walk, and taking it is what rebuilds the
    /// snapshot. Turning this off leaves that walk as the only path, which is Phase 1's behaviour exactly.
    /// </para>
    /// <para>
    /// <b>It is off by default because the measurement says so, and the measurement is the interesting part</b> (15 § 10). At d05 with 200 sessions it
    /// engages on 79 % of frames and carries <b>89.5 %</b> of hit slots — 60.7 million of 67.8 million skip their block read and their known-set probe
    /// entirely — and the frame stage costs the same to within the noise: −0.7 % on <c>frames</c>, +3.5 % on the <c>subs</c> track, three of four
    /// interleaved pairs favouring the walk. Removing nine tenths of the per-hit work changed nothing, which says the per-hit work was not the cost. What
    /// remains is the ITERATION of the session's whole hit list, plus the sort and encode that are proportional to records rather than to hits. Making each
    /// hit cheaper cannot help while the list is still walked per session per tick; the list itself has to stop being built, which is what a dirty-driven
    /// interest pass would do (15 § 3.2) and what this is NOT.
    /// </para>
    /// </remarks>
    public bool IncrementalInterest { get; init; } = true;

    /// <summary>
    /// Whether a frame that could not say everything owes its next frame the SLOTS it left out, rather than a walk of the session's whole view.
    /// Default: on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things make a frame incomplete: an ENTER the per-frame budget deferred, and a slot whose occupant was replaced (which no change mask can show,
    /// because the bit stays set). Either way the interest view is about to claim the slot, so the difference would never offer it again and the client
    /// would never be told about an entity the engine had decided to describe. Something must carry the debt.
    /// </para>
    /// <para>
    /// <b>What it costs to carry it as the whole frame.</b> Measured at d06 with 200 sessions: the blunt form forced a full walk on <b>40.8 %</b> of
    /// frames, those frames performed roughly <b>91 %</b> of every slot read in the subsystem, and 13 679 of 13 880 full gathers came from this one
    /// condition — not from slot reuse, and not from a lagging session, which accounted for none. Carrying the slots instead takes the full-gather rate to
    /// <b>12.1 %</b> and slot reads down <b>61 %</b>, with no overlap between the arms on either count.
    /// </para>
    /// <para>
    /// Turning it off restores the blunt <c>ForceFullGather</c> exactly, which is what makes the two an A/B on one binary. The blunt form is still what
    /// runs wherever there is no interest view to record the debt into.
    /// </para>
    /// </remarks>
    public bool OwedSlotCarry { get; init; } = true;

    /// <summary>
    /// How much of a session's outstanding slot debt one frame may serve, as a multiple of <see cref="EnterBudgetPerFrame"/>. <c>0</c> serves all of it.
    /// Default: 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything owed is an ENTER waiting to be described, and a frame can describe at most <see cref="EnterBudgetPerFrame"/> of them. When a view is
    /// larger than the budget can fill — a 31 147-entity disc against a budget of 500 — the backlog never drains, and reading all of it on every frame
    /// spends thousands of slot reads to choose five hundred. Measured at d07 with 200 sessions: <b>428 of the 513 million slots the gather read were
    /// owed</b>, 83.6 % of the frame stage, to send 500 per frame. Bounding it took the gather's visited set from 512 M to 165 M.
    /// </para>
    /// <para>
    /// <b>Nothing is lost by bounding it.</b> A slot the slice does not reach keeps its bit and the next frame takes it — the debt is a mask, not a flag.
    /// What DOES change is when an entity is described: a session whose view outruns its budget learns about it over more frames, in a different order.
    /// Raising <see cref="EnterBudgetPerFrame"/> is the direct answer to that; this only stops the engine spending unboundedly on a backlog it cannot
    /// send.
    /// </para>
    /// <para>
    /// The slice is applied PER RUN rather than per frame, because runs are walked in a fixed order and a per-frame cap would serve the first clusters
    /// every tick and starve the last ones forever.
    /// </para>
    /// </remarks>
    public int OwedSliceMultiplier { get; init; } = 2;

    /// <summary>
    /// Whether each cluster's changed records are encoded ONCE per tick and referenced by every session that watches it, rather than re-encoded per
    /// session. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it removes is the per-session multiplier, not the encode</b> (17 § 16). A state record's bytes depend on the entity and the tick, never on
    /// who is watching, so the records an encode-once design produces is the count of slots the projection names as changed — a property of the WORLD.
    /// Measured at d06 that count is flat in the session count while the records the frame stage emits are linear in it: 41× at 200 sessions and 83× at
    /// 1 000.
    /// </para>
    /// <para>
    /// <b>It needs the wire to admit sub-runs</b>, which it does (03 § 5): a netId gap is relative to the record before it, so a sub-list that was one
    /// ascending sequence could never be assembled from bytes encoded elsewhere. Each run restarts the delta.
    /// </para>
    /// <para>
    /// <b>A session falls back to encoding a cluster itself whenever it cannot prove it may share</b> — its baseline is not the previous tick, it has not
    /// been told about every slot the run describes, it is owed something there, or an identity in the cluster has moved since it last read it. The
    /// fallback is per cluster and not per frame, so one arriving entity costs one cluster rather than the session's whole view.
    /// </para>
    /// <para>
    /// <b>It pairs with cluster-granular interest.</b> A shared run describes every changed slot of a cluster, so a session that reaches only part of one
    /// can never use it — the "told about every slot" test refuses it, correctly and at no risk, but also at no gain. With
    /// the share rate is near zero by construction.
    /// </para>
    /// </remarks>
    public bool SharedClusterBlocks { get; init; }

    /// <summary>
    /// Whether a referenced cluster run is checked against the session's known-set, slot by slot, before it is taken. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <b>It is the work <see cref="SharedClusterBlocks"/> exists to skip, so it is a test harness and not an option to run with.</b> Referencing a run
    /// without walking the cluster rests on an invariant spanning three structures — the session's per-slot identities, its known-set and the block's
    /// record of entities that arrived from elsewhere — and a hole in it does not fail loudly: one client is handed a <c>STATE</c> for an entity it has
    /// never heard of, and diverges silently from there. This turns that into a throw, so the differential oracle proves the invariant rather than
    /// sampling its consequences.
    /// </remarks>
    public bool VerifySharedRuns { get; init; }

    /// <summary>
    /// Whether the interest stage times its own phases: the shared broad query, the per-entity narrow filter, and the run assembly. Default
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <b>It answers whether caching a session's CLUSTER set could pay.</b> A maintained cluster list removes the broad phase, the directory probes and
    /// the run assembly; it does not remove the narrow phase, because knowing a cluster is in range says nothing about which of its slots are. A density
    /// ladder cannot separate the two — cluster count inside a disc scales with density exactly as entity count does — so the split has to be timed.
    /// Three timestamp pairs per session per archetype is real cost on a stage measured in single-digit milliseconds, hence a measurement instrument and
    /// not a setting to run with.
    /// </remarks>
    public bool MeasureInterestPhases { get; init; }

    /// <summary>
    /// Whether a session that is up to date stops re-stating membership that did not change. Default off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, every session re-emits and re-marks every cluster it holds on every tick, and the frame stage walks all of them to find the few that
    /// changed — measured at a thousand sessions, 81 % of the runs walked carried nothing. With it, a session whose frame will be incremental emits runs
    /// only where its membership moved — the clusters it holds are still marked watched, so projection is unchanged — and content changes reach it from
    /// projection's per-tick changed-block table, computed once per block rather than rediscovered by every session.
    /// </para>
    /// <para>
    /// A session that is behind, reset or owed a full read keeps the old path whole, because a full gather needs every run.
    /// </para>
    /// </remarks>
    public bool SparseTopology { get; init; }

    /// <summary>
    /// Whether projection re-encodes only the watched slots the engine marked changed, instead of every watched slot. Default on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turns on the replicated archetypes' content-change marks and the per-tick changed-cluster list, which the projection pass's gate reads (design 21,
    /// slice 3 and § 7.2.1). The byte comparison still decides what is sent; the marks only narrow which slots are compared, so the wire is unchanged.
    /// </para>
    /// <para>
    /// <b>On by default because the trade is lopsided, measured both ways at d06/1 000.</b> Where an app writes what changes, projection fell 41 %. Where
    /// every replicated component is claimed every tick — the gate's worst case — what remains is the fence's list publish, about 1 % of the tick, and the
    /// gate still paid it back. The marks themselves are one bit-OR per span handout and per spatial write. Turn it off only for an app that genuinely
    /// rewrites every projected component every tick and cannot spare that 1 %.
    /// </para>
    /// </remarks>
    public bool GateProjectionOnChanges { get; init; } = true;

    /// <summary>
    /// The most sessions one interest cell group may hold before a crowded cell is cut into pieces. Zero (the default) is the automatic cap: sessions
    /// divided by workers, at least eight.
    /// </summary>
    /// <remarks>
    /// A group is resolved by one worker, so the largest group is a floor on the stage's wall time; every extra piece pays the cell's broad phase again.
    /// </remarks>
    public int InterestGroupCap { get; init; }

    /// <summary>
    /// Whether the frame stage takes its sessions from a shared cursor rather than a fixed slice each. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stage's wall time is its slowest chunk.</b> Splitting the tick's sessions into equal COUNTS assumes they cost the same, and they do not: a
    /// session working through an enter backlog describes up to <see cref="EnterBudgetPerFrame"/> entities where a settled one describes a handful, and
    /// which sessions end up together is decided by the interest-cell sort, which balances nothing. Measured at d06 with 1 000 sessions, the static split
    /// delivered 59 ms of CPU in 17 ms of wall — an effective 3.5 workers out of 32.
    /// </para>
    /// <para>
    /// <b>The mechanism is one <c>Interlocked.Increment</c> per session</b>, which needs no estimate of what a session will cost — the
    /// thing no static heuristic can get right, because the cost depends on a backlog that changes every tick. Its cost when off is one predicted branch
    /// per chunk.
    /// </para>
    /// <para>
    /// <b>What it gives up</b> is the affinity between a worker and the sessions it served last tick, which matters only to
    /// <c>FrameWorkerScratch.Shared</c> — the per-worker cache that lets two sessions with identical frames copy rather than re-encode. That cache applies
    /// to <c>ObserverKind.World</c> observers only, so a spatial profile loses nothing by it.
    /// </para>
    /// </remarks>
    public bool DynamicFrameScheduling { get; init; } = true;

    /// <summary>
    /// Whether the gather issues hardware prefetches for the lines it is about to read. Default <see langword="false"/>, and the default is a
    /// measurement rather than a preference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It changes no output, only when the lines arrive.</b> The gather reads four things per interest run whose addresses form a dependent chain —
    /// the block header, the view's debt word, the slot's hot entry, and the known-set probe, whose address only the hot entry supplies. On paper nothing
    /// in that chain overlaps anything else. This issues the next run's independent lines a run early and the next slot's probe line a slot early.
    /// </para>
    /// <para>
    /// <b>It is off by default because it was measured and it did nothing.</b> Six interleaved pairs at d06 with 200 sessions, on one binary, gave
    /// 381/455/392 ns of gather per run with it off against 381/426/389 with it on — no separation. The premise was wrong: only about <b>464 replication
    /// blocks are projected per tick</b> at that point and roughly eighty sessions read each one, so the headers and hot entries are cache-resident and
    /// there is no latency to hide.
    /// </para>
    /// <para>
    /// <b>It is kept because the regime it is built for is real and this workload is not it</b> — a world wide enough that each block is read by one
    /// session rather than eighty makes every one of those lines a genuine miss. Nothing here derives an engine default from one application's shape, so
    /// the switch stays and the default states what was measured. Its cost when off is one predicted branch per run on a value already in a register,
    /// and on a platform with no prefetch instruction the tests fold at JIT time and the code disappears entirely.
    /// </para>
    /// </remarks>
    public bool GatherPrefetch { get; init; }

    /// <summary>
    /// Whether sessions sharing an interest cell resolve their observers from one query instead of one each. Default: on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The broad phase is keyed by SPACE, not by session.</b> Sphere observers are grouped by the cell their viewpoint falls in; a cell holding more than
    /// one session runs a single query, enlarged by half a cell diagonal so that it covers every viewpoint the cell can hold, and each member then keeps the
    /// candidates within its own radius using the engine's own narrowphase arithmetic. Membership is identical to a per-session query — the enlargement buys
    /// the sharing and is removed again before any run is recorded.
    /// </para>
    /// <para>
    /// <b>It cannot lose.</b> A cell holding one session takes the direct path, so nothing is enlarged for a lone observer; a cell holding two already costs
    /// less than resolving them separately, because the enlarged query is 1.53x the area of one disc. The switch exists so the two shapes can be measured on
    /// one binary, which is what this repository's A/B rule requires, not because there is a workload that should turn it off.
    /// </para>
    /// <para>
    /// Non-sphere observers, and sessions that have not been placed, are unaffected: they have no cell and take the direct path.
    /// </para>
    /// </remarks>
    public bool CellKeyedInterest { get; init; } = true;
}
