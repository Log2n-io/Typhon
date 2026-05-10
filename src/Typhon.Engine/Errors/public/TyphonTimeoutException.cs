using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Base for all timeout-related exceptions.
/// Enables <c>catch (TyphonTimeoutException)</c> to handle any timeout uniformly.
/// Does NOT inherit from <see cref="System.TimeoutException"/> (would break single-inheritance chain).
/// </summary>
[PublicAPI]
public class TyphonTimeoutException : TyphonException
{
    /// <summary>
    /// Creates a new <see cref="TyphonTimeoutException"/> with the specified error code, message, and wait duration.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the timeout.</param>
    /// <param name="waitDuration">How long the caller waited before the timeout fired.</param>
    public TyphonTimeoutException(TyphonErrorCode errorCode, string message, TimeSpan waitDuration) : base(errorCode, message)
    {
        WaitDuration = waitDuration;
    }

    /// <summary>
    /// Creates a new <see cref="TyphonTimeoutException"/> with the specified error code, message, wait duration, and inner exception.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the timeout.</param>
    /// <param name="waitDuration">How long the caller waited before the timeout fired.</param>
    /// <param name="innerException">The exception that caused this timeout.</param>
    public TyphonTimeoutException(TyphonErrorCode errorCode, string message, TimeSpan waitDuration, Exception innerException)
        : base(errorCode, message, innerException)
    {
        WaitDuration = waitDuration;
    }

    /// <summary>How long the caller waited before the timeout fired.</summary>
    public TimeSpan WaitDuration { get; }

    /// <summary>Timeouts are always transient — the resource is presumably available later.</summary>
    public override bool IsTransient => true;
}
