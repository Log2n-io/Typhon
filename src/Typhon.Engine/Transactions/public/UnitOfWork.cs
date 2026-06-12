using JetBrains.Annotations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

/// <summary>
/// The durability boundary for user operations. Batches multiple transactions for efficient persistence
/// while maintaining atomicity guarantees on crash recovery. Each UoW is assigned a UoW ID that stamps
/// all revisions created within its scope.
/// </summary>
/// <remarks>
/// <para>
/// This is the middle tier of the three-tier API hierarchy: <c>DatabaseEngine → UnitOfWork → Transaction</c>.
/// Create via <see cref="DatabaseEngine.CreateUnitOfWork"/>.
/// </para>
/// <para>
/// All durability modes route through the WAL (WAL is mandatory — ADR-054); <see cref="DurabilityMode"/> controls only WHEN a UoW's WAL records become
/// crash-safe:
/// <list type="bullet">
/// <item><b>Deferred</b>: records become durable only on an explicit <see cref="Flush"/> / <see cref="FlushAsync"/>; dispose does not flush.</item>
/// <item><b>GroupCommit</b>: the WAL writer thread auto-flushes buffered records every <c>GroupCommitIntervalMs</c>; <see cref="FlushAsync"/>
/// waits for the pending group.</item>
/// <item><b>Immediate</b>: each <c>Commit()</c> requests a WAL flush and waits for FUA durability before returning.</item>
/// </list>
/// Dirty data pages are drained by the checkpoint in every mode — never by a per-UoW <c>SaveChanges</c>.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class UnitOfWork : IDisposable
{
    private readonly DatabaseEngine _dbe;
    private readonly DurabilityMode _durabilityMode;
    private readonly ushort _uowId;
    private UnitOfWorkState _state;
    private int _transactionCount;
    private int _committedTransactionCount;
    private bool _disposed;

    private readonly CancellationTokenSource _cts;
    private readonly Deadline _deadline;

    /// <summary>Controls when WAL records become crash-safe for this UoW.</summary>
    public DurabilityMode DurabilityMode => _durabilityMode;

    /// <summary>Current lifecycle state of this UoW.</summary>
    public UnitOfWorkState State => _state;

    /// <summary>UoW identifier for revision stamping and crash recovery. Allocated from UoW Registry.</summary>
    public ushort UowId => _uowId;

    /// <summary>Number of transactions created within this UoW.</summary>
    public int TransactionCount => _transactionCount;

    /// <summary>Number of transactions that have been committed within this UoW.</summary>
    public int CommittedTransactionCount => _committedTransactionCount;

    /// <summary>Whether this UoW has been disposed.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Shared ChangeSet for Deferred and GroupCommit modes. Null for Immediate mode (each transaction owns its own).
    /// </summary>
    internal ChangeSet ChangeSet { get; }

    /// <summary>
    /// When <see langword="true"/>, transactions in this UoW skip per-row WAL serialization in <c>Transaction.PersistAndFinalize</c>. Set by
    /// <see cref="BulkLoadSession"/> to honor BL-01 — the bulk path emits a manifest pair (<c>BulkBegin</c>/<c>BulkEnd</c>) instead of per-row records. Pages
    /// still get dirty-marked normally so the checkpoint flushes them at <see cref="BulkLoadSession.CompleteBulkLoad"/>. Never set on UoWs returned by
    /// <see cref="DatabaseEngine.CreateUnitOfWork"/>.
    /// </summary>
    internal bool SuppressWalSerialization { get; }

    internal UnitOfWork(
        DatabaseEngine dbe,
        DurabilityMode durabilityMode,
        ushort uowId,
        TimeSpan timeout,
        ChangeSet changeSet = null,
        bool suppressWalSerialization = false)
    {
        _dbe = dbe;
        _durabilityMode = durabilityMode;
        _uowId = uowId;
        _state = UnitOfWorkState.Pending;

        // Deferred/GroupCommit: UoW owns the ChangeSet, shared across all transactions.
        // The ChangeSet is created early by DatabaseEngine.CreateUnitOfWork so that
        // AllocateUowId can track registry page mutations in it (avoiding sync I/O).
        // Immediate: each transaction creates its own ChangeSet for per-commit I/O.
        ChangeSet = changeSet;
        SuppressWalSerialization = suppressWalSerialization;

        _cts = new CancellationTokenSource();
        _deadline = timeout == TimeSpan.Zero
            ? Deadline.Infinite
            : Deadline.FromTimeout(timeout);
    }

    /// <summary>
    /// Creates a new transaction within this UoW. The transaction inherits the UoW's identity
    /// and deadline for revision stamping and cancellation propagation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The UoW has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The UoW is not in <see cref="UnitOfWorkState.Pending"/> state.</exception>
    [return: TransfersOwnership]
    public Transaction CreateTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != UnitOfWorkState.Pending)
        {
            throw new InvalidOperationException($"Cannot create transaction: UoW is {_state}");
        }

        Interlocked.Increment(ref _transactionCount);
        _dbe.RecordTransactionCreated();
        return _dbe.TransactionChain.CreateTransaction(_dbe, this);
    }

    /// <summary>
    /// Creates a <see cref="UnitOfWorkContext"/> for use with <see cref="Transaction.Commit(ref UnitOfWorkContext, ConcurrencyConflictHandler)"/>.
    /// Composes the UoW's deadline with the provided timeout (tighter deadline wins).
    /// </summary>
    public UnitOfWorkContext CreateContext(TimeSpan timeout = default)
    {
        var effectiveDeadline = timeout == default
            ? _deadline
            : Deadline.Min(_deadline, Deadline.FromTimeout(timeout));

        return new UnitOfWorkContext(effectiveDeadline, _cts.Token, _uowId);
    }

    /// <summary>
    /// Records that a transaction within this UoW has committed.
    /// </summary>
    internal void RecordTransactionCommitted() => Interlocked.Increment(ref _committedTransactionCount);

    /// <summary>
    /// Transitions the UoW to <see cref="UnitOfWorkState.WalDurable"/> AND records the commit in the <see cref="UowRegistry"/>. Single source of truth for
    /// "UoW is now durable" so the gauge counter increments exactly once per UoW, regardless of how many transactions the UoW hosts. MaxTSN is passed as 0 —
    /// the field is recorded on the registry entry but is not read by any production code path (gauge increment is the only observable effect).
    /// <para>
    /// The shared <see cref="ChangeSet"/> is forwarded to <see cref="UowRegistry.RecordCommit"/> so the registry page mutation piggybacks on the UoW's
    /// dirty-page accounting instead of triggering a synchronous SaveChanges (page CRC + RandomAccess.WriteAsync + fsync) on the TickDriver thread. In WAL
    /// mode the registry page is durable via the WAL record anyway; the on-disk registry copy is a checkpoint cache that the next checkpoint cycle writes.
    /// </para>
    /// </summary>
    private void TransitionToWalDurable()
    {
        _state = UnitOfWorkState.WalDurable;
        _dbe.UowRegistry?.RecordCommit(_uowId, 0, ChangeSet);
    }

    /// <summary>
    /// Synchronous flush. Forces all pending data to stable storage.
    /// For WAL mode: signals WAL writer and waits for durable LSN.
    /// For WAL-less mode: behavior depends on <see cref="DurabilityMode"/>.
    /// </summary>
    public void Flush()
    {
        if (_state != UnitOfWorkState.Pending)
        {
            return;
        }

        // WAL is mandatory: signal the WAL writer to flush and wait for the durable LSN (M7 — through the IDurabilityLog seam).
        var log = _dbe.DurabilityLog;
        log.RequestFlush();
        var currentLsn = log.LastAppendedLsn;
        if (currentLsn > 0)
        {
            var ctx = _deadline == Deadline.Infinite ? WaitContext.Null : WaitContext.FromDeadline(_deadline);
            log.WaitForDurable(currentLsn, ref ctx);
        }

        TransitionToWalDurable();
    }

    /// <summary>
    /// Async flush. WAL is mandatory: signals the WAL writer and waits for the durable LSN.
    /// </summary>
    public Task FlushAsync()
    {
        if (_state != UnitOfWorkState.Pending)
        {
            return Task.CompletedTask;
        }

        var log = _dbe.DurabilityLog;
        log.RequestFlush();
        var currentLsn = log.LastAppendedLsn;
        if (currentLsn > 0)
        {
            _dbe.LogUowFlushStart(_uowId, _durabilityMode, currentLsn);
            // Use the UoW's deadline if bounded; otherwise fall back to DefaultCommitTimeout
            // to prevent infinite hangs (especially for Deferred UoWs with Deadline.Infinite).
            var ctx = _deadline == Deadline.Infinite
                ? WaitContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout)
                : WaitContext.FromDeadline(_deadline);
            log.WaitForDurable(currentLsn, ref ctx);
            _dbe.LogUowFlushComplete(_uowId);
        }

        TransitionToWalDurable();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Release the UoW registry slot. For Deferred/GroupCommit the mutation goes into the
        // shared ChangeSet so it will be included in whatever flush path runs next.
        if (_uowId != 0)
        {
            _dbe.UowRegistry.Release(_uowId, ChangeSet);
        }

        // WAL is mandatory. Deferred UoWs skip flush — WAL records are already in the commit buffer and will be written by the WAL writer thread
        // asynchronously. Non-Deferred modes flush for the durability guarantee on dispose.
        if (_durabilityMode != DurabilityMode.Deferred)
        {
            _ = FlushAsync();
        }

        // Balance DirtyCounter to prevent inflation. ChangeSet pages are never written via SaveChangesAsync — only the checkpoint writes them. Each UoW's
        // ChangeSet incremented DirtyCounter for pages it touched, but the balancing DecrementDirty (from SavePages completion) never runs. Cap at 1 so that:
        //   (a) Pages stay dirty for checkpoint (counter >= 1)
        //   (b) One checkpoint cycle makes them evictable (1 → 0)
        ChangeSet?.ReleaseExcessDirtyMarks();

        // Cancel any outstanding operations
        _cts.Cancel();
        _cts.Dispose();

        _state = UnitOfWorkState.Free;
    }
}
