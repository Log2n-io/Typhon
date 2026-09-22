using JetBrains.Annotations;
using System;
using System.Net;
using System.Security.Claims;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// The application's admission hook: it is handed everything a connecting client said about itself and answers with a role and limits, or a refusal.
/// </summary>
/// <param name="request">What the client presented. It is a <see langword="ref"/> <see langword="struct"/>, so it cannot outlive the call.</param>
/// <returns><see cref="Admission.Accept(SessionRole, SessionLimits, object)"/> or <see cref="Admission.Reject"/>.</returns>
/// <remarks>
/// <b>Synchronous, and it touches no engine data.</b> It runs on the transport thread that decoded <c>HELLO</c>, before the session exists, so there is no
/// transaction to read and no tick to be inside of. Anything asynchronous — a JWT check, a database lookup — belongs to the authenticator the security work
/// introduces (<c>design/Subscriptions/04-transport.md § 3</c>); anything needing engine state belongs to the system that reads
/// <see cref="SessionEvents"/> in the tick.
/// </remarks>
[PublicAPI]
public delegate Admission AdmitHandler(in AdmissionRequest request);

/// <summary>
/// What a connecting client presented, as the application's admission hook sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is interpreted by the engine.</b> <see cref="Kind"/> is checked against the declared set and no further; <see cref="Token"/> is opaque
/// bytes the engine never reads. In the first version there is no engine-side security at all
/// (<c>design/Subscriptions/03-wire-protocol.md § 12 W22</c>), which is why <see cref="Principal"/> is always <see langword="null"/> today: the
/// asynchronous authenticator that produces one arrives with the security work.
/// </para>
/// <para>
/// <b>A <see langword="ref"/> <see langword="struct"/> on purpose.</b> The hook cannot stash the request, capture it in a closure or hand it to another
/// thread — which is the compiler enforcing the rule that admission is synchronous and stateless rather than a comment asking for it.
/// </para>
/// </remarks>
[PublicAPI]
public readonly ref struct AdmissionRequest
{
    /// <summary>
    /// Creates a request. The transport builds one per <c>HELLO</c>; a test builds one directly.
    /// </summary>
    /// <param name="kind">The application-declared name the client presented, or an empty string when it named none.</param>
    /// <param name="token">The client's opaque credential. Never read by the engine.</param>
    /// <param name="requestedCaps">The capability bits the client asked for. Granted ⊆ requested ∩ known ∩ authorized.</param>
    /// <param name="helloPayload">The client's application payload, at most <see cref="ProtocolConstants.HelloPayloadMaxBytes"/> bytes.</param>
    /// <param name="requestedLimits">Limits proposed ahead of the hook. <see langword="null"/> means the operator's defaults.</param>
    /// <param name="appData">Application data produced before the hook — the authenticator's, when one exists.</param>
    /// <param name="remote">The client's address, as the transport sees it.</param>
    /// <param name="transport">The transport's name: <c>tcp</c>, <c>ws</c>, <c>webtransport</c>, <c>fake</c>.</param>
    public AdmissionRequest(string kind, string token, uint requestedCaps, ReadOnlySpan<byte> helloPayload, SessionLimits requestedLimits, object appData,
        EndPoint remote, string transport)
    {
        Kind = kind ?? string.Empty;
        Token = token ?? string.Empty;
        RequestedCaps = requestedCaps;
        HelloPayload = helloPayload;
        RequestedLimits = requestedLimits ?? SessionLimits.Default;
        AppData = appData;
        Remote = remote;
        Transport = transport ?? string.Empty;
    }

    /// <summary>The application-declared kind the client presented. Empty when it named none.</summary>
    public string Kind { get; }

    /// <summary>The client's opaque credential, at most <see cref="ProtocolConstants.TokenMaxBytes"/> bytes on the wire.</summary>
    public string Token { get; }

    /// <summary>The capability bits the client asked for.</summary>
    public uint RequestedCaps { get; }

    /// <summary>The client's application payload. It is borrowed from the transport's buffer and must be copied to be kept.</summary>
    public ReadOnlySpan<byte> HelloPayload { get; }

    /// <summary>Limits proposed before the hook ran. The hook is free to hand back different ones.</summary>
    public SessionLimits RequestedLimits { get; }

    /// <summary>Application data produced before the hook — the authenticator's, when one exists. <see langword="null"/> in the first version.</summary>
    public object AppData { get; }

    /// <summary>The client's address. <see langword="null"/> for an in-process link.</summary>
    public EndPoint Remote { get; }

    /// <summary>The transport's name: <c>tcp</c>, <c>ws</c>, <c>webtransport</c>, <c>fake</c>.</summary>
    public string Transport { get; }

    /// <summary>The authenticated principal. Always <see langword="null"/> until the security work lands an authenticator.</summary>
    public ClaimsPrincipal Principal => null;
}

