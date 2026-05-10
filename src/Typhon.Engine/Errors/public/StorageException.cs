using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A storage-layer failure (I/O error, page fault, segment corruption).
/// </summary>
[PublicAPI]
public class StorageException : TyphonException
{
    /// <summary>
    /// Creates a new <see cref="StorageException"/> with the specified error code and message.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the storage error.</param>
    public StorageException(TyphonErrorCode errorCode, string message) : base(errorCode, message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="StorageException"/> with the specified error code, message, and inner exception.
    /// </summary>
    /// <param name="errorCode">Numeric error code identifying the specific failure.</param>
    /// <param name="message">Human-readable description of the storage error.</param>
    /// <param name="innerException">The exception that caused this storage error.</param>
    public StorageException(TyphonErrorCode errorCode, string message, Exception innerException) : base(errorCode, message, innerException)
    {
    }
}
