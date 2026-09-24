using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// P1-16 — the <c>STATS</c> block: a dense absolute snapshot of every declared metric, taken once a second and written into the frame of every session that
/// asked for it (03-wire-protocol § 3, W25).
/// </summary>
/// <remarks>
/// <para>
/// <b>Encode once, copy N times, and that is the whole shape of the thing.</b> The <c>server</c> segment is identical for every subscriber by construction —
/// it names nothing about a session — so it is collected and run through the codecs ONCE per emission, on the tick thread in
/// <see cref="FrameAssembler.BeginTick"/>, into a scratch buffer; each session's frame then copies those bytes and appends its own three values. That is
/// [02-execution § 7]'s encode-once applied to the one block that would otherwise be re-encoded per session for no difference in the result, and
/// <see cref="ServerSegmentEncodes"/> / <see cref="ServerSegmentCopies"/> expose it as a pair of counters so a test can assert it rather than infer it from a
/// timing.
/// </para>
/// <para>
/// <b>Every value source is read once per emission, never once per session.</b> The application's <c>Func&lt;double&gt;</c> sources included: a metric whose
/// source walks a structure would otherwise be walked once per subscriber, which is the trap the scope split exists to avoid.
/// </para>
/// <para>
/// <b>The cadence is <c>tick mod N = 0</c>, N = max(1, round(1 s / nominal period))</b>, so every subscriber receives the same tick's bytes and a client can
/// treat the arrival of a block as a one-second boundary. Latest wins and nothing is queued: a session skipped on an emission tick simply has no block that
/// second, which costs it nothing because the gauges are windowed and the counters are cumulative mod 2³².
/// </para>
/// <para>
/// <b>Percentiles are computed from the runtime's telemetry ring at emission time, not accumulated per tick.</b> The ring already holds every tick's
/// duration and every system's, so the per-tick cost of this whole class is zero and the once-a-second cost is one pass over the window plus one sort of it
/// — 60 samples at 60 Hz. The ring is a single-writer diagnostic structure with no publication protocol of its own, so a sample read while the tick driver is
/// writing it may be torn; the consequence is one perturbed sample in a percentile, which is why the ring is read here rather than mirrored into a structure
/// that would have to be written on the tick path to gain nothing.
/// </para>
/// <para>
/// <b>Nothing here allocates after construction.</b> The value array, the encode scratch, the sample buffer and the per-system accumulator are sized once
/// from the catalog and the window.
/// </para>
/// </remarks>
internal sealed class StatsEncoder
{
    /// <summary>Which of the engine's own numbers a server-scope metric reads. <see cref="Application"/> is everything the application declared.</summary>
    private enum ServerSource
    {
        /// <summary>A built-in this engine cannot source yet; it emits zero rather than a number nobody measured.</summary>
        Unsourced = 0,
        TickP50,
        TickP99,
        SystemMean,
        ArchetypeEntities,
        Sessions,
        NetOutBytesPerSec,
        TrackP99,
        Application,
    }

    /// <summary>One server-scope metric bound to its source, resolved once at construction so the emission path never looks a metric up by name.</summary>
    private readonly struct ServerBinding
    {
        public ServerSource Source { get; init; }

        /// <summary>Where this metric's values start in <see cref="_serverValues"/>.</summary>
        public int ValueOffset { get; init; }

        /// <summary>How many values it contributes: its label count, or 1.</summary>
        public int ValueCount { get; init; }

        /// <summary>The application's reader, for <see cref="ServerSource.Application"/>.</summary>
        public Func<double> Application { get; init; }

        /// <summary>A labelled application metric's reader, which fills its values.</summary>
        public MetricValuesSource ApplicationValues { get; init; }
    }

    /// <summary>A half saturates at its largest finite value; a metric never carries a NaN or an infinity, because a HUD would render one (W25).</summary>
    private const double MaxHalf = 65504.0;

