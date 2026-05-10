using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A WAL commit buffer claim timed out waiting for buffer space (back-pressure). Always transient — the buffer will drain and space will become available.
/// </summary>
[PublicAPI]
public class WalBackPressureTimeoutException : TyphonTimeoutException
{
    /// <summary>
    /// Creates a new <see cref="WalBackPressureTimeoutException"/>.
    /// </summary>
    /// <param name="requestedBytes">Number of bytes the producer tried to claim.</param>
    /// <param name="waitDuration">How long the producer waited before the timeout fired.</param>
    public WalBackPressureTimeoutException(int requestedBytes, TimeSpan waitDuration)
        : base( TyphonErrorCode.WalBackPressureTimeout,
                $"WAL back-pressure timeout after {waitDuration.TotalMilliseconds:F0}ms waiting for {requestedBytes} bytes", waitDuration)
    {
        RequestedBytes = requestedBytes;
    }

    /// <summary>Number of bytes the producer tried to claim.</summary>
    public int RequestedBytes { get; }
}
