namespace Typhon.Protocol;

/// <summary>
/// Values fixed by the protocol major, which a client needs before it has received any catalog (03-wire-protocol § 2, decision W22).
/// </summary>
public static class ProtocolConstants
{
    /// <summary>The protocol major this library speaks. The WebSocket subprotocol is <c>typhon.2</c>; the TCP preamble is <see cref="TcpPreamble"/>.</summary>
    public const ushort Major = 2;

    /// <summary>The protocol minor this library speaks. The lower minor of the two sides wins.</summary>
    public const ushort Minor = 0;

    /// <summary>The WebSocket subprotocol name for <see cref="Major"/>.</summary>
    public const string WebSocketSubprotocol = "typhon.2";

    /// <summary>The 4-byte TCP preamble, ASCII <c>TYP2</c>: each side writes its own; a mismatch closes the connection without a <c>KICK</c> (W31).</summary>
    public static System.ReadOnlySpan<byte> TcpPreamble => "TYP2"u8;

    /// <summary>Largest <c>HELLO</c> message, in bytes: the first message's own limit, before <c>limits.clientMessageBytes</c> applies.</summary>
    public const int HelloMaxBytes = 16 * 1024;

    /// <summary>Largest <c>HELLO</c> token, in UTF-8 bytes.</summary>
    public const int TokenMaxBytes = 8 * 1024;

    /// <summary>Largest session kind name, in UTF-8 bytes.</summary>
    public const int SessionKindMaxBytes = 32;

    /// <summary>Largest application payload a <c>HELLO</c> may carry, in bytes.</summary>
    public const int HelloPayloadMaxBytes = 256;

    /// <summary>Largest <c>KICK</c> reason, in UTF-8 bytes: a WebSocket close frame's own limit (RFC 6455 § 5.5).</summary>
    public const int KickReasonMaxBytes = 123;

    /// <summary>How long a server waits for <c>HELLO</c> before closing with <see cref="CloseCodes.HelloTimeout"/>, in milliseconds.</summary>
    public const int HelloTimeoutMs = 5000;

    /// <summary>Command indices below this are reserved for built-in commands; application commands start here.</summary>
    public const int FirstAppCommandIdx = 16;

    /// <summary>Event indices below this are reserved for built-in events; application events start here.</summary>
    public const int FirstAppEventIdx = 16;

    /// <summary>Metric indices below this are reserved for built-in metrics; application metrics start here.</summary>
    public const int FirstAppMetricIdx = 32;

    /// <summary>The name prefix reserved for built-in metrics.</summary>
    public const string BuiltInMetricPrefix = "typhon.";

    /// <summary>At most this many change groups per archetype, public or owner: the width of a record's <c>u8</c> group mask.</summary>
    public const int MaxGroups = 8;

    /// <summary>At most this many archetypes: an SDK limit (the TypeScript store packs the archetype into 8 bits of a handle), not a wire limit.</summary>
    public const int MaxArchetypes = 255;

    /// <summary>The widest <c>bits</c> field: a shift of at most 7 plus the width fits one 32-bit read.</summary>
    public const int MaxPackedBits = 24;

    /// <summary>The largest element count a <c>list</c> codec may declare.</summary>
    public const int MaxListCount = 255;
}

/// <summary>
/// Close codes, with the meanings RFC 6455 gives them, plus the engine's private-use range (W24). A <c>KICK</c> carries one of these; on WebSocket the same
/// number closes the connection.
/// </summary>
public static class CloseCodes
{
    /// <summary>Normal closure: <c>BYE</c>, or a clean server shutdown.</summary>
    public const ushort Normal = 1000;

    /// <summary>The server is going away; reconnect with backoff.</summary>
    public const ushort GoingAway = 1001;

    /// <summary>Protocol error: framing, or an unknown or out-of-state message type.</summary>
    public const ushort ProtocolError = 1002;

    /// <summary>A payload that is inconsistent with its type: a bad index, length, UTF-8 sequence or an over-cap field.</summary>
    public const ushort MalformedPayload = 1007;

    /// <summary>A message above its size limit, detected before decoding.</summary>
    public const ushort MessageTooBig = 1009;

    /// <summary>An internal server error, such as a flush failure after a frame was produced.</summary>
    public const ushort InternalError = 1011;

    /// <summary>Try again later: the server is full, or this session lagged too far behind.</summary>
    public const ushort TryAgainLater = 1013;

    /// <summary>No <c>PING</c> received within the acknowledgement window.</summary>
    public const ushort NoAcknowledgement = 4001;

    /// <summary>No <c>HELLO</c> within <see cref="ProtocolConstants.HelloTimeoutMs"/>.</summary>
    public const ushort HelloTimeout = 4002;

    /// <summary>The application's admission hook rejected the connection's credentials.</summary>
    public const ushort AuthenticationRejected = 4003;

    /// <summary>The first code of the application range, <c>4100–4999</c>.</summary>
    public const ushort FirstApplicationCode = 4100;

    /// <summary>The last code of the application range.</summary>
    public const ushort LastApplicationCode = 4999;

    /// <summary>Whether a client may send <paramref name="code"/> in <c>BYE</c>: 1000, or the private-use range 4000–4999 a browser accepts.</summary>
    /// <param name="code">The close code.</param>
    /// <returns><see langword="true"/> when a browser's <c>WebSocket.close</c> would accept it.</returns>
    public static bool IsValidClientCode(ushort code) => code == Normal || code is >= 4000 and <= 4999;
}