    /// <summary>The per-session segment's three values, in the order the catalog declares them.</summary>
    private const int SessionValueCount = 3;

    private readonly CatalogPlan _plan;
    private readonly SessionTable _sessions;
    private readonly SendPump _sendPump;
    private readonly SubscriptionsIngress _ingress;
    private readonly DatabaseEngine _engine;

    private readonly ServerBinding[] _serverBindings;

    // By position in the session segment: an application metric's reader, null for the engine's own three.
    private readonly Func<SessionId, double>[] _sessionApplication;

    /// <summary>Application metric sources that threw — counted, and their values sent as zero: a metric must not take down the frame stage.</summary>
    public long ApplicationFaults;
    private readonly double[] _serverValues;
    private readonly byte[] _serverBytes;

    /// <summary>Per-system telemetry indices in the order <c>typhon.system.mean</c>'s labels name them; empty when the metric is not published.</summary>
    private readonly int[] _systemMeanIndices;

    /// <summary>The track's own per-tick timing, which is what <c>typhon.subscriptions.track.p99</c> reports (P1-17).</summary>
    private readonly SubscriptionsTelemetry _track;

    /// <summary>Per-archetype catalog ids in the order <c>typhon.archetype.entities</c>'s labels name them; empty when the metric is not published.</summary>
    private readonly ushort[] _archetypeCatalogIds;

    private readonly double[] _samples;
    private readonly double[] _systemSums;
    private readonly double _nominalTickPeriodSeconds;

    private TickTelemetryRing _telemetry;

    private long _lastEmissionTick = -1;
    private long _bytesSentAtLastEmission;
    private int _serverLength;
    private int _isEmissionTick;
    private long _serverEncodes;
    private long _serverCopies;

    /// <summary>
    /// Binds every metric the catalog declares to the number it reads.
    /// </summary>
    /// <param name="plan">The compiled catalog: the two segments, in index order, are exactly what a <c>STATS</c> block carries.</param>
    /// <param name="registry">The frozen declarations, for the application metrics' sources.</param>
    /// <param name="engine">The database, for the per-archetype live entity count.</param>
    /// <param name="plans">One compiled plan per replicated archetype, to resolve an archetype's wire name to its catalog id.</param>
    /// <param name="sessions">The session table, for the open count.</param>
    /// <param name="sendPump">The send side, for the bytes that actually left.</param>
    /// <param name="ingress">The inbound path, for a session's dropped-command counter.</param>
    /// <param name="systemNames">The scheduled systems' names in schedule order — the same list the catalog's labels were built from.</param>
    /// <param name="nominalTickPeriodUs">The nominal tick period, which sets the emission cadence and the per-second denominators.</param>
    /// <param name="track">The replication track's own per-tick timing, which is what <c>typhon.subscriptions.track.p99</c> reports.</param>
    public StatsEncoder(CatalogPlan plan, SubscriptionsRegistry registry, DatabaseEngine engine, CompiledProjectionPlan[] plans, SessionTable sessions,
        SendPump sendPump, SubscriptionsIngress ingress, IReadOnlyList<string> systemNames, uint nominalTickPeriodUs, SubscriptionsTelemetry track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(sessions);

        _plan = plan;
        _engine = engine;
        _sessions = sessions;
        _sendPump = sendPump;
        _ingress = ingress;
        _nominalTickPeriodSeconds = Math.Max(1u, nominalTickPeriodUs) / 1_000_000.0;
        EmissionPeriodTicks = Math.Max(1, (int)Math.Round(1_000_000.0 / Math.Max(1u, nominalTickPeriodUs), MidpointRounding.AwayFromZero));

        var values = 0;
        _serverBindings = new ServerBinding[plan.ServerMetrics.Length];
        for (var i = 0; i < _serverBindings.Length; i++)
        {
            var metric = plan.ServerMetrics[i];
            _serverBindings[i] = new ServerBinding
            {
                Source = SourceOf(metric.Name),
                ValueOffset = values,
                ValueCount = metric.ValueCount,
                Application = ApplicationSource(registry, metric.Name),
                ApplicationValues = Declaration(registry, metric.Name)?.ValuesSource,
            };

            values += metric.ValueCount;
        }

        _serverValues = new double[values];
        _sessionApplication = new Func<SessionId, double>[plan.SessionMetrics.Length];
        for (var i = 0; i < _sessionApplication.Length; i++)
        {
            _sessionApplication[i] = Declaration(registry, plan.SessionMetrics[i].Name)?.SessionSource;
        }

        _serverBytes = new byte[MaxSegmentBytes(plan.ServerMetrics)];

        // The block's worst case, which the frame's upper bound has to make room for: the type byte, the length prefix's full reservation, and both segments.
        MaxBlockBytes = 1 + MaxVaruBytes + _serverBytes.Length + MaxSegmentBytes(plan.SessionMetrics);

        _systemMeanIndices = LabelledSystemIndices(plan, systemNames);
        _track = track;
        _archetypeCatalogIds = LabelledArchetypeIds(plan, plans);

        // One sample per tick of the window, and the window is at most what the ring can still hold.
        _samples = new double[Math.Max(1, EmissionPeriodTicks)];
        _systemSums = new double[systemNames?.Count ?? 0];
    }

