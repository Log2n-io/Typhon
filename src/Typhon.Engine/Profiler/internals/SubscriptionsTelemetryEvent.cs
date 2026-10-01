// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Server-wide push-replication figures, one record per stats emission. Instant-shaped. See <see cref="TraceEventKind.SubscriptionsServerTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A transport, not a measurement</b> — the same argument <see cref="SpatialArchetypeTelemetryEvent"/> makes. Every value here is one the engine already
/// computes for the <c>STATS</c> wire block that goes to <i>game clients</i> (<c>Subscriptions/internals/StatsEncoder.cs</c>). An operator wants exactly those
/// numbers and has no way to get them: a Workbench attach session is a one-way trace stream with no channel back to the engine, and the numbers that do exist
/// are addressed to the wrong audience.
/// </para>
/// <para>
/// <b>Emitted at the stats cadence, not per tick.</b> Every rate here is already per-second, computed against the elapsed ticks since the last emission;
/// re-emitting per tick would divide by one tick and report noise. It also keeps the tick path clear, which matters because this subsystem's counters are read
/// on it.
/// </para>
/// <para>
/// <b>Independent of the application's metric catalog, deliberately.</b> <c>StatsEncoder.BeginTick</c> returns early when the app declared no metrics, because
/// with an empty catalog there is no client-facing block to encode. The operator's telemetry must not be silenceable that way — an app that declares nothing
/// is exactly an app whose replication nobody has looked at — so this emission is driven from <c>SubscriptionsContext.Reset</c> on its own cadence check
/// rather than from inside the encoder. <c>Reset</c> and not the frames track, because the track is not dispatched when no session is served and "no sessions"
/// is the reading an operator most needs; see <c>FrameAssembler.EmitOperatorTelemetry</c> for what that placement obliges.
/// </para>
/// </remarks>
[TraceEvent(TraceEventKind.SubscriptionsServerTelemetry, Shape = TraceEventShape.Instant, Gate = "SubscriptionsServerTelemetryActive")]
internal ref partial struct SubscriptionsServerTelemetryEvent
{
    /// <summary>Sessions open at this emission — the denominator every per-session figure is read against.</summary>
    [BeginParam] public int Sessions;
    /// <summary>Outbound replication bytes per second across all sessions, over the window since the last emission.</summary>
    [BeginParam] public float NetOutBytesPerSec;
    /// <summary>p99 of the replication track's own duration, in ms. The tick cost of replicating, as distinct from the tick's.</summary>
    [BeginParam] public float TrackP99Ms;
    /// <summary>
    /// p99 of the durability wait, in ms. Beside the replication figures on purpose: a replication track that looks slow is often a disk that is slow, and
    /// the two numbers side by side are what tell those apart.
    /// </summary>
    [BeginParam] public float DurabilityWaitP99Ms;
    /// <summary>Frames skipped since the runtime started, server-wide. Monotonic — a consumer differentiates it over its own window.</summary>
    /// <remarks>
    /// <para>
    /// The most diagnostic counter on this record: a skip is the engine deciding a session could not keep up, so a rising slope here is the shape of "why did
    /// this client not see that entity" before anyone thinks to ask it.
    /// </para>
    /// <para>
    /// <b><c>FrameAssembler.FramesSkipped</c>, the process-wide counter — not a sum over the open sessions.</b> The first version summed
    /// <c>SessionFrameState.FramesSkipped</c> across currently-open sessions, which FALLS when one closes: a consumer told to differentiate it, as the line
    /// above tells it to, got a negative rate out of an ordinary disconnect, and the skipping done by the sessions that stayed was subtracted away with the
    /// departing session's total. Per-session skips are still available, per session, on
    /// <see cref="SubscriptionsSessionTelemetryEvent.FramesSkipped"/> — where the population cannot change underneath the number.
    /// </para>
    /// </remarks>
    [BeginParam] public long FramesSkipped;
    /// <summary>Frame-pool blocks currently rented (<c>FramePool.RentedCount</c>).</summary>
    [BeginParam] public int FramePoolRented;
    /// <summary>
    /// Frame-pool blocks allocated (<c>FramePool.BlockCount</c>). Read against <see cref="FramePoolRented"/>: full occupancy is backpressure, not health.
    /// </summary>
    [BeginParam] public int FramePoolBlocks;
    /// <summary>
    /// Frames the pool refused because its byte budget was already committed (<c>FramePool.BudgetSkipCount</c>). Cumulative.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="FramesSkipped"/>, and the more actionable of the two: a per-session skip says one client could not keep up, whereas a budget
    /// skip says the SERVER ran out of frame memory and the fix is configuration rather than a client.
    /// </remarks>
    [BeginParam] public long FramePoolBudgetSkips;
    /// <summary>
    /// How many <see cref="SubscriptionsSessionTelemetryEvent"/> rows accompany this record.
    /// </summary>
    /// <remarks>
    /// Below <see cref="Sessions"/> whenever the per-session emission hit its cap. A consumer must render the difference rather than presenting the rows it
    /// received as the whole population — the same contract kind 66's <c>presentRealms</c>/<c>runnableRealms</c> pair carries.
    /// </remarks>
    [BeginParam] public int ReportedSessions;
}

/// <summary>
/// One open session's push-replication figures. Instant-shaped. See <see cref="TraceEventKind.SubscriptionsSessionTelemetry"/>.
/// </summary>
/// <remarks>
/// A TRANSPORT, like <see cref="SubscriptionsServerTelemetryEvent"/>, and capped for the same reason <see cref="SpatialRealmTelemetryEvent"/> is: its volume
/// scales with a population the engine does not bound for the trace's benefit. The count actually emitted rides the server record, so a panel can say how many
/// sessions it is not showing.
/// </remarks>
[TraceEvent(TraceEventKind.SubscriptionsSessionTelemetry, Shape = TraceEventShape.Instant, Gate = "SubscriptionsSessionTelemetryActive")]
internal ref partial struct SubscriptionsSessionTelemetryEvent
{
    /// <summary>The session's id, as the engine and the operator both name it.</summary>
    [BeginParam] public ulong SessionId;
    /// <summary>
    /// The realm this session's client currently holds (<c>SessionFrameState.CommittedRealm</c>), or <c>0xFFFF</c> before its first <c>RESET</c>.
    /// </summary>
    /// <remarks>
    /// Per session since #1050, and what makes a realm-scoped operator view possible at all. The sentinel is the committed realm's own "-1 before the first
    /// RESET" widened to the wire's unsigned field, so a consumer can tell "not yet told its realm" from "in realm 0".
    /// </remarks>
    [BeginParam] public ushort RealmId;
    /// <summary>This session's outbound bytes per second, over the window since the last emission.</summary>
    [BeginParam] public float BytesPerSec;
    /// <summary>Frames skipped for this session since it opened. Cumulative.</summary>
    [BeginParam] public long FramesSkipped;
    /// <summary>
    /// The session's degrade level — the engine's own judgement that this client is struggling, before any skip is forced.
    /// </summary>
    /// <remarks>
    /// Preferred over a raw backlog depth for this first version because it is what the engine ACTS on, and because the send state that holds the backlog is
    /// not reachable from the emission site. Zero is healthy.
    /// </remarks>
    [BeginParam] public int DegradeLevel;
}
