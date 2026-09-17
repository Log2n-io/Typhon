using System;

namespace Typhon.Engine.Internals;

/// <summary>What the skip policy decided for one session on one tick.</summary>
internal enum SkipVerdict
{
    /// <summary>Produce a frame.</summary>
    Produce = 0,

    /// <summary>Skip this tick. The session keeps its baseline and its next frame carries the union (SUB-03).</summary>
    Skip = 1,

    /// <summary>Skip, and the session has been skipped long enough that it must be closed with 1013.</summary>
    Close = 2,
}

/// <summary>
/// When a session is skipped rather than served, when it is degraded a rate class, and when it is closed — the three thresholds of
/// <c>design/Subscriptions/07-delivery.md</c>, in one place so no caller re-derives one of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skip, never queue.</b> Every decision here ends in "produce nothing this tick"; none of them ends in "hold this frame for later". Records are absolute
/// (SUB-03), so the next frame a session does receive carries everything the skipped ones would have, and a queue would only build a backlog a slow client can
/// never drain — while costing the encode of every frame in it.
/// </para>
/// <para>
/// <b>Three independent reasons, one counter.</b> The in-flight bound (<c>K</c> frames outstanding), the acknowledgement lag and the degrade gate all advance
/// the same <c>SkipRun</c>, which is what <see cref="Evaluate"/> then reads. That is deliberate: what the operator cares about is "this session has not been
/// served for N ticks", not which of the three reasons produced each of those N.
/// </para>
/// <para>
/// <b>The round trip is an allowance, not a measurement.</b> The design writes the lag bound as
/// <c>producedTick − ackedTick &gt; max(5, ⌈(RTT + PING period) / tick⌉ + 2)</c>, but the wire gives the server no way to measure RTT: <c>PING.clientMs</c> is
/// opaque and echoed, so it is the <i>client</i> that measures the round trip. Rather than invent a measurement, the bound uses a fixed allowance
/// (<see cref="SubscriptionsOptions.LagSkipRttAllowanceMs"/>) in RTT's place and says so. A client that reports its own RTT — which no message carries today —
/// would let this become the measurement the formula assumes; that is written down as an open item rather than faked here.
/// </para>
/// </remarks>
internal static class SkipPolicy
{
    /// <summary>The floor of the lag bound: no session is skipped for lag before it is this many ticks behind, however fast the tick.</summary>
    /// <remarks>
    /// At 60 Hz a 250 ms allowance alone gives 17 ticks; at 1 Hz it gives one, and a one-tick bound would skip every healthy client, because an
    /// acknowledgement cannot arrive in less than a round trip. The floor is what makes the bound safe at slow tick rates.
    /// </remarks>
    public const int MinimumLagTicks = 5;

    /// <summary>How many consecutive produced frames earn a degraded session its rate class back.</summary>
    /// <remarks>
    /// <b>Asymmetric on purpose.</b> Degrading takes 20 skips and recovering takes 200 frames, so a session that oscillates around the threshold settles at
    /// the degraded rate instead of flapping between the two — and a frame produced resets <c>SkipRun</c>, so a symmetric rule would recover on the first
    /// frame after a degrade and degrade again 20 ticks later, forever.
    /// </remarks>
    public const int RecoveryFrames = 200;

    /// <summary>The deepest a session's rate class may be dropped: one frame in four.</summary>
    /// <remarks>Beyond this the session is not a slow client but a dead one, and <see cref="SubscriptionsOptions.CloseAfterSkips"/> is the answer.</remarks>
    public const int MaxDegradeLevel = 2;

    /// <summary>
    /// How many ticks a session's acknowledgement may lag before it is skipped for lag.
    /// </summary>
    /// <param name="options">The operator's rails — the ping rate and the round-trip allowance.</param>
    /// <param name="tickPeriodUs">The nominal tick period.</param>
    /// <returns>The bound, never below <see cref="MinimumLagTicks"/>.</returns>
    public static int LagBoundTicks(SubscriptionsOptions options, uint tickPeriodUs)
    {
        ArgumentNullException.ThrowIfNull(options);

        var period = Math.Max(1u, tickPeriodUs);
        var pingPeriodMs = options.PingHz <= 0 ? 0 : 1000 / options.PingHz;
        var allowanceUs = ((long)options.LagSkipRttAllowanceMs + pingPeriodMs) * 1000;
        var ticks = (int)Math.Min(int.MaxValue - 2, (allowanceUs + period - 1) / period) + 2;
        return Math.Max(MinimumLagTicks, ticks);
    }

