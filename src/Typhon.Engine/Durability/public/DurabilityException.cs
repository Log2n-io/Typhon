using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Base class for all durability subsystem exceptions (WAL, checkpoint, recovery).
/// </summary>
[PublicAPI]
public class DurabilityException : TyphonException
{
    /// <summary>
    /// Creates a new <see cref="DurabilityException"/> with the specified error code and message.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the durability error.</param>
    public DurabilityException(TyphonErrorCode errorCode, string message) : base(errorCode, message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="DurabilityException"/> with the specified error code, message, and inner exception.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the durability error.</param>
    /// <param name="innerException">The exception that caused this durability error.</param>
    public DurabilityException(TyphonErrorCode errorCode, string message, Exception innerException)
        : base(errorCode, message, innerException)
    {
    }
}

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

/// <summary>
/// A fatal WAL write I/O failure occurred. The engine cannot accept durable commits after this error (fail-fast per ADR-020).
/// Not transient — requires engine restart.
/// </summary>
[PublicAPI]
public class WalWriteException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="WalWriteException"/>.
    /// </summary>
    /// <param name="innerException">The underlying I/O exception that caused the write failure.</param>
    public WalWriteException(Exception innerException) : base(TyphonErrorCode.WalWriteFailure, $"Fatal WAL write failure: {innerException.Message}", innerException)
    {
    }
}

/// <summary>
/// A WAL segment file operation failed (creation, rotation, or header validation).
/// </summary>
[PublicAPI]
public class WalSegmentException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="WalSegmentException"/>.
    /// </summary>
    /// <param name="segmentPath">Path to the segment file that caused the error.</param>
    /// <param name="detail">Description of the failure.</param>
    public WalSegmentException(string segmentPath, string detail) : base(TyphonErrorCode.WalSegmentError, $"WAL segment error [{segmentPath}]: {detail}")
    {
        SegmentPath = segmentPath;
    }

    /// <summary>Path to the segment file that caused the error.</summary>
    public string SegmentPath { get; }
}
