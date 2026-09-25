using System;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>What a client should do about a close code.</summary>
public enum ReconnectDecision
{
    /// <summary>Do not reconnect: the session ended for a reason reconnecting cannot change.</summary>
    Stop = 0,

    /// <summary>Reconnect after the backoff.</summary>
    Retry = 1,

    /// <summary>The application's call — a normal close or an application-defined code.</summary>
    Application = 2,
}

/// <summary>
/// Whether a close is worth reconnecting after, and how long to wait.
/// </summary>
/// <remarks>
/// <para>
/// <b>The close code is the whole input.</b> [05 § 1](../../../claude/design/Subscriptions/05-sdks.md) splits the codes into three groups, and the split is
/// about whether the condition can change on its own. A server going away (1001), an internal error (1011), overload (1013), a missed acknowledgement (4001)
/// and a handshake timeout (4002) are all transient: the same client reconnecting later may well succeed. A protocol error (1002), a malformed payload
/// (1007), an over-cap message (1009) and a rejected credential (4003) are not — the client would do the identical thing again and be closed for the identical
/// reason, so retrying is a hot loop that looks like a network problem.
/// </para>
/// <para>
/// <b>1000 and the application range are not ours to decide.</b> A normal close is usually the application's own <c>BYE</c>, and 4100–4999 means whatever
/// the application says it means. Guessing either way would be wrong half the time, so the decision is handed back with <see cref="ReconnectDecision.Application"/>.
/// </para>
/// <para>
/// <b>Backoff is exponential with full jitter.</b> Doubling alone synchronises every client that lost the same server onto the same retry instants, which is
/// how a restart turns into a thundering herd; the jitter is what spreads them. Full jitter — uniform in [0, delay] — rather than half, because the property
/// that matters is decorrelation, and the shorter expected wait costs nothing when the ceiling is thirty seconds.
/// </para>
/// </remarks>
public sealed class ReconnectPolicy
{
    private readonly ClientOptions _options;
    private readonly Random _random;

    /// <summary>Builds a policy over a client's options.</summary>
    /// <param name="options">The options, whose backoff bounds and attempt cap this reads.</param>
    /// <param name="seed">A seed for the jitter, so a test can replay a sequence of delays. Omit it in production.</param>
    public ReconnectPolicy(ClientOptions options, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    /// <summary>How many consecutive failures have happened since the last successful handshake.</summary>
    public int Attempt { get; private set; }

    /// <summary>What to do about a close code, ignoring the attempt cap.</summary>
    /// <param name="code">The close code.</param>
    /// <returns>The decision.</returns>
    public static ReconnectDecision DecideFor(ushort code) => code switch
    {
        CloseCodes.GoingAway => ReconnectDecision.Retry,
        CloseCodes.InternalError => ReconnectDecision.Retry,
        CloseCodes.TryAgainLater => ReconnectDecision.Retry,
        CloseCodes.PolicyViolation => ReconnectDecision.Retry,
        CloseCodes.NoAcknowledgement => ReconnectDecision.Retry,
        CloseCodes.HelloTimeout => ReconnectDecision.Retry,
        CloseCodes.ProtocolError => ReconnectDecision.Stop,
        CloseCodes.MalformedPayload => ReconnectDecision.Stop,
        CloseCodes.MessageTooBig => ReconnectDecision.Stop,
        CloseCodes.AuthenticationRejected => ReconnectDecision.Stop,
        CloseCodes.Normal => ReconnectDecision.Application,
        _ => code >= 4100 ? ReconnectDecision.Application : ReconnectDecision.Stop,
    };

    /// <summary>
    /// What to do about a close, taking the attempt cap and the client's own switch into account.
    /// </summary>
    /// <param name="code">The close code.</param>
    /// <returns>The decision.</returns>
    public ReconnectDecision Decide(ushort code)
    {
        if (!_options.Reconnect)
        {
            return ReconnectDecision.Stop;
        }

        var decision = DecideFor(code);
        if (decision == ReconnectDecision.Retry && _options.MaxReconnectAttempts > 0 && Attempt >= _options.MaxReconnectAttempts)
        {
            return ReconnectDecision.Stop;
        }

        return decision;
    }

    /// <summary>The delay before the next attempt, and counts that attempt.</summary>
    /// <returns>The delay, uniform in [0, 2^attempt × initial] and capped.</returns>
    public TimeSpan NextDelay()
    {
        var doublings = Math.Min(Attempt, 16);
        var ceiling = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, doublings);
        ceiling = Math.Min(ceiling, _options.MaxBackoff.TotalMilliseconds);
        Attempt++;
        return TimeSpan.FromMilliseconds(_random.NextDouble() * ceiling);
    }

    /// <summary>Forgets the failures. Called when a handshake completes, so a long-lived session starts fresh if it later drops.</summary>
    public void NoteConnected() => Attempt = 0;
}
