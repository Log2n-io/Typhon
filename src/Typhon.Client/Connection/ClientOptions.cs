using System;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>What a <see cref="TyphonClient"/> needs to connect, and the few dials that are genuinely an application's to turn.</summary>
/// <remarks>
/// Everything here has a default that works. The ones without a sensible default — the endpoint — have no default at all, so a
/// misconfiguration is a construction error rather than a connection that quietly goes nowhere.
/// </remarks>
public sealed class ClientOptions
{
    /// <summary>Where the server is: a <c>ws://</c> or <c>wss://</c> URL, or a <c>tcp://host:port</c> one.</summary>
    public Uri Endpoint { get; init; }

    /// <summary>The session kind the application declared, or empty for the default.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The opaque credential handed to the server's admission hook. Never interpreted here.</summary>
    public string Token { get; init; } = "";

    /// <summary>Application data for the session's <c>Opened</c> hook, at most 256 bytes.</summary>
    public byte[] HelloPayload { get; init; } = [];

    /// <summary>The optional server work to ask for. Granted is a subset; read <see cref="TyphonClient.CapsGranted"/> for what arrived.</summary>
    public Capabilities Caps { get; init; }

    /// <summary>
    /// How often <c>PING</c> carries this client's newest applied tick, in hertz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not optional, and not a keepalive.</b> The server's lag skip reads <c>lastAppliedTick</c> to decide whether this client is far enough behind that
    /// producing for it is waste, and a session that stops pinging is closed 4001. Four hertz is the catalog's own default; the server tells the client what
    /// it expects through <c>tick.pingHz</c>, and <see cref="TyphonClient"/> prefers that over this when the catalog names one.
    /// </para>
    /// <para>
    /// <b>Zero means the host drives it</b> through <see cref="TyphonClient.SendPingAsync"/>, and the client starts no loop of its own. That is what a load
    /// generator needs: a thousand clients each holding a timer is a thousand timer registrations and a thousand wakeups per period, where one shared timer
    /// walking a list costs one. A host that sets zero and then never pings will have every one of its sessions closed 4001, which is correct and is the
    /// symptom to look for.
    /// </para>
    /// </remarks>
    public int PingHz { get; init; } = 4;

    /// <summary>
    /// Motion segments kept per entity, or <c>0</c> (the default) to size the history from the render delay the clock may evaluate at
    /// (<see cref="SegmentRing.DepthFor"/>: 17 at 50 Hz). A client that never evaluates motion — a load generator, a recorder — sets <c>1</c>: the history is
    /// the store's largest per-entity cost, about 0.6 KB per moving entity at 50 Hz, and such a client holds tens of thousands of entities.
    /// </summary>
    public int SegmentHistory { get; init; }

    /// <summary>How long to wait for <c>WELCOME</c> before giving up. The server's own <c>HELLO</c> timeout is five seconds.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether to reconnect at all. When false, a close ends the client whatever the code says.</summary>
    public bool Reconnect { get; init; } = true;

    /// <summary>The first backoff delay; each attempt doubles it up to <see cref="MaxBackoff"/>, with jitter.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The ceiling on backoff, however many attempts have failed.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many consecutive failed attempts to make before giving up. Zero is unlimited.</summary>
    public int MaxReconnectAttempts { get; init; }

    /// <summary>A recorder to hand every inbound message to, or <see langword="null"/>.</summary>
    public Recorder Recorder { get; init; }

    /// <summary>Receives every frame's events as they are applied, on the receive loop; <see langword="null"/> drops them.</summary>
    public IEventHandler Events { get; init; }

    /// <summary>Throws when the options cannot produce a connection.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoint, nameof(Endpoint));
        ArgumentOutOfRangeException.ThrowIfNegative(PingHz, nameof(PingHz));

        if (HelloPayload != null && HelloPayload.Length > ProtocolConstants.HelloPayloadMaxBytes)
        {
            throw new ArgumentException(
                $"HelloPayload is {HelloPayload.Length} bytes; the protocol caps it at {ProtocolConstants.HelloPayloadMaxBytes} (03 § 3). A server would "
                + "refuse the HELLO, so this is refused here where the stack trace still names the caller.",
                nameof(HelloPayload));
        }
    }
}
