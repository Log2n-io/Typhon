namespace Typhon.Client;

/// <summary>
/// The round trip measured from <c>PONG</c> (05-sdks § 1): the client's own <c>clientMs</c> comes back, so a round trip needs no state per ping.
/// </summary>
/// <remarks>
/// <para>
/// The same arithmetic as the TypeScript and native SDKs' <c>PingScheduler</c>, pinned across the three by the <c>clock-trace</c> golden vector:
/// <c>clientMs</c> is the local millisecond clock truncated to 32 bits, the round trip is the difference in the same arithmetic — so a wrap every 49 days
/// costs nothing — and the smoothed value is TCP's estimator (RFC 6298), a new sample weighted 1/8.
/// </para>
/// <para>
/// <see cref="Clock.SubTickPhase"/> takes the one-way delay its caller estimates; half of <see cref="RttMs"/> is the usual one.
/// </para>
/// <para>
/// <b>Written by the receive loop, read by anyone.</b> Each property is one aligned word read, so a reader sees a value some <c>PONG</c> produced; it may
/// see <see cref="RttMs"/> and <see cref="LastRttMs"/> from two different <c>PONG</c>s, which for a smoothed figure read at human rate is no error.
/// </para>
/// </remarks>
public sealed class RoundTripEstimator
{
    /// <summary>Creates an estimator.</summary>
    /// <param name="weight">The weight of a new sample in <see cref="RttMs"/>; 1/8 by default, as the other SDKs and TCP use.</param>
    public RoundTripEstimator(double weight = 0.125) => Weight = weight;

    /// <summary>The weight of a new sample in the smoothed round trip.</summary>
    public double Weight { get; }

    /// <summary>The smoothed round trip in milliseconds, 0 before the first <c>PONG</c>.</summary>
    public double RttMs { get; private set; }

    /// <summary>The latest round trip in milliseconds, 0 before the first <c>PONG</c>.</summary>
    public double LastRttMs { get; private set; }

    /// <summary><c>PONG</c>s measured since the estimator was created or reset.</summary>
    public long PongsReceived { get; private set; }

    /// <summary>The local millisecond clock as a <c>PING</c>'s <c>clientMs</c> carries it: truncated, then to 32 bits.</summary>
    /// <param name="nowMs">The local time in milliseconds, never negative.</param>
    /// <returns>The 32-bit stamp.</returns>
    public static uint Stamp(double nowMs) => nowMs > 0 ? unchecked((uint)(ulong)nowMs) : 0u;

    /// <summary>Measures one <c>PONG</c>.</summary>
    /// <param name="clientMs">The <c>PONG</c>'s <c>clientMs</c>: the stamp this client sent.</param>
    /// <param name="recvMs">The local time the <c>PONG</c> arrived, on the clock the stamp was taken from.</param>
    public void OnPong(uint clientMs, double recvMs)
    {
        PongsReceived++;
        double rtt = unchecked(Stamp(recvMs) - clientMs);
        LastRttMs = rtt;

        // Seeded by the FIRST sample, not by "the smoothed value is still zero": stamps are whole milliseconds, so 0 is an ordinary loopback round
        // trip, and treating it as "no sample yet" took the next sample whole.
        RttMs = PongsReceived == 1 ? rtt : RttMs + (Weight * (rtt - RttMs));
    }

    /// <summary>Forgets every sample: a new session.</summary>
    public void Reset()
    {
        RttMs = 0;
        LastRttMs = 0;
        PongsReceived = 0;
    }
}
