using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A single WAL claim exceeds the entire buffer capacity. Not transient — the claim can never succeed without reconfiguring the buffer.
/// </summary>
[PublicAPI]
public class WalClaimTooLargeException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="WalClaimTooLargeException"/>.
    /// </summary>
    /// <param name="requestedBytes">Number of bytes the producer tried to claim.</param>
    /// <param name="bufferCapacity">Maximum capacity of the buffer in bytes.</param>
    public WalClaimTooLargeException(int requestedBytes, int bufferCapacity)
        : base(TyphonErrorCode.WalClaimTooLarge, $"WAL claim of {requestedBytes} bytes exceeds buffer capacity of {bufferCapacity} bytes")
    {
        RequestedBytes = requestedBytes;
        BufferCapacity = bufferCapacity;
    }

    /// <summary>Number of bytes the producer tried to claim.</summary>
    public int RequestedBytes { get; }

    /// <summary>Maximum capacity of the buffer in bytes.</summary>
    public int BufferCapacity { get; }
}
