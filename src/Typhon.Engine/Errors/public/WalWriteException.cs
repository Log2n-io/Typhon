using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

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
