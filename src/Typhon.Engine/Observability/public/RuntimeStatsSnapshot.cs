using System;
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
/// <b>Read at human rate, not on the tick path.</b> It allocates (one snapshot, two arrays) and walks the telemetry window, so it belongs behind an HTTP
/// endpoint, a CLI verb or a log timer — not in a system. Nothing here is a counter the caller must difference: every figure is either an instantaneous
/// level or a percentile over the window the caller asked for.
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
    public int Overruns { get; init; }

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
