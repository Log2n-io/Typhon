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
