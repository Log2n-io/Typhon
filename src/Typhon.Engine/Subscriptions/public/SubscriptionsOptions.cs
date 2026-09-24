using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Operator configuration for engine-owned replication. It governs how much memory replication state may occupy, not how clients connect — the transport
/// carries its own options.
/// </summary>
/// <remarks>
/// <para>
/// The blocks describe every live entity of an observed archetype (SUB-13), so what they cost follows the replicated population, and the frames follow
/// connections and observer radii, which are untrusted input. Left unbounded either is a memory-exhaustion path the application cannot close, which is
/// why the budgets are hard ceilings rather than hints.
/// </para>
/// <para>
/// <b>An unused subsystem allocates nothing.</b> No observed archetype means no blocks, so the budget costs a non-subscriber exactly zero.
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
    /// <b>Size it from a formula, not from this default.</b> A block covers a whole cluster, and every live cluster of an archetype some profile observes
    /// holds one (SUB-13), so the requirement is <c>96 B × N × (live clusters of the observed archetypes)</c>, where <c>N</c> is the archetype's cluster
    /// slot count. An operator sizes this from their replicated population, exactly as they size the page cache. The engine ships a
    /// rail, not an estimate of anyone's working set; the default matches the page cache's own default as the engine's scale for a significant pool, and is
    /// not derived from any application's workload.
    /// </para>
    /// <para>
    /// It is a ceiling, never a reservation. On exhaustion a cluster is given no block, and its entities are not replicated until one is free; the pool
    /// never allocates beyond the budget on the tick path and never throws there.
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
    /// fifths of the way here, then a close. A session this far behind is not going to catch up, and the memory it holds — frame slots and an ingress
    /// ring — is worth more to a client that can keep up. The close code tells the SDK to reconnect with backoff rather than to give up.
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
    /// a stuck session holds — at most two frames and a ring — none of which is worth being impatient over when the close costs its client
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
    /// An observer is a query per tick, so this is the rail that bounds a session's <i>query</i> cost. Four is the engine's rail — a near tier, a far tier and
    /// two spare (<c>design/Subscriptions/01-model.md § 9</c>) — not an estimate of what any application uses.
    /// </para>
    /// <para>
    /// It exists because <c>SessionLimits.MaxObservers</c> comes from an application's admission hook, which decides on untrusted input. Without an
    /// operator ceiling a hook could hand one connection a hundred thousand observers and bound the tick's cost by nothing at all; with it, the worst a hook
    /// can do is be generous inside a rail it does not control, which is the same contract every other per-session limit has.
    /// </para>
    /// </remarks>
    public int ObserversPerSession { get; init; } = 4;

    /// <summary>
    /// The replication grid's cell side, in world units. <b>Required</b> whenever a profile observes an archetype; no default, and the runtime refuses to
    /// start without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replication tracks what each session has been given cell by cell over this grid, which covers the spatial world's bounds
    /// (<see cref="SpatialGridConfig.WorldMin"/>, <see cref="SpatialGridConfig.WorldMax"/>). It is a separate value from the spatial
    /// <see cref="SpatialGridConfig.CellSize"/>: the spatial cells cluster entities for queries, these cells bound what a session is sent.
    /// </para>
    /// <para>
    /// <b>It is load-bearing for the whole subsystem's cost, which is why it is declared rather than derived.</b> For an observer of radius <c>R</c>, each
    /// session keeps a window of <c>W = 2⌈R / c⌉ + 5</c> cells per axis and reads that many cells every tick it moves, so a small side multiplies the
    /// per-session work by <c>(R / c)²</c>. A large side makes each cell coarser, so the band a moving session is sent when it enters a cell grows with it.
    /// The usual choice is about a third of the largest <see cref="ProfileBuilder.Sphere"/> radius: <c>R / c = 3</c> gives an 11 × 11 window. Profiles of
    /// different radii share this one grid, so it is sized for the set of them, not for any one.
    /// </para>
    /// <para>
    /// Refused at start: absent or not positive; a grid wider than 2²¹ cells on an axis; a window wider than 16 cells (<c>⌈R / c⌉ &gt; 5</c> for the largest
    /// radius), the limit of the current window storage. The resolved grid is logged when the runtime starts.
    /// </para>
    /// </remarks>
    public double ReplicationCellM { get; init; }

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
    /// Below this much replication work — <c>connected sessions × projected blocks</c> — the pipeline runs as one dispatched system with no internal
    /// barriers instead of as several. <b>Default: 0, which means never: the staged pipeline always runs.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default is zero because the crossing point is a measurement nobody has taken yet.</b> The staged pipeline's critical path is three sequential
    /// dispatches and a dispatch is a worker wake/barrier cycle — ≈ 0.1 ms measured, so ≈ 0.3 ms of scheduling before any replication work happens. Below
    /// some amount of work those barriers cost more than the parallelism they buy, and the collapsed shape pays one dispatch instead of three. WHERE that
    /// crossing point is depends on the worker count, the machine and the projection, and the only honest way to find it is to measure both shapes on the
    /// same binary (<c>archive/Subscriptions/09-phase1-build-plan.md</c>, Q-M1). A number shipped here before that measurement would be an engine default
    /// derived from nothing, so this ships as an opt-in and the collapsed path stays unreachable until an operator or a benchmark names a value.
    /// </para>
    /// <para>
    /// The unit is a product of two counts, not a time: it is the quantity the shape is chosen on, and it is deliberately coarse. The projected-block count
    /// is the previous tick's, because this tick's is not known until the blocks step — one of the stages being shaped — has run.
    /// </para>
    /// <para>
    /// Both shapes produce the same frames, byte for byte: the collapsed one runs the same stage bodies over the same chunk partition, serially, and a
    /// differential test asserts it. So this is a performance knob and never a behavioural one, and <c>int.MaxValue</c> — collapse whatever the load — is a
    /// legitimate setting for a small server, not an abuse of the option.
    /// </para>
    /// </remarks>
    public int CollapseBelowWorkUnits { get; init; }

    /// <summary>
    /// Whether the projection gives each chunk a fixed stride of the watched blocks instead of letting chunks claim them from a shared cursor. Off by default:
    /// with a fixed stride the stage waits for its latest worker's whole share. The price of claiming is reproducibility — which chunk projects a block, and
    /// so which identity lease names a new entity and where its bytes land, depends on timing. Set it where two runs must produce the same bytes.
    /// </summary>
    /// <remarks>
    /// Measured at d06/1 000, 32 workers, six interleaved pairs: projection 1.18 → 0.98 ms, tick P50 −0.34 ms (6/6); the stage's pool efficiency 64 % → 85 %.
    /// </remarks>
    public bool DeterministicProjection { get; init; }

    /// <summary>
    /// Tests only: the push path's shadow oracle — every published record checked for legality against a shadow of what the client holds. Also on
    /// with <c>TYPHON_PUSH_SHADOW=1</c>.
    /// </summary>
    internal bool PushShadow { get; init; }

    /// <summary>
    /// Tests only: serve a flat world with the deep implementation (10 § 3.5, L6), which must agree with the flat one on it — what the degeneracy tests
    /// run.
    /// </summary>
    internal bool ForceDeepReplicationForTest { get; init; }

    /// <summary>
    /// Whether a profile may declare <see cref="PushDetection.Automatic"/> (ADR-067). Off: replication is explicit — a system that writes a
    /// replicated value calls <see cref="SubscriptionsCommands.Replicate{TArchetype}(in ClusterRef{TArchetype}, int)"/> — and a profile asking for automatic
    /// detection is refused when the runtime starts.
    /// </summary>
    /// <remarks>
    /// Automatic detection encodes every live entity of the profile's archetypes every tick (+2.3 ms at d06/1 000). It stays behind this switch until it has
    /// been optimised and measured again, and is re-introduced as a supported option only if it earns its cost.
    /// </remarks>
    public bool AllowAutomaticPushDetection { get; init; }
}
