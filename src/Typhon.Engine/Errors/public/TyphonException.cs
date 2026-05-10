using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Base class for all Typhon engine exceptions.
/// Provides structured error information: numeric code and transience hint.
/// </summary>
[PublicAPI]
public class TyphonException : Exception
{
    /// <summary>
    /// Creates a new <see cref="TyphonException"/> with the specified error code and message.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the error.</param>
    public TyphonException(TyphonErrorCode errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Creates a new <see cref="TyphonException"/> with the specified error code, message, and inner exception.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public TyphonException(TyphonErrorCode errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Numeric error code identifying the specific failure.</summary>
    public TyphonErrorCode ErrorCode { get; }

    /// <summary>
    /// Hint: true if this failure is temporary and retrying may succeed.
    /// The engine does NOT retry automatically — this is informational for callers.
    /// Default is false — subclasses must explicitly opt in to transience.
    /// </summary>
    public virtual bool IsTransient => false;
}
