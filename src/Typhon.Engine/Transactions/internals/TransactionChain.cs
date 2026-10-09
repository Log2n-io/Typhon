using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal class TransactionChain : ResourceNode, IDebugPropertiesProvider
{
    private const int PoolMaxSize = 16;

    private Transaction _head;
    private Transaction _tail;
    private long _minTSN;

    internal Transaction Head => _head;
    internal Transaction Tail => _tail;
    internal long MinTSN => _minTSN;
    internal long NextFreeId => _nextFreeId;

    /// <summary>
    /// Gets the count of active transactions in the chain.
    /// </summary>
    /// <remarks>
    /// Maintained via atomic increment/decrement in PushHead/Remove for O(1) access.
    /// </remarks>
    internal int ActiveCount => _activeCount;

    /// <summary>
    /// Count of idle <see cref="Transaction"/> instances currently pooled for reuse. Backs the profiler's <c>TxChainPoolSize</c> gauge.
    /// <see cref="ConcurrentQueue{T}.Count"/> is O(n) — fine at the bounded <see cref="PoolMaxSize"/> = 16.
    /// </summary>
    internal int PoolCount => _pool.Count;

    /// <summary>
    /// Cumulative count of transactions successfully committed since engine start. Monotonic. Incremented from
    /// <see cref="Transaction.Commit(ref UnitOfWorkContext, ConcurrencyConflictHandler)"/> via <see cref="IncrementCommitTotal"/>. The profiler emits
    /// this as a gauge; the viewer derives per-tick throughput by subtracting consecutive snapshots.
    /// </summary>
    internal long CommitTotal => _commitTotal;

    /// <summary>Cumulative count of transactions rolled back since engine start. Monotonic. Same per-tick-delta derivation on the viewer.</summary>
    internal long RollbackTotal => _rollbackTotal;

    /// <summary>
    /// Cumulative count of transactions created (via <see cref="CreateTransaction"/>) since engine start. Monotonic. Lets the viewer
    /// derive per-tick "transactions started" throughput by subtracting consecutive snapshots — the symmetric counterpart to
    /// <see cref="CommitTotal"/> / <see cref="RollbackTotal"/>.
    /// </summary>
    internal long CreatedTotal => _createdTotal;

    private long _commitTotal;
    private long _rollbackTotal;
    private long _createdTotal;

    /// <summary>
    /// Called from <see cref="Transaction.Commit(ref UnitOfWorkContext, ConcurrencyConflictHandler)"/> to bump the cumulative commit counter.
    /// Lock-free, atomic.
    /// </summary>
    internal void IncrementCommitTotal() => Interlocked.Increment(ref _commitTotal);

    /// <summary>
    /// Called from <see cref="Transaction.Rollback(ref UnitOfWorkContext, Typhon.Profiler.TransactionRollbackReason)"/> to bump the cumulative
    /// rollback counter.
    /// </summary>
    internal void IncrementRollbackTotal() => Interlocked.Increment(ref _rollbackTotal);

    private AccessControl _control;
    private readonly ConcurrentQueue<Transaction> _pool;
    private readonly int _maxActiveTransactions;
    private long _nextFreeId;
    private int _activeCount;

    public TransactionChain(int maxActiveTransactions, IResource parent) : base("TransactionChain", ResourceType.TransactionPool, parent)
    {
        _dbe = parent as DatabaseEngine;
        _maxActiveTransactions = maxActiveTransactions;
        _nextFreeId = 1;
        _pool = new ConcurrentQueue<Transaction>();
        for (int i = 0; i < PoolMaxSize; i++)
        {
            _pool.Enqueue(new Transaction());
        }
    }

    /// <summary>
    /// Sets the next free TSN to the given value. Used during engine reload to restore the TSN counter from the persisted header so that MVCC visibility
    /// works for entities created by previous sessions.
    /// </summary>
    internal void SetNextFreeId(long value)
    {
        _nextFreeId = value;

        // A reopened database whose counter is past the limit is refused at open, not at its first transaction (#1208); one past the warning says so now.
        if (value >= TsnRefusalThreshold)
        {
            throw new TransactionSequenceExhaustedException(value, TsnRefusalThreshold);
        }

        if (value >= TsnWarningThreshold)
        {
            WarnTsnHeadroomOnce(value);
        }
    }

    /// <summary>Test seam: sets the TSN counter without the open-time check of <see cref="SetNextFreeId"/>.</summary>
    internal void SetNextFreeIdForTest(long value) => _nextFreeId = value;

    /// <summary>The engine this chain belongs to (its resource parent), for the TSN headroom warning; null in a fixture without one.</summary>
    private readonly DatabaseEngine _dbe;

    public ref AccessControl Control => ref _control;

    /// <summary>
    /// Registers <paramref name="transaction"/> and returns its TSN (TXC-01): pushed with its TSN pending (0), then the TSN drawn and published. A cutoff
    /// walker that sees the node waits for its TSN (<see cref="AwaitTsn"/>); one that read the head before the push read the counter before that, and this
    /// TSN, drawn after the push, is above what it read.
    /// </summary>
    internal long Register([TransfersOwnership] Transaction transaction)
    {
        PushHead(transaction);
        CreationWindowProbe?.Invoke();
        var tsn = Interlocked.Increment(ref _nextFreeId);
        transaction.PublishTsn(tsn);
        if (Volatile.Read(ref _tail) == transaction)
        {
            // Pushed into an empty chain: its TSN is the tail's. A Remove that made it the tail meanwhile awaited and wrote the same value.
            Volatile.Write(ref _minTSN, tsn);
        }

        return tsn;
    }

    /// <summary>
    /// Lock-free CAS-based stack push of a transaction whose TSN is still pending. Concurrent with Remove (which holds exclusive lock) and other PushHead
    /// callers (CAS contention only).
    /// </summary>
    private void PushHead([TransfersOwnership] Transaction transaction)
    {
        Interlocked.Increment(ref _activeCount);
        Transaction oldHead;
        do
        {
            oldHead = Volatile.Read(ref _head);
            transaction.Next = oldHead;
        } while (Interlocked.CompareExchange(ref _head, transaction, oldHead) != oldHead);

        if (oldHead == null)
        {
#pragma warning disable TYPHON004 // CAS on _tail — returned old value is intentionally discarded (conditional write, not a leak)
            Interlocked.CompareExchange(ref _tail, transaction, null);
#pragma warning restore TYPHON004
        }
    }

    /// <summary>
    /// The TSN of a transaction in the chain. A node pushed but not yet drawn (TXC-01) is waited for: its creator is two instructions from publishing it,
    /// and needs no lock to do so.
    /// </summary>
    private long AwaitTsn(Transaction transaction)
    {
        var tsn = transaction.ReadPublishedTsn();
        if (tsn == 0)
        {
            Interlocked.Increment(ref _pendingNodeWaits);
            var spin = new SpinWait();
            while ((tsn = transaction.ReadPublishedTsn()) == 0)
            {
                spin.SpinOnce(sleep1Threshold: -1);   // never Sleep(1): the creator is runnable and only needs the CPU
            }
        }

        return tsn;
    }

    public void WalkHeadToTail(Func<Transaction, bool> predicate)
    {
        _control.EnterSharedAccess(ref WaitContext.Null);

        var cur = Volatile.Read(ref _head);
        while (cur != null)
        {
            if (!predicate(cur))
            {
                break;
            }

            cur = cur.Next;
        }

        _control.ExitSharedAccess();
    }

    /// <summary>
    /// The lowest <see cref="Transaction.InFlightLsnFloor"/> over the live transactions: the first LSN appended by any commit still between its WAL
    /// append and the end of its publish, or <see cref="long.MaxValue"/> when there is none (CK-13).
    /// </summary>
    internal long LowestInFlightLsn()
    {
        var lowest = long.MaxValue;
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_control.EnterSharedAccess(ref wc))
        {
            // A lock timeout is transient, so the checkpoint cycle that asked is retried rather than latched fatal (CK-06).
            ThrowHelper.ThrowLockTimeout("TransactionChain/LowestInFlightLsn", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        try
        {
            for (var cur = Volatile.Read(ref _head); cur != null; cur = cur.Next)
            {
                var floor = cur.InFlightLsnFloor;
                if (floor != 0 && floor < lowest)
                {
                    lowest = floor;
                }
            }
        }
        finally
        {
            _control.ExitSharedAccess();
        }

        return lowest;
    }

    /// <summary>
    /// The cutoff the leaving tail hands the revision cleanup: the lowest snapshot that may still read, over every other transaction in the chain and every
    /// one being created (TXC-01). Caller must hold shared lock on Control, and be the tail.
    /// </summary>
    /// <remarks>
    /// The minimum, not the second-to-last node's TSN: concurrent creations push in any order, so the chain is not in TSN order. The counter is read before
    /// the head: a transaction pushed after this walk read the head draws its TSN after that, above <c>issued</c>; a pending node the walk does see is
    /// awaited. The walk is the one the second-to-last search already made.
    /// </remarks>
    internal long ComputeNextMinTSN()
    {
        var issued = Volatile.Read(ref _nextFreeId);
#pragma warning disable TYPHON004 // a reference compared against, not owned: the tail is its caller's to dispose
        var tail = Volatile.Read(ref _tail);
#pragma warning restore TYPHON004
        var next = issued + 1;
        for (var cur = Volatile.Read(ref _head); cur != null; cur = cur.Next)
        {
            if (cur != tail)
            {
                next = Math.Min(next, AwaitTsn(cur));
            }
        }

        if (CutoffProbe is { } probe)
        {
            next = probe(next);
        }

        PublishRetainedMinTSN(next);
        return next;
    }

    /// <summary>How many times a cutoff walk, or a Remove, met a node whose TSN was still pending and waited for it (TXC-01). Diagnostic.</summary>
    internal long PendingNodeWaits => Volatile.Read(ref _pendingNodeWaits);

    private long _pendingNodeWaits;

    /// <summary>
    /// Test seam (TXC-02): invoked by a Remove of the tail alone in the chain, just before its CAS of the head (<c>false</c>) and, when that CAS emptied
    /// the chain, just after it (<c>true</c>).
    /// </summary>
    internal Action<bool> RemoveTailProbe { get; set; }

    /// <summary>TXC-02's mutant: a Remove that empties the chain clears the tail after its CAS of the head instead of before.</summary>
    internal bool TailClearedAfterCasMutantForTest;

    /// <summary>Test seam (TXC-01's mutant): replaces the cutoff <see cref="ComputeNextMinTSN"/> computed.</summary>
    internal Func<long, long> CutoffProbe { get; set; }

    /// <summary>
    /// The oldest TSN whose revisions are still guaranteed to be retained. A snapshot below this may have been trimmed — #672.
    /// </summary>
    /// <remarks>
    /// Monotonic. Every caller of <see cref="ComputeNextMinTSN"/> computes it in order to trim to it, so publishing here covers the trim sites without
    /// having to find them individually. Cleanups can complete out of order, hence an interlocked max rather than a plain store.
    /// <para>
    /// <see cref="PointInTimeAccessor"/> allocates a TSN via <see cref="AllocateTSN"/> and registers NOTHING in the chain, so
    /// <see cref="ComputeNextMinTSN"/> cannot see it and no amount of care on the cleanup side will retain its snapshot. This watermark is what lets a read
    /// at such a TSN discover that its snapshot is gone instead of reading a zeroed component.
    /// </para>
    /// </remarks>
    internal long RetainedMinTSN => Volatile.Read(ref _retainedMinTSN);

    private long _retainedMinTSN;

    private void PublishRetainedMinTSN(long value)
    {
        var seen = Volatile.Read(ref _retainedMinTSN);
        while (value > seen)
        {
            var prior = Interlocked.CompareExchange(ref _retainedMinTSN, value, seen);
            if (prior == seen)
            {
                return;
            }

            seen = prior;
        }
    }

    /// <summary>
    /// Removes a transaction from the chain. Holds exclusive lock to serialize with other Removes.
    /// Uses CAS on _head with re-scan fallback to handle concurrent PushHead.
    /// </summary>
    public void Remove(Transaction transaction)
    {
        _control.EnterExclusiveAccess(ref WaitContext.Null);
        Interlocked.Decrement(ref _activeCount);

        // The tail is the chain's last node, and only a Remove moves it off a node. A push that found the chain empty may still set it after this read, but
        // `transaction` is then not the last node, and isTail is false either way.
        var isTail = Volatile.Read(ref _tail) == transaction;

        // Scan for predecessor (singly-linked)
        var cur = Volatile.Read(ref _head);
        Transaction prev = null;
        while (cur != null && cur != transaction)
        {
            prev = cur;
            cur = cur.Next;
        }

        // Unlink
        if (prev != null)
        {
            prev.Next = transaction.Next;
            if (isTail)
            {
                // The last node's predecessor is the new last node.
                SetTail(prev);
            }
        }
        else
        {
            if (isTail && !TailClearedAfterCasMutantForTest)
            {
                // Alone in the chain (TXC-02): the tail is cleared BEFORE the CAS that empties the head, which orders it. A push that then finds the chain
                // empty finds the tail cleared too, and its own CAS makes it the tail. Cleared after, a push in between would fail that CAS against this
                // transaction, and the chain would hold a node and no tail.
                Volatile.Write(ref _minTSN, 0);
                Volatile.Write(ref _tail, null);
            }

            if (isTail)
            {
                RemoveTailProbe?.Invoke(false);
            }

            if (Interlocked.CompareExchange(ref _head, transaction.Next, transaction) == transaction)
            {
                if (isTail)
                {
                    RemoveTailProbe?.Invoke(true);
                    if (TailClearedAfterCasMutantForTest)
                    {
                        Volatile.Write(ref _tail, null);
                        Volatile.Write(ref _minTSN, 0);
                    }
                }
            }
            else
            {
                // PushHead prepended nodes between our Volatile.Read and CAS — re-scan for new predecessor
                var p = Volatile.Read(ref _head);
                while (p.Next != transaction)
                {
                    p = p.Next;
                }
                p.Next = transaction.Next;
                if (isTail)
                {
                    SetTail(p);
                }
            }
        }

        // Capture pool decision under lock; transaction is fully unlinked, so Reset+Enqueue can safely happen outside the critical section to reduce hold time.
        bool shouldPool = _pool.Count < PoolMaxSize;

        _control.ExitExclusiveAccess();

        if (shouldPool)
        {
            transaction.Reset();
            _pool.Enqueue(transaction);
        }
        else
        {
            // Dropped rather than pooled, so nothing will ever reset or reuse it. Its spawn arena holds native memory that only Dispose returns — Reset now
            // RETAINS a block by design, so a discarded transaction that ever spawned would leak that block for the process lifetime.
            transaction.DisposeSpawnArena();
        }
    }

    /// <summary>Test seam (TXC-01): invoked inside a creation's window, between the push and the TSN draw. A test holds a creator there.</summary>
    internal Action CreationWindowProbe { get; set; }

    /// <summary>
    /// Makes <paramref name="node"/>, the chain's last node, the tail; its TSN may still be pending (TXC-01). Caller holds the exclusive lock.
    /// </summary>
    private void SetTail(Transaction node)
    {
        Volatile.Write(ref _tail, node);
        Volatile.Write(ref _minTSN, AwaitTsn(node));
    }

    /// <summary>
    /// Allocates a monotonically-increasing TSN without creating a Transaction.
    /// Used by <see cref="PointInTimeAccessor"/> to get an MVCC visibility snapshot point.
    /// </summary>
    internal long AllocateTSN()
    {
        CheckTsnHeadroom();
        return Interlocked.Increment(ref _nextFreeId);
    }

    /// <summary>
    /// The counter above which a TSN draw takes the slow path (#1208): a warning, once, then the refusal at <see cref="TsnRefusalThreshold"/>. A TSN is
    /// stored in 48 bits (<c>CompRevStorageElement.TSN</c>, <c>EntityRecordHeader.BornTSN</c>/<c>DiedTSN</c>); bit 47 stays free for the commit-stamp
    /// design's pending mark.
    /// </summary>
    internal const long TsnWarningThreshold = 1L << 46;

    /// <summary>
    /// No TSN is drawn once the counter reaches this (#1208): 2^47 minus a fixed margin of 2^24. Fixed, not per live transaction: draws pass the check
    /// and then draw, so concurrent ones can overshoot it by up to one per thread, and the commit-stamp design (option C) draws a stamp at a commit point
    /// that can never be refused. 2^24 covers both with room to spare, at a cost of one part in 10^7 of the range.
    /// </summary>
    internal const long TsnRefusalThreshold = (1L << 47) - (1L << 24);

    private int _tsnWarned;

    /// <summary>
    /// The begin path's one predictable branch (#1208): below <see cref="TsnWarningThreshold"/> nothing happens. Above it, a warning once; at
    /// <see cref="TsnRefusalThreshold"/>, <see cref="ResourceExhaustedException"/> — a refusal before the 48-bit stored TSN can wrap and read as "born at
    /// genesis, alive".
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckTsnHeadroom()
    {
        // A plain read: the value is advisory (the margin absorbs a stale one) and takes part in no ordering.
        var next = _nextFreeId;
        if (next >= TsnWarningThreshold)
        {
            TsnHeadroomSlowPath(next);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TsnHeadroomSlowPath(long next)
    {
        if (next >= TsnRefusalThreshold)
        {
            throw new TransactionSequenceExhaustedException(next, TsnRefusalThreshold);
        }

        WarnTsnHeadroomOnce(next);
    }

    private void WarnTsnHeadroomOnce(long next)
    {
        // Read first: past 2^46 every draw comes here, for years — an unconditional exchange would be a locked write on the chain's line per draw.
        if (Volatile.Read(ref _tsnWarned) == 0 && Interlocked.Exchange(ref _tsnWarned, 1) == 0)
        {
            _dbe?.WarnTsnHeadroomLow(next, TsnRefusalThreshold);
        }
    }

    /// <summary>Whether the TSN headroom warning has been logged (#1208).</summary>
    internal bool TsnHeadroomWarned => Volatile.Read(ref _tsnWarned) != 0;

    /// <summary>
    /// Lock-free transaction creation. No lock acquired — uses ConcurrentQueue for pooling, then <see cref="Register"/>: CAS-based push, atomic TSN increment.
    /// </summary>
    [return: TransfersOwnership]
    public Transaction CreateTransaction(DatabaseEngine dbe, UnitOfWork uow = null, bool readOnly = false, CommitDiscipline discipline = CommitDiscipline.TickFence,
        bool ownChangeSet = false)
    {
        if (_activeCount >= _maxActiveTransactions)
        {
            ThrowHelper.ThrowResourceExhausted("Data/TransactionChain/CreateTransaction", ResourceType.Service, _activeCount, _maxActiveTransactions);
        }

        CheckTsnHeadroom();

        if (!_pool.TryDequeue(out var t))
        {
            t = new Transaction();
        }

        t.Init(dbe, uow, readOnly, discipline, ownChangeSet);

        // Gauge: cumulative "transactions created" counter. Single convergence point — every call path (UnitOfWork.CreateTransaction,
        // DatabaseEngine.CreateQuickTransaction, DatabaseEngine.CreateReadOnlyTransaction) funnels through this method, so one
        // increment here counts every transaction exactly once. Pooled (dequeued) and newly-allocated paths both pass through.
        Interlocked.Increment(ref _createdTotal);

        return t;
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            // Free each pooled transaction's retained arena block before dropping the queue. Clear() alone releases the managed references and leaks the
            // native memory behind them, which Reset deliberately keeps alive between uses.
            while (_pool.TryDequeue(out var pooled))
            {
                pooled.DisposeSpawnArena();
            }
            _pool.Clear();
        }

        base.Dispose(disposing);
        IsDisposed = true;
    }

    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["ActiveCount"] = _activeCount,
            ["MaxActive"] = _maxActiveTransactions,
            ["MinTSN"] = MinTSN,
            ["NextFreeId"] = _nextFreeId,
            ["Pool.Available"] = _pool.Count,
            ["Pool.MaxSize"] = PoolMaxSize,
        };
}