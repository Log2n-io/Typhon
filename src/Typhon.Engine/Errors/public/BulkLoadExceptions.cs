using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Thrown by <see cref="DatabaseEngine.BeginBulkLoad"/> when a bulk session is already open. BulkLoad is exclusive in v1 — only one session per engine.
/// Regular <see cref="UnitOfWork"/>s continue to run.
/// </summary>
[PublicAPI]
public class BulkSessionAlreadyActiveException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="BulkSessionAlreadyActiveException"/> identifying the active bulk session.
    /// </summary>
    /// <param name="activeBulkSessionId">64-bit id of the bulk session that is currently open.</param>
    public BulkSessionAlreadyActiveException(long activeBulkSessionId)
        : base(TyphonErrorCode.BulkSessionAlreadyActive,
               $"a BulkLoad session is already active (id={activeBulkSessionId:X16}); only one bulk session can be open per engine in v1")
    {
        ActiveBulkSessionId = activeBulkSessionId;
    }

    /// <summary>64-bit id of the bulk session that prevented this call from acquiring the gate.</summary>
    public long ActiveBulkSessionId { get; }
}

/// <summary>
/// Thrown by <c>BulkLoadSession.{Spawn, Update, Destroy, CompleteBulkLoad}</c> when the session has already been completed (via <c>CompleteBulkLoad</c>) or
/// disposed.
/// </summary>
[PublicAPI]
public class BulkSessionClosedException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="BulkSessionClosedException"/>.
    /// </summary>
    /// <param name="bulkSessionId">64-bit id of the closed session.</param>
    public BulkSessionClosedException(long bulkSessionId)
        : base(TyphonErrorCode.BulkSessionClosed,
               $"BulkLoad session (id={bulkSessionId:X16}) is already closed; create a fresh session via DatabaseEngine.BeginBulkLoad")
    {
        BulkSessionId = bulkSessionId;
    }

    /// <summary>64-bit id of the closed session.</summary>
    public long BulkSessionId { get; }
}

/// <summary>
/// Thrown by <see cref="BulkLoadSession.CompleteBulkLoad"/> when the synchronous checkpoint did not complete within
/// <see cref="BulkLoadOptions.CheckpointTimeout"/>. The bulk session remains alive — the caller may retry <see cref="BulkLoadSession.CompleteBulkLoad"/>
/// (e.g., with a longer timeout) or call <see cref="BulkLoadSession.Dispose"/> to discard.
/// </summary>
[PublicAPI]
public class BulkLoadCheckpointTimeoutException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="BulkLoadCheckpointTimeoutException"/>.
    /// </summary>
    /// <param name="bulkSessionId">64-bit id of the session whose checkpoint timed out.</param>
    /// <param name="timeout">Configured checkpoint timeout that elapsed.</param>
    public BulkLoadCheckpointTimeoutException(long bulkSessionId, TimeSpan timeout)
        : base(TyphonErrorCode.BulkLoadCheckpointTimeout,
               $"BulkLoad session (id={bulkSessionId:X16}) checkpoint did not complete within {timeout.TotalSeconds:F1}s; "
               + "session remains alive — retry or dispose")
    {
        BulkSessionId = bulkSessionId;
        Timeout = timeout;
    }

    /// <summary>64-bit id of the session whose checkpoint timed out.</summary>
    public long BulkSessionId { get; }

    /// <summary>The timeout value that elapsed.</summary>
    public TimeSpan Timeout { get; }
}