    /// <summary>Ticks between emissions: <c>max(1, round(1 s / nominal period))</c>, so a block lands on the same tick for every subscriber.</summary>
    public int EmissionPeriodTicks { get; }

    /// <summary>The block's worst case in bytes, which a frame's upper bound has to include before the encode starts.</summary>
    public int MaxBlockBytes { get; }

    /// <summary>Whether this tick carries a block. Published by <see cref="BeginTick"/> and read by every assembling worker.</summary>
    public bool IsEmissionTick => Volatile.Read(ref _isEmissionTick) != 0;

    /// <summary>How many times the server segment has been run through the codecs. One per emission, whatever the session count (W25).</summary>
    public long ServerSegmentEncodes => Volatile.Read(ref _serverEncodes);

    /// <summary>How many times those bytes have been copied into a frame. One per session that received a block.</summary>
    public long ServerSegmentCopies => Volatile.Read(ref _serverCopies);

    /// <summary>
    /// <b>Test seam.</b> When positive, overrides <see cref="EmissionPeriodTicks"/>, so a fixture can observe a block without running a runtime for a second.
    /// Nothing in production sets it; it mirrors <c>FrameAssembler.BaselineAdvancesOnSkipForTest</c>.
    /// </summary>
    internal int EmissionPeriodTicksForTest;

    /// <summary>Binds the runtime's telemetry ring, the source of every duration in the block. Called once, before the first tick.</summary>
    /// <param name="telemetry">The ring, or <see langword="null"/> on a runtime that keeps none.</param>
    public void AttachTelemetry(TickTelemetryRing telemetry) => Volatile.Write(ref _telemetry, telemetry);

    /// <summary>
    /// Decides whether this tick emits and, if it does, collects and encodes the server segment exactly once.
    /// </summary>
    /// <param name="tick">The tick being assembled.</param>
    /// <remarks>
    /// Single-threaded, on the frame stage's prologue, before any session is assembled. That is what makes "one encode for N sessions" structural rather than
    /// a race won: the workers only ever copy from a buffer that was complete before they were dispatched, and the dispatch is the barrier that publishes it.
    /// </remarks>
    public void BeginTick(long tick)
    {
        var period = EmissionPeriodTicksForTest > 0 ? EmissionPeriodTicksForTest : EmissionPeriodTicks;
        if (tick % period != 0 || (_plan.ServerMetrics.Length == 0 && _plan.SessionMetrics.Length == 0))
        {
            Volatile.Write(ref _isEmissionTick, 0);
            return;
        }

        var window = _lastEmissionTick < 0 ? period : (int)Math.Min(period, tick - _lastEmissionTick);
        Collect(tick, window);
        Encode();

        _lastEmissionTick = tick;
        _bytesSentAtLastEmission = _sendPump?.BytesSent ?? 0;

        // RELEASE — the segment's bytes and its length are written above, and this is what makes them visible to a worker that reads the flag.
        Volatile.Write(ref _isEmissionTick, 1);
    }

