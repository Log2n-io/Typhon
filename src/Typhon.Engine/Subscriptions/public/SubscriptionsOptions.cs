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
    /// Ceiling on native memory held by one observed archetype's per-cluster replication state blocks. Default: 256 MiB per archetype.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per archetype.</b> Each observed archetype has its own pool, and each pool honours this ceiling on its own; the total is this times the number of
    /// observed archetypes.
    /// </para>
    /// <para>
    /// <b>Size it from a formula, not from this default.</b> A block covers a whole cluster, and every live cluster of an archetype some profile observes
    /// holds one (SUB-13), so an archetype needs about <c>96 B × N × (its live clusters)</c>, where <c>N</c> is its cluster slot count; the exhaustion
    /// warning prints the exact block size. An operator sizes this from their replicated population, exactly as they size the page cache. The engine ships a
    /// rail, not an estimate of anyone's working set; the default matches the page cache's own default as the engine's scale for a significant pool, and is
    /// not derived from any application's workload.
    /// </para>
    /// <para>
    /// <b>A ceiling, never a reservation.</b> On exhaustion a pushed cluster is given no block and is DEFERRED (SUB-32): its entities are not replicated
    /// until a block frees, and then are, with nothing lost in between. The pool never allocates beyond the budget on the tick path and never throws there.
    /// Every refusal is counted: the pool's <c>RentRefusals</c> in the resource graph's metrics, and a warning is logged when an archetype's pool starts
    /// refusing and an information line when it stops, never once per tick.
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
    /// How long a link-dead session may be resumed, in milliseconds; <c>0</c> issues no resume tokens at all. Default: 60 000.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only switch resume has, and zero is a real configuration rather than a disabled feature.</b> It is exported as the catalog's
    /// <c>limits.resumeGraceMs</c>, which is how a client chooses between presenting its token and connecting fresh, so an operator who sets zero has told
    /// every SDK not to bother: <c>WELCOME</c> then carries the all-zero token the wire reserves for "none", nothing is minted, and neither the token array
    /// nor the record table is committed.
    /// </para>
    /// <para>
    /// The number is a trade an application makes, not a technical limit. A grace is a promise to one client — its avatar, its claim, whatever the application
    /// keyed to the session — held at the expense of every other client, who cannot have that record and may see the player standing there. 60 s is what
    /// 01-model § 3 picked and what a mobile client losing a tower needs; a competitive server wants seconds.
    /// </para>
    /// </remarks>
    public int ResumeGraceMs { get; init; } = 60_000;

    /// <summary>
    /// How many sessions may have a resume pending at once; <c>0</c> takes <see cref="MaxSessions"/>. Default: 0.
    /// </summary>
    /// <remarks>
    /// Past the cap the oldest pending resume expires early, exactly as it would at its deadline — the application gets its <c>ResumeExpired</c> and lets go.
    /// <b>The cap bounds promises, not memory</b>: the record table is slot-indexed and sized by <see cref="MaxSessions"/> whenever resume is on, because a
    /// presented token names its record directly and a smaller table would buy a hash lookup on the ingress path with the memory it saved.
    /// </remarks>
    public int MaxPendingResumes { get; init; }

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
    /// The most bytes of reliable events (<see cref="EventBuilder{T}.Reliable"/>) the engine keeps: what every live session may still be owed, held for
    /// <see cref="CloseStalledAfter"/>. Required — refused at <c>Start</c> when 0 — once a reliable event is declared; unused otherwise.
    /// </summary>
    /// <remarks>
    /// An emit that would take the kept bytes past it is refused and counted, never dropped later: the guarantee is to every session alive when the event is
    /// emitted, so the emitter is the one who must learn it did not go. Each emit reserves its event's largest encoding until the tick encodes it. Size it
    /// as the reliable bytes a burst emits per tick times the ticks in <see cref="CloseStalledAfter"/>.
    /// </remarks>
    public long ReliableEventBytes { get; init; }

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
    /// A session that has just connected, switched profile or turned around wants thousands of entities at once; sending them all would produce one
    /// enormous frame and then nothing. The budget spreads that fill over consecutive frames, nearest first, and nothing is lost — deferred records wait and
    /// rise in priority. 500 is an engine rail: it is the granularity at which a view fills smoothly at any tick rate.
    /// <para>
    /// <b>A count does not bound bytes</b> — a record with wide sections can reach half a frame — so the fill is paced by bytes too, at half of
    /// <see cref="FrameBytes"/>, and what one frame cannot carry is owed to the next ones (design/Subscriptions/15, SUB-36). It is also the owed set's
    /// scale: past four budgets of owed records, a session is refilled by a <c>RESET</c> instead.
    /// </para>
    /// </remarks>
    public int EnterBudgetPerFrame { get; init; } = 500;

    /// <summary>
    /// The most groups one session holds in each group set at once (14 § 3, P2): the application's declared worst case. Required when a group set is
    /// declared — 0, the default, refuses to start then; at most 1 048 576.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bound, not a reservation (#1407): a session's group state costs the keys it holds — about 12 B a slot, committed and pending each, at most
    /// four slots a key — and its per-frame group work grows with them, whatever this says. It bounds the keys admitted: one the session no longer
    /// meets, kept held while a frame's releases are spent, can keep it above the bound for a few frames.
    /// </para>
    /// <para>
    /// A region the session meets past this many is not held: the kept ones and then the nearest are, and the session is told how many it left out
    /// (<c>GROUP_TRUNCATED</c>). It is also <c>SubscribeRequest</c>'s burst, clamped to 5–1 024, so a client subscribing up to 1 024 keys at once is taken
    /// in one tick.
    /// </para>
    /// </remarks>
    public int MaxGroupsPerSession { get; init; }

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
    /// The frame budget's limit (design/Subscriptions/15, SUB-36): a frame carries the records that fit it and owes the rest to the next frames, so a burst
    /// is spread rather than refused; a <see cref="SessionLimits.FrameBytes"/> below it lowers one session's. It is exported in the catalog so an SDK can
    /// size its receive buffer once and never reallocate, which is why it is a rail rather than a suggestion: a client that has sized its buffer from it
    /// must never be handed more. Refused at <c>Start</c>: an archetype whose largest enter record exceeds half of it.
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
    /// Each session's inbound budget, bytes per second, across every message it sends once open. <b>Required when the catalog has commands</b> — the
    /// application's own or the built-in <c>ClientRegion</c> — and at least <see cref="ClientMessageBytes"/>: <c>Start</c> refuses either. No default: it is
    /// the one rail between a single client and the tick, and what it should be depends on the application's commands, not on the engine.
    /// </summary>
    /// <remarks>
    /// A token bucket per connection, one second deep. Every message is charged; only a <c>COMMANDS</c> message is refused — whole, each of its commands
    /// answered with a <c>RATE_LIMITED</c> <c>ACK</c> so the client's <c>lastSeq</c> still settles them. It is still decoded to find them, so the budget
    /// bounds what reaches the tick and the abuse rule (<see cref="AbuseRefusalsPerWindow"/>) bounds the decoding. A <c>PING</c>, a <c>BYE</c> and a
    /// protocol error are never delayed (design/Subscriptions/11 § 4.2).
    /// </remarks>
    public int IngressBytesPerSecond { get; init; }

    /// <summary>The window sustained abuse is measured in. Default: one second.</summary>
    public TimeSpan AbuseWindow { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Refused commands in one <see cref="AbuseWindow"/> — over the inbound budget, over a command's rate, sent by a role that may not — past which the
    /// window counts as abusive. Default: 64.
    /// </summary>
    public int AbuseRefusalsPerWindow { get; init; } = 64;

    /// <summary>
    /// Consecutive abusive windows after which the session is closed with 1008 (policy violation) rather than refused forever. Default: 3. A client over
    /// its limits for a moment is refused and recovers; one that keeps sending what is refused is closed, and its SDK reconnects with backoff.
    /// </summary>
    public int AbuseWindows { get; init; } = 3;

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
    /// Tests and measurements only: the visibility slack <c>h</c>, in metres, of every moving observed archetype, in place of the rule (09 § 2, Q1: the
    /// smallest <c>R / 48</c> over the Sphere profiles observing it). <see cref="double.NaN"/> applies the rule; 0 is exact.
    /// </summary>
    internal double VisibilitySlackMForTest { get; init; } = double.NaN;

    /// <summary>
    /// Tests only: every published frame's <c>ENTITIES</c> blocks are compared with the frame budget's meter (15 § 4.1), and a block longer than its
    /// metered bound is counted in <c>FrameAssembler.MeterViolations</c> — the meter is an upper bound, or frames can exceed their limit.
    /// </summary>
    internal bool CheckFrameMeterForTest { get; init; }

    /// <summary>
    /// Tests only: the frame budget (15) is off — no fill allowance, no fit — so frames are bounded by the enter count alone, as before #1408. What a
    /// SUB-36 mutant runs, to show the budget's tests fail without it.
    /// </summary>
    internal bool FrameBudgetOffForTest { get; init; }

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
