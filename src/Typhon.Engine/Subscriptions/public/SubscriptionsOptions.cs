using JetBrains.Annotations;

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
    /// Consecutive skipped frames after which a session is degraded — dropped a rate class, or given a smaller near radius. Default: 20.
    /// </summary>
    /// <remarks>
    /// A skip is normal: a client whose link is momentarily busy misses a frame and converges on the next one. A <i>run</i> of skips is the signal that the
    /// session is being served faster than it can consume, and the answer is to serve it less rather than to queue more. 20 is a fifth of the way to
    /// <see cref="CloseAfterSkips"/>, so degradation has four more chances to work before the session is closed — the ratio is what matters here, not the
    /// absolute number of ticks, which differs with every tick rate.
    /// </remarks>
    public int DegradeAfterSkips { get; init; } = 20;

    /// <summary>
    /// Consecutive skipped frames after which a session is closed with "try again later". Default: 50.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session this far behind is not going to catch up, and the memory it holds — a known-set, a frame slot, an ingress ring — is worth more to a client
    /// that can keep up. Closing is the honest outcome: the close code tells the SDK to reconnect with backoff rather than to give up.
    /// </para>
    /// <para>
    /// It also sets how long a released network identity is quarantined before it can be reissued: this many ticks plus one, so no identity can be reused
    /// while a session that might still be holding it is alive.
    /// </para>
    /// <para>
    /// <b>It must be at least one.</b> Zero would read as "never close a lagging session", and the quarantine would then be sized from a skip window with no
    /// bound at all — a session could be skipped for a thousand ticks while an identity it still holds was reissued after two, which is the leave-then-enter
    /// collision SUB-06 exists to prevent. A runtime built with zero refuses to start rather than replicating something subtly wrong.
    /// </para>
    /// </remarks>
    public int CloseAfterSkips { get; init; } = 50;

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
}