    /// <summary>
    /// How many ticks a session's acknowledgement is behind what was produced for it.
    /// </summary>
    /// <param name="producedTick">The tick of the last frame published for the session.</param>
    /// <param name="ackedTick">The newest tick the client reported applied.</param>
    /// <returns>The lag, or zero for a session that has received nothing or acknowledged nothing yet.</returns>
    /// <remarks>
    /// A session that has never been acknowledged is not lagging: a client that has just connected has nothing to acknowledge, and treating its zero as
    /// "infinitely behind" would skip every session for its first few hundred milliseconds — precisely the window in which it needs its first frames.
    /// </remarks>
    public static long AcknowledgementLag(long producedTick, long ackedTick)
        => ackedTick <= 0 || producedTick <= ackedTick ? 0 : producedTick - ackedTick;

    /// <summary>
    /// Whether a degraded session produces on this tick.
    /// </summary>
    /// <param name="degradeLevel">The session's dropped rate classes, 0 for none.</param>
    /// <param name="tick">The tick.</param>
    /// <returns><see langword="false"/> when the session's rate class skips this tick.</returns>
    /// <remarks>
    /// A rate class is a power of two, so the gate is a mask rather than a modulo, and every session at the same level lands on the same ticks — which is what
    /// lets shared frames (P1-15) keep sharing across a degrade instead of splitting one encode into two.
    /// </remarks>
    public static bool ProducesOnTick(int degradeLevel, long tick)
        => degradeLevel <= 0 || (tick & ((1L << Math.Min(degradeLevel, MaxDegradeLevel)) - 1)) == 0;

    /// <summary>
    /// Reads a session's skip run against the operator's two thresholds.
    /// </summary>
    /// <param name="skipRun">Consecutive ticks the session has been skipped.</param>
    /// <param name="options">The operator's rails.</param>
    /// <returns>Whether the run has reached the degrade or the close threshold.</returns>
    public static SkipVerdict Evaluate(int skipRun, SubscriptionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.CloseAfterSkips > 0 && skipRun >= options.CloseAfterSkips)
        {
            return SkipVerdict.Close;
        }

        return skipRun > 0 ? SkipVerdict.Skip : SkipVerdict.Produce;
    }

    /// <summary>
    /// Whether a skip run has earned the session a dropped rate class.
    /// </summary>
    /// <param name="skipRun">Consecutive ticks the session has been skipped.</param>
    /// <param name="degradeLevel">What it has been dropped already.</param>
    /// <param name="options">The operator's rails.</param>
    /// <returns><see langword="true"/> when the level should rise by one.</returns>
    /// <remarks>
    /// The test is on a multiple of the threshold rather than on the threshold alone, so a session that stays stuck drops a second class after another 20
    /// skips rather than dropping every class at once the moment it crosses the first line.
    /// </remarks>
    public static bool ShouldDegrade(int skipRun, int degradeLevel, SubscriptionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.DegradeAfterSkips <= 0 || degradeLevel >= MaxDegradeLevel)
        {
            return false;
        }

        return skipRun >= options.DegradeAfterSkips * (degradeLevel + 1);
    }

    /// <summary>
    /// How many ticks of silence close a session with 4001.
    /// </summary>
    /// <param name="options">The operator's rails.</param>
    /// <param name="tickPeriodUs">The nominal tick period.</param>
    /// <returns>The bound.</returns>
    /// <remarks>
    /// Three ping periods, floored at the lag bound: a client that missed one ping is not gone, and a client that missed three has stopped talking. The floor
    /// keeps the two policies from crossing at slow tick rates, where three ping periods can be fewer ticks than the lag bound and a healthy client would be
    /// closed for silence before it was ever skipped for lag.
    /// </remarks>
    public static int SilenceBoundTicks(SubscriptionsOptions options, uint tickPeriodUs)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PingHz <= 0)
        {
            return int.MaxValue;
        }

        var period = Math.Max(1u, tickPeriodUs);
        var silenceUs = 3L * 1000 * 1000 / options.PingHz;
        var ticks = (int)Math.Min(int.MaxValue, (silenceUs + period - 1) / period);
        return Math.Max(ticks, LagBoundTicks(options, tickPeriodUs));
    }
}