/// <summary>
/// The answer an application's admission hook gives: a role and limits, or a refusal carrying the close code the client is told.
/// </summary>
/// <remarks>
/// <para>
/// <b>An accepted session still has to fit.</b> The hook decides policy, not capacity: a session accepted here is refused with
/// <see cref="CloseCodes.TryAgainLater"/> when the table is full, because "who may connect" and "how many may connect" are different questions and the
/// operator owns the second one.
/// </para>
/// <para>
/// <b>Refusal codes are checked where they are written.</b> <see cref="Reject"/> throws for a code outside <c>4100-4999</c> rather than sending it, because a
/// number outside that band means something else to every client on the wire — 1002 says "the server framed a message wrong", and a hook that answers 1002
/// tells every SDK never to reconnect.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct Admission
{
    private Admission(bool accepted, SessionRole role, SessionLimits limits, object appData, ushort code, string reason)
    {
        IsAccepted = accepted;
        Role = role;
        Limits = limits;
        AppData = appData;
        RejectCode = code;
        RejectReason = reason;
    }

    /// <summary>Whether the client is admitted.</summary>
    public bool IsAccepted { get; }

    /// <summary>The role the session carries for the rest of its life.</summary>
    public SessionRole Role { get; }

    /// <summary>The limits admission asked for. Zeros mean the operator's <see cref="SubscriptionsOptions"/> value.</summary>
    public SessionLimits Limits { get; }

    /// <summary>
    /// Application data carried through to <see cref="SessionEventKind.Opened"/>, so the system that possesses an avatar knows who it is for.
    /// </summary>
    public object AppData { get; }

    /// <summary>The close code a refused client is given. Zero on an acceptance.</summary>
    public ushort RejectCode { get; }

    /// <summary>
    /// The refusal's reason, truncated to <see cref="ProtocolConstants.KickReasonMaxBytes"/> bytes by the encoder. May be <see langword="null"/>.
    /// </summary>
    public string RejectReason { get; }

    /// <summary>
    /// Admits the client.
    /// </summary>
    /// <param name="role">What the session may do. <see cref="SessionRole.Spectator"/> is the one that can do the least.</param>
    /// <param name="limits">Its ceilings. <see langword="null"/> — or any field left at zero — takes the operator's value.</param>
    /// <param name="appData">
    /// Anything the application wants back on <see cref="SessionEventKind.Opened"/>. Held by reference; the engine never reads it.
    /// </param>
    /// <returns>The acceptance.</returns>
    public static Admission Accept(SessionRole role, SessionLimits limits = null, object appData = null)
        => new(true, role, limits ?? SessionLimits.Default, appData, 0, null);

    /// <summary>
    /// Refuses the client with an application close code.
    /// </summary>
    /// <param name="code">The close code, in the application range <c>4100-4999</c>.</param>
    /// <param name="reason">An optional reason, truncated to <see cref="ProtocolConstants.KickReasonMaxBytes"/> bytes by the encoder.</param>
    /// <returns>The refusal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="code"/> is outside <c>4100-4999</c>. Every other code in the protocol already means something specific to every client, so borrowing
    /// one would make an application's policy decision read as a framing bug, an overload or a server fault.
    /// </exception>
    public static Admission Reject(ushort code, string reason = null)
    {
        if (code is < CloseCodes.FirstApplicationCode or > CloseCodes.LastApplicationCode)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code,
                $"An application close code is {CloseCodes.FirstApplicationCode}-{CloseCodes.LastApplicationCode}. Codes below that are the protocol's own " +
                "and already mean something to every client: 1002 framing, 1007 malformed, 1009 too big, 1013 try later, 4003 admission refused. The engine " +
                "sends 4003 by itself when a kind is undeclared, so a hook that wants to say why refuses with its own code in the application range.");
        }

        return new Admission(false, SessionRole.Spectator, SessionLimits.Default, null, code, reason);
    }

    /// <summary>
    /// The engine's own refusal, which is free of the application range check because it uses the protocol's reserved codes.
    /// </summary>
    /// <param name="code">The close code.</param>
    /// <param name="reason">Why.</param>
    /// <returns>The refusal.</returns>
    internal static Admission RefuseInternal(ushort code, string reason) => new(false, SessionRole.Spectator, SessionLimits.Default, null, code, reason);

    /// <inheritdoc />
    public override string ToString() => IsAccepted ? $"accept {Role}" : $"reject {RejectCode}";
}
