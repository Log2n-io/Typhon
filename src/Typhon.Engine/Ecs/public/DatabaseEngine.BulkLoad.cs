using System.Threading;

namespace Typhon.Engine;

public partial class DatabaseEngine
{
    /// <summary>
    /// 0 = no bulk session active. Non-zero = the active bulk session's id. The exclusive bulk gate is held via
    /// <see cref="Interlocked.CompareExchange(ref long, long, long)"/> from 0 → sessionId at <see cref="BeginBulkLoad"/> entry and reset to 0
    /// in <see cref="ReleaseBulkSessionGate"/>.
    /// </summary>
    /// <remarks>
    /// BulkLoad is exclusive in v1 — only one session per engine at a time. Regular <see cref="UnitOfWork"/>s continue to run; they see the pre-bulk MVCC
    /// snapshot.
    /// </remarks>
    private long _bulkSessionGate;

    /// <summary>
    /// Counter for assigning <see cref="BulkLoadSession.BulkSessionId"/> values. Monotonic per engine instance.
    /// Persisted-vs-volatile is irrelevant in v1: a fresh engine boot resets the counter, and recovery uses the session id from the WAL chunks (not this
    /// counter).
    /// </summary>
    private long _nextBulkSessionId;

    /// <summary>
    /// Open a new <see cref="BulkLoadSession"/>. Acquires the engine-wide exclusive bulk gate; throws <see cref="BulkSessionAlreadyActiveException"/> if a
    /// session is already open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <c>claude/design/Durability/BulkLoad/01-api.md</c> for the API reference and lifecycle. A bulk session is <i>thread-affine</i> — only the thread
    /// that opened it may call methods on it. Regular transactions can run concurrently and see the pre-bulk snapshot.
    /// </para>
    /// <para>
    /// Mutate through the returned session's <see cref="BulkLoadSession.Spawn{TArch}"/>, <see cref="BulkLoadSession.Update{T}"/>, and
    /// <see cref="BulkLoadSession.Destroy"/>, then call <see cref="BulkLoadSession.CompleteBulkLoad"/> to commit and checkpoint the bulk durably. Disposing
    /// without completing discards it — none of the bulk's revisions become visible (UR-03).
    /// </para>
    /// </remarks>
    /// <param name="options">Optional configuration. Defaults to a new <see cref="BulkLoadOptions"/> if <see langword="null"/>.</param>
    /// <returns>A fresh, open bulk session. Caller must <see cref="BulkLoadSession.CompleteBulkLoad"/> or
    /// <see cref="BulkLoadSession.Dispose"/> when done; the engine-wide gate is held until then.</returns>
    /// <exception cref="BulkSessionAlreadyActiveException">A bulk session is already open on this engine.</exception>
    public BulkLoadSession BeginBulkLoad(BulkLoadOptions options = null)
    {
        options ??= new BulkLoadOptions();

        var newSessionId = Interlocked.Increment(ref _nextBulkSessionId);
        var prev = Interlocked.CompareExchange(ref _bulkSessionGate, newSessionId, 0L);
        if (prev != 0L)
        {
            throw new BulkSessionAlreadyActiveException(prev);
        }

        return new BulkLoadSession(this, options, newSessionId);
    }

    /// <summary>
    /// Releases the bulk-session exclusive gate so a subsequent <see cref="BeginBulkLoad"/> can succeed. Called by <see cref="BulkLoadSession.Dispose"/>
    /// (which is in turn called by <c>CompleteBulkLoad</c>'s closing path and by the explicit discard path).
    /// </summary>
    internal void ReleaseBulkSessionGate() => Interlocked.Exchange(ref _bulkSessionGate, 0L);

    /// <summary>
    /// Internal helper used by <see cref="BulkLoadSession"/>'s constructor to allocate a <see cref="UnitOfWork"/> with <c>SuppressWalSerialization=true</c>.
    /// Mirrors <see cref="CreateUnitOfWork"/> with the bulk-only flag — the public <see cref="CreateUnitOfWork"/> never sets this flag, and external callers
    /// cannot construct a <see cref="UnitOfWork"/> directly (the constructor is internal).
    /// </summary>
    internal UnitOfWork CreateUnitOfWorkForBulkLoad()
    {
        var effectiveTimeout = TimeoutOptions.Current.DefaultUowTimeout;
        var wc = WaitContext.FromTimeout(effectiveTimeout);

        // Bulk uses Deferred so per-tx Commit returns without waiting on fsync.
        // The bulk's durability comes from CompleteBulkLoad's explicit checkpoint + WaitForDurable.
        var changeSet = MMF.CreateChangeSet();
        var uowId = UowRegistry.AllocateUowId(ref wc, changeSet);

        return new UnitOfWork(this, DurabilityMode.Deferred, uowId, effectiveTimeout, changeSet, suppressWalSerialization: true);
    }
}