    /// <summary>
    /// Writes one session's <c>STATS</c> block: the shared server bytes, then its own three values.
    /// </summary>
    /// <param name="w">The frame writer, positioned where the block starts.</param>
    /// <param name="session">Whose frame it is, for the ingress row's drop counter.</param>
    /// <param name="state">Its frame state, which carries the per-session windows.</param>
    /// <param name="tick">The tick being assembled.</param>
    public void WriteBlock(ref WireWriter w, SessionId session, SessionFrameState state, long tick)
    {
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Stats);
        w.WriteBytes(new ReadOnlySpan<byte>(_serverBytes, 0, Volatile.Read(ref _serverLength)));
        WriteSessionSegment(ref w, session, state, tick);
        TickWriter.EndBlock(ref w, mark);

        Interlocked.Increment(ref _serverCopies);
    }

    // ── collection ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Collect(long tick, int window)
    {
        var telemetry = Volatile.Read(ref _telemetry);
        var seconds = Math.Max(_nominalTickPeriodSeconds, window * _nominalTickPeriodSeconds);
        var outBytesPerSec = _sendPump == null ? 0 : Math.Max(0, _sendPump.BytesSent - _bytesSentAtLastEmission) / seconds;

        for (var i = 0; i < _serverBindings.Length; i++)
        {
            ref readonly var binding = ref _serverBindings[i];
            var at = binding.ValueOffset;
            switch (binding.Source)
            {
                case ServerSource.TickP50:
                    _serverValues[at] = TickPercentile(telemetry, tick, window, 0.50);
                    break;
                case ServerSource.TickP99:
                    _serverValues[at] = TickPercentile(telemetry, tick, window, 0.99);
                    break;
                case ServerSource.TrackP99:
                    _serverValues[at] = TrackPercentile(tick, window, 0.99);
                    break;
                case ServerSource.SystemMean:
                    SystemMeans(telemetry, tick, window, _serverValues.AsSpan(at, binding.ValueCount));
                    break;
                case ServerSource.ArchetypeEntities:
                    ArchetypeEntities(_serverValues.AsSpan(at, binding.ValueCount));
                    break;
                case ServerSource.Sessions:
                    _serverValues[at] = _sessions.OpenCount;
                    break;
                case ServerSource.NetOutBytesPerSec:
                    _serverValues[at] = outBytesPerSec;
                    break;
                case ServerSource.Application:
                    // One call per emission, never one per session. A labelled metric's reader fills one value per label; a scalar one gives its value.
                    var target = _serverValues.AsSpan(at, binding.ValueCount);
                    target.Clear();
                    try
                    {
                        if (binding.ApplicationValues != null)
                        {
                            binding.ApplicationValues(target);
                        }
                        else if (binding.Application != null)
                        {
                            target[0] = binding.Application();
                        }
                    }
                    catch (Exception)
                    {
                        target.Clear();
                        Interlocked.Increment(ref ApplicationFaults);
                    }

                    break;
                default:
                    for (var v = 0; v < binding.ValueCount; v++)
                    {
                        _serverValues[at + v] = 0;
                    }

                    break;
            }
        }
    }

    private void Encode()
    {
        var writer = new WireWriter(_serverBytes);
        var at = 0;
        foreach (var metric in _plan.ServerMetrics)
        {
            for (var v = 0; v < metric.ValueCount; v++)
            {
                WriteValue(ref writer, metric, _serverValues[at + v]);
            }

            at += metric.ValueCount;
        }

        // The length before the flag, and both before the release in BeginTick: a worker that sees the flag sees a complete segment.
        Volatile.Write(ref _serverLength, writer.Position);
        Interlocked.Increment(ref _serverEncodes);
    }

    private void WriteSessionSegment(ref WireWriter w, SessionId session, SessionFrameState state, long tick)
    {
        var elapsed = Math.Max(1, tick - state.StatsTick);
        var seconds = elapsed * _nominalTickPeriodSeconds;
        var bytes = Math.Max(0, state.BytesPublished - state.StatsBytesMark);

        Span<double> values = stackalloc double[SessionValueCount];
        values[0] = bytes / seconds;
        values[1] = Counter(state.FramesSkipped);
        values[2] = Counter(_ingress?.RowOf(session) is { } row ? Volatile.Read(ref row.DroppedCommands) : 0);

        var at = 0;
        for (var m = 0; m < _plan.SessionMetrics.Length; m++)
        {
            var metric = _plan.SessionMetrics[m];
            var application = _sessionApplication[m];
            for (var v = 0; v < metric.ValueCount; v++)
            {
                var value = at < SessionValueCount ? values[at] : 0;
                if (application != null)
                {
                    // Read here, once per subscribing session per emission, on the session's worker (09 § 15, D5).
                    try
                    {
                        value = application(session);
                    }
                    catch (Exception)
                    {
                        value = 0;
                        Interlocked.Increment(ref ApplicationFaults);
                    }
                }

                WriteValue(ref w, metric, value);
                at++;
            }
        }
    }

    /// <summary>
    /// Closes a session's window, once the frame carrying its block has been published.
    /// </summary>
    /// <param name="state">The session's frame state.</param>
    /// <param name="tick">The published frame's tick.</param>
    /// <remarks>
    /// Separate from <see cref="WriteBlock"/> for the reason <c>FrameAssembler</c> separates its commit from its encode: an encode that is then abandoned —
    /// an oversize frame, an exhausted pool — must leave the session exactly as it was, or the window it reports next would be measured from a block nobody
    /// received.
    /// </remarks>
    public static void NoteBlockPublished(SessionFrameState state, long tick)
    {
        state.StatsTick = tick;
        state.StatsBytesMark = state.BytesPublished;
    }

    // ── the numbers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private double TickPercentile(TickTelemetryRing telemetry, long tick, int window, double q)
    {
        var count = FillTickSamples(telemetry, tick, window);
        return Percentile(_samples, count, q);
    }

    /// <summary>
    /// A percentile of what the replication track itself cost, in milliseconds.
    /// </summary>
    /// <param name="tick">The newest tick to consider.</param>
    /// <param name="window">How many ticks back to look.</param>
    /// <param name="q">The percentile, in [0, 1].</param>
    /// <returns>Milliseconds.</returns>
    /// <remarks>
    /// <b>This used to add up every scheduler system whose name began with "Subscriptions".</b> That was a real measurement of very nearly the right thing,
    /// and it was wrong in two ways that could not be seen from its output: an application system named with the same prefix joined the engine's number, and
    /// summing per-system durations counts a parallel stage once per worker, so the figure could exceed the tick it was measuring. The track now times
    /// itself, and reports the SPAN from its first chunk to its last — which is what "what did replication cost this tick" means.
    /// </remarks>
    private double TrackPercentile(long tick, int window, double q) => _track.Percentile(tick, window, q, _samples) / 1000.0;

    private int FillTickSamples(TickTelemetryRing telemetry, long tick, int window)
    {
        if (telemetry == null)
        {
            return 0;
        }

        var count = 0;
        var oldest = telemetry.OldestAvailableTick;
        var newest = telemetry.NewestTick;
        for (var t = Math.Max(oldest, tick - window); t <= newest && count < _samples.Length; t++)
        {
            _samples[count++] = telemetry.GetTick(t).ActualDurationMs;
        }

        return count;
    }

    private void SystemMeans(TickTelemetryRing telemetry, long tick, int window, Span<double> destination)
    {
        destination.Clear();
        if (telemetry == null || _systemMeanIndices.Length == 0)
        {
            return;
        }

        Array.Clear(_systemSums);
        var ticks = 0;
        var oldest = telemetry.OldestAvailableTick;
        var newest = telemetry.NewestTick;
        for (var t = Math.Max(oldest, tick - window); t <= newest; t++)
        {
            var systems = telemetry.GetSystemMetrics(t);
            var upTo = Math.Min(systems.Length, _systemSums.Length);
            for (var s = 0; s < upTo; s++)
            {
                _systemSums[s] += systems[s].DurationUs;
            }

            ticks++;
        }

        if (ticks == 0)
        {
            return;
        }

        var upToLabel = Math.Min(destination.Length, _systemMeanIndices.Length);
        for (var i = 0; i < upToLabel; i++)
        {
            var index = _systemMeanIndices[i];
            destination[i] = (uint)index < (uint)_systemSums.Length ? _systemSums[index] / ticks / 1000.0 : 0;
        }
    }

    private void ArchetypeEntities(Span<double> destination)
    {
        destination.Clear();
        var upTo = Math.Min(destination.Length, _archetypeCatalogIds.Length);
        for (var i = 0; i < upTo; i++)
        {
            destination[i] = _engine.GetArchetypeEntityCount(_archetypeCatalogIds[i]);
        }
    }

    /// <summary>The nearest-rank percentile of the first <paramref name="count"/> samples, which the call is free to reorder.</summary>
    private static double Percentile(double[] samples, int count, double q)
    {
        if (count <= 0)
        {
            return 0;
        }

        Array.Sort(samples, 0, count);
        var rank = (int)Math.Ceiling(q * count) - 1;
        return samples[Math.Clamp(rank, 0, count - 1)];
    }

    /// <summary>A counter travels cumulative mod 2³², so a session that missed an emission loses nothing by it (W25).</summary>
    private static double Counter(long value) => (uint)(value & 0xFFFFFFFFL);

    // ── encoding ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteValue(ref WireWriter w, MetricPlan metric, double value)
    {
        if (metric.Value.ValueKind == FieldValueKind.Skipped)
        {
            // A codec this library does not know can still be laid out: the catalog declared its width, and a decoder skips it by the same number.
            for (var i = 0; i < metric.Value.Codec.FixedBytes; i++)
            {
                w.WriteU8(0);
            }

            return;
        }

        FieldCodec.WriteNumber(ref w, metric.Value, [Saturate(value, metric.Value.Kind)]);
    }

    /// <summary>
    /// W25: a metric never carries a NaN or an infinity, a half saturates at 65 504, and an integer codec receives an integer inside its range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The integer half is not decoration — without it this method hands <see cref="FieldCodec"/> values it throws on.</b>
    /// <c>FieldCodec.ToUnsignedInteger</c> refuses anything with a fractional part outright, and both bytes-per-second built-ins are <c>varu</c> fed by a
    /// division whose denominator is the window in seconds. That denominator is exactly 1 only when the tick period divides a second exactly: at 60 Hz the
    /// window is 1.00002 s, at 120 Hz 0.99996 s, at 144 Hz 0.999936 s. So the quotient is fractional at most supported tick rates, the encode throws, and the
    /// throw leaves the frame stage — stalling the whole replication track once a second. A call the tick path makes unconditionally must never throw, and
    /// clamping at this one choke point is what makes that true for every metric rather than for the ones someone remembered.
    /// </para>
    /// <para>
    /// Ties round half away from zero, which is the protocol's rule for every codec (03 § 2). <c>quant</c>, <c>unorm</c>, <c>snorm</c> and <c>angle</c> are
    /// absent below because <see cref="WireMath"/>'s encoders already clamp their own domain and already map a NaN to zero; a second clamp here would only
    /// be a second place for the two to disagree.
    /// </para>
    /// <para>
    /// The float half is the rule <c>TickWriter.WriteStats</c> applies, restated because that one is private to the message writer and this path does not go
    /// through it — the block is written in two pieces, one shared and one per session, which a single call that writes both segments cannot express.
    /// </para>
    /// </remarks>
    private static double Saturate(double x, CodecKind kind) => kind switch
    {
        CodecKind.U8 => Integer(x, 0, byte.MaxValue),
        CodecKind.I8 => Integer(x, sbyte.MinValue, sbyte.MaxValue),
        CodecKind.U16 => Integer(x, 0, ushort.MaxValue),
        CodecKind.I16 => Integer(x, short.MinValue, short.MaxValue),
        CodecKind.U32 or CodecKind.Varu => Integer(x, 0, uint.MaxValue),
        CodecKind.I32 or CodecKind.Vari => Integer(x, int.MinValue, int.MaxValue),
        CodecKind.F16 => Real(x, MaxHalf),
        CodecKind.F32 => Real(x, float.MaxValue),
        _ => double.IsNaN(x) ? 0 : x,
    };

    /// <summary>Rounds to an integer inside <c>[min, max]</c>: a NaN reads as zero, an infinity saturates at the end it points to.</summary>
    private static double Integer(double x, double min, double max)
    {
        if (double.IsNaN(x))
        {
            return 0;
        }

        // Written as comparisons against the bounds rather than a clamp of the rounded value, so an infinity saturates without ever reaching Math.Round.
        return x <= min ? min : x >= max ? max : Math.Round(x, MidpointRounding.AwayFromZero);
    }

    /// <summary>Clamps to <c>[−max, max]</c>: a NaN reads as zero, an infinity saturates.</summary>
    private static double Real(double x, double max) => double.IsNaN(x) ? 0 : x > max ? max : x < -max ? -max : x;

    // ── binding, once ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private const int MaxVaruBytes = 5;

    private static ServerSource SourceOf(string name) => name switch
    {
        "typhon.tick.p50" => ServerSource.TickP50,
        "typhon.tick.p99" => ServerSource.TickP99,
        BuiltInMetrics.SystemMean => ServerSource.SystemMean,
        BuiltInMetrics.ArchetypeEntities => ServerSource.ArchetypeEntities,
        "typhon.sessions" => ServerSource.Sessions,
        "typhon.net.outBytesPerSec" => ServerSource.NetOutBytesPerSec,
        "typhon.subscriptions.track.p99" => ServerSource.TrackP99,

        // typhon.durability.wait.p99 lands here on purpose: the runtime times the UoW flush only through the profiler's phase wrapper, which is folded away
        // when the profiler is off, so there is no always-on number to read. It emits zero until one exists, rather than a number nobody measured.
        _ => name.StartsWith(ProtocolConstants.BuiltInMetricPrefix, StringComparison.Ordinal) ? ServerSource.Unsourced : ServerSource.Application,
    };

    private static MetricDeclaration Declaration(SubscriptionsRegistry registry, string name)
    {
        foreach (var declaration in registry.Metrics)
        {
            if (string.Equals(declaration.Name, name, StringComparison.Ordinal))
            {
                return declaration;
            }
        }

        return null;
    }

    private static Func<double> ApplicationSource(SubscriptionsRegistry registry, string name)
    {
        foreach (var declaration in registry.Metrics)
        {
            if (string.Equals(declaration.Name, name, StringComparison.Ordinal))
            {
                return declaration.Source;
            }
        }

        return null;
    }

    /// <summary>
    /// The scheduler index each label of <c>typhon.system.mean</c> names.
    /// </summary>
    /// <remarks>
    /// <b>Resolved by walking the two lists together, not by re-deriving the catalog's filter.</b> <c>CatalogBuilder</c> drops a system with no name from the
    /// labels, so label <c>k</c> is the <c>k</c>-th NAMED system rather than system <c>k</c>. Recomputing that rule here would put it in two files that have
    /// to agree; advancing a cursor over the named systems reproduces it from the data instead, and a label count that does not match the named count leaves
    /// the metric at zero rather than emitting values against the wrong systems.
    /// </remarks>
    private static int[] LabelledSystemIndices(CatalogPlan plan, IReadOnlyList<string> systemNames)
    {
        var labels = LabelsOf(plan, BuiltInMetrics.SystemMean);
        if (labels == 0 || systemNames == null)
        {
            return [];
        }

        var indices = new int[labels];
        var at = 0;
        for (var i = 0; i < systemNames.Count && at < labels; i++)
        {
            if (!string.IsNullOrEmpty(systemNames[i]))
            {
                indices[at++] = i;
            }
        }

        return at == labels ? indices : [];
    }


    /// <summary>The engine catalog id each label of <c>typhon.archetype.entities</c> names.</summary>
    /// <remarks>
    /// <para>
    /// The labels are the archetype names in WIRE index order, which canonicalization derives by ordinal sort; the compiled plans are in DECLARATION order.
    /// Resolving by name is what keeps the values aligned with the labels whichever order either list happens to be in.
    /// </para>
    /// <para>
    /// <b>A label that matches no plan abandons the whole map</b>, exactly as <see cref="LabelledSystemIndices"/> abandons its own on a count mismatch. The
    /// alternative is worse than it looks: zero is a valid <c>ArchetypeCatalogId</c>, so leaving an unresolved entry at its default would report the FIRST
    /// archetype's population under another archetype's label — a wrong number that reads as a right one. A metric of zeros is visibly broken; a metric of
    /// plausible numbers attributed to the wrong thing is not.
    /// </para>
    /// </remarks>
    private static ushort[] LabelledArchetypeIds(CatalogPlan plan, CompiledProjectionPlan[] plans)
    {
        var metric = FindMetric(plan, BuiltInMetrics.ArchetypeEntities);
        var labels = metric?.Metric?.Labels;
        if (labels == null || labels.Length == 0)
        {
            return [];
        }

        var ids = new ushort[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var found = false;
            foreach (var compiled in plans)
            {
                if (string.Equals(compiled.Name, labels[i], StringComparison.Ordinal))
                {
                    ids[i] = compiled.ArchetypeCatalogId;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return [];
            }
        }

        return ids;
    }

    private static int LabelsOf(CatalogPlan plan, string name) => FindMetric(plan, name)?.Metric?.Labels?.Length ?? 0;

    private static MetricPlan FindMetric(CatalogPlan plan, string name)
    {
        foreach (var metric in plan.ServerMetrics)
        {
            if (string.Equals(metric.Name, name, StringComparison.Ordinal))
            {
                return metric;
            }
        }

        return null;
    }

    /// <summary>The widest a segment can be: every metric's every value at its codec's largest encoding.</summary>
    private static int MaxSegmentBytes(MetricPlan[] metrics)
    {
        var bytes = 0;
        foreach (var metric in metrics)
        {
            bytes += metric.ValueCount * MaxValueBytes(metric.Value);
        }

        return bytes;
    }

    private static int MaxValueBytes(FieldPlan field) => field.Kind switch
    {
        CodecKind.U8 or CodecKind.I8 => 1,
        CodecKind.U16 or CodecKind.I16 or CodecKind.F16 => 2,
        CodecKind.U32 or CodecKind.I32 or CodecKind.F32 => 4,
        CodecKind.Varu or CodecKind.Vari => MaxVaruBytes,
        CodecKind.Quant or CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle => Math.Max(1, (field.Codec.Bits + 7) / 8),
        CodecKind.Unknown => Math.Max(0, field.Codec.FixedBytes),

        // Nothing else is a legal metric codec (W25); the encode would throw on it, and a bound that is too large only wastes scratch.
        _ => 8,
    };
}
