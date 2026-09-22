using System;

namespace Typhon.Protocol;

/// <summary>
/// Thrown when bytes received from the other side do not form a valid message. It carries the close code the connection is closed with, so the transport
/// never has to classify a decoding failure itself.
/// </summary>
/// <remarks>
/// Only a decoder throws this, and only for input it received. An encoder handed values it cannot represent throws <see cref="ArgumentException"/> instead:
/// that is a bug on the sending side, not a hostile or broken peer.
/// </remarks>
public sealed class WireFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="closeCode">The close code the connection must be closed with (see <see cref="CloseCodes"/>).</param>
    /// <param name="message">What was wrong with the input.</param>
    public WireFormatException(ushort closeCode, string message) : base(message) => CloseCode = closeCode;

    /// <summary>The close code the connection must be closed with: 1002 for framing, 1007 for a malformed payload, 1009 for an oversized message.</summary>
    public ushort CloseCode { get; }

    internal static WireFormatException Malformed(string message) => new(CloseCodes.MalformedPayload, message);

    internal static WireFormatException Protocol(string message) => new(CloseCodes.ProtocolError, message);
}
