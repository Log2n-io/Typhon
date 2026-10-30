using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thread-safe, lightweight snapshot accessor for parallel entity access.
/// Constructed once (empty), then <see cref="Attach"/>ed before each parallel system dispatch to obtain a fresh TSN.
/// Per-worker <see cref="EntityAccessor"/> instances are stored in a flat array indexed by worker ID — zero per-entity
/// dictionary overhead.
/// <para>
/// Supports reading all storage modes (Versioned via MVCC chain walk, SingleVersion/Transient direct).
/// Supports writing SingleVersion and Transient components. Throws on Versioned writes.
/// Does not support Spawn, Destroy, Commit, or Rollback.
/// </para>
/// <para>
/// <b>Reuse pattern:</b> A single PTA is constructed once and reused across all non-Versioned parallel systems within a tick
/// and across ticks. Call <see cref="Attach"/> before each system dispatch — it allocates a fresh TSN and resets per-worker
/// EntityAccessors while preserving ChunkAccessor page caches (zero allocation after first-tick warmup).
/// </para>
/// </summary>
[PublicAPI]
public sealed class PointInTimeAccessor : IDisposable
{
    private DatabaseEngine _dbe;
    private EntityAccessor[] _workerAccessors; // Flat array indexed by workerId — no dictionary lookup
    private int _workerCount;
    private bool _disposed;

    /// <summary>Creates an empty PointInTimeAccessor. Call <see cref="Attach"/> before use.</summary>
    public PointInTimeAccessor()
    {
    }

    private PointInTimeAccessor(DatabaseEngine dbe, long tsn, int workerCount)
    {
        _dbe = dbe;
        _workerCount = workerCount;
        _workerAccessors = new EntityAccessor[workerCount];
        TSN = tsn;
        IsAttached = true;
    }

    /// <summary>
    /// Creates a PointInTimeAccessor with a frozen MVCC snapshot at the current TSN.
    /// </summary>
    public static PointInTimeAccessor Create(DatabaseEngine dbe, int workerCount = 1)
    {
        var tsn = dbe.TransactionChain.AllocateTSN();
        return new PointInTimeAccessor(dbe, tsn, workerCount);
    }

    /// <summary>The frozen MVCC snapshot timestamp. All workers see the same snapshot.</summary>
    public long TSN { get; private set; }

    /// <summary>Whether this accessor has been attached to a DatabaseEngine and has a valid TSN.</summary>
    public bool IsAttached { get; private set; }

    /// <summary>
    /// Attach (or re-attach) this accessor with a fresh TSN.
    /// On first call: stores the engine reference and worker count, allocates the accessor array and a TSN.
    /// On subsequent calls: allocates a fresh TSN and resets all existing per-worker EntityAccessors via
    /// <see cref="EntityAccessor.ResetForNewSnapshot"/>, preserving ChunkAccessor page caches.
    /// <para>
    /// Must be called from a single thread (the prepare phase), NOT concurrently with worker access.
    /// </para>
    /// </summary>
    public void Attach(DatabaseEngine dbe, int workerCount)
    {
        CheckConfig.Require(CheckConfig.Enabled, !_disposed, $"PointInTimeAccessor used after disposal");

        CheckConfig.Require(CheckConfig.Enabled, _dbe == null || _dbe == dbe,
            $"PointInTimeAccessor.Attach called with a different DatabaseEngine than the initial Attach");
        _dbe ??= dbe;

        // Resize array if worker count changed
        if (_workerAccessors == null || _workerAccessors.Length < workerCount)
        {
            var old = _workerAccessors;
            _workerAccessors = new EntityAccessor[workerCount];
            if (old != null)
            {
                Array.Copy(old, _workerAccessors, old.Length);
            }
        }

        _workerCount = workerCount;

        var newTsn = _dbe.TransactionChain.AllocateTSN();
        TSN = newTsn;
        IsAttached = true;

        // Reset existing accessors (warm caches preserved)
        ResetWorkersForSnapshot(newTsn);
    }

    /// <summary>
    /// Leases the given worker's <see cref="EntityAccessor"/> to the calling thread: pins the thread's epoch for the lease's length and hands out the
    /// worker's accessor, created on first use and kept warm across leases (its caches, not a pin). Read only through <see cref="WorkerLease.Accessor"/>,
    /// inside the lease: <c>using var lease = pta.Enter(workerId);</c>. The runtime takes one per chunk.
    /// </summary>
    /// <remarks>
    /// The pin is the lease's, never the accessor's (EP-04, #1532): held from the first use of a worker accessor until its thread died, it froze the page
    /// cache's eviction floor at the oldest worker's pin, and a tick that stalled on a page never saw one freed. A lease is a <c>ref struct</c>, so it
    /// cannot be stored, captured or carried to another thread: its pin and its unpin run on one thread. A worker accessor used with no lease open throws.
    /// </remarks>
    public WorkerLease Enter(int workerId)
    {
        CheckConfig.Require(CheckConfig.Enabled, !_disposed, $"PointInTimeAccessor used after disposal");
        CheckConfig.Require(CheckConfig.Enabled, IsAttached, $"PointInTimeAccessor used before Attach");
        CheckConfig.Require(CheckConfig.Enabled, workerId >= 0 && workerId < _workerAccessors.Length,
            $"workerId {workerId} out of range [0, {_workerAccessors.Length})");

        var epochManager = _dbe.EpochManager;
        var depth = epochManager.EnterScope();
        try
        {
            var acc = _workerAccessors[workerId];
            if (acc == null)
            {
                acc = new EntityAccessor();
                acc.InitLightweight(_dbe, TSN);
                _workerAccessors[workerId] = acc;
            }

            acc.BeginLease();
            return new WorkerLease(acc, epochManager, depth);
        }
        catch
        {
            epochManager.ExitScope(depth);
            throw;
        }
    }

    /// <summary>
    /// A worker accessor leased to one thread for one stretch of reads (<see cref="Enter"/>): its epoch pin lasts exactly as long as the lease. Dispose it
    /// on the thread that took it, in reverse order of nested leases — <c>using</c> does both. Never copy it: two copies would end one lease twice.
    /// </summary>
    [MustDispose(Reason = "keeps the worker epoch-pinned past its lease")]
    [NoCopy]
    public ref struct WorkerLease
    {
        private readonly EntityAccessor _accessor;
        private readonly EpochManager _epochManager;
        private readonly int _depth;

        internal WorkerLease(EntityAccessor accessor, EpochManager epochManager, int depth)
        {
            _accessor = accessor;
            _epochManager = epochManager;
            _depth = depth;
        }

        /// <summary>The worker's accessor: read, and write SingleVersion / Transient components, through it while the lease is open.</summary>
        public readonly EntityAccessor Accessor => _accessor;

        /// <summary>Ends the lease and unpins the thread's epoch (the outermost lease on the thread).</summary>
        public void Dispose()
        {
            if (_accessor == null)
            {
                return;
            }

            _accessor.EndLease();
            _epochManager.ExitScope(_depth);
            this = default;
        }
    }

    /// <summary>
    /// Resets every worker accessor to <paramref name="tsn"/>, committing what each holds. Under an epoch scope of its own: the commits touch the
    /// accessors' pages, and a worker accessor no longer carries a pin of its own (EP-04).
    /// </summary>
    private void ResetWorkersForSnapshot(long tsn)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        for (var i = 0; i < _workerCount; i++)
        {
            var acc = _workerAccessors[i];
            if (acc != null)
            {
                CheckConfig.Require(CheckConfig.Enabled, !acc.IsLeased, $"PointInTimeAccessor snapshot moved while worker {i} is leased");
                acc.ResetForNewSnapshot(tsn);
            }
        }
    }

    /// <summary>
    /// Advance to a new MVCC snapshot. Equivalent to <see cref="Attach"/> but reuses the existing engine and worker count.
    /// </summary>
    public void AdvanceSnapshot()
    {
        CheckConfig.Require(CheckConfig.Enabled, !_disposed, $"PointInTimeAccessor used after disposal");
        CheckConfig.Require(CheckConfig.Enabled, IsAttached, $"PointInTimeAccessor.AdvanceSnapshot called before Attach");
        var newTsn = _dbe.TransactionChain.AllocateTSN();
        TSN = newTsn;

        ResetWorkersForSnapshot(newTsn);
    }

    /// <summary>
    /// Flushes the given worker's EntityAccessor: commits its dirty chunk state, releases its dirty marks, and lets go of every page its windows hold (its
    /// component accessors', its entity-map cache's, its cluster cache's) — the accessor stays usable and refills on its next read. The runtime calls it
    /// once per system, for every worker, from whichever thread completed the system, with no lease open. Call it with no lease open, or from the thread
    /// holding the worker's lease. It moves no epoch: the worker holds none outside a lease.
    /// </summary>
    public void FlushWorker(int workerId)
    {
        CheckConfig.Require(CheckConfig.Enabled, !_disposed, $"PointInTimeAccessor used after disposal");
        if (workerId >= 0 && workerId < _workerCount && _workerAccessors[workerId] is { } acc)
        {
            CheckConfig.Require(CheckConfig.Enabled, !acc.IsLeasedByAnotherThread,
                $"PointInTimeAccessor worker {workerId} flushed while another thread leases it");
            using var guard = EpochGuard.Enter(_dbe.EpochManager);
            acc.FlushAtSystemEnd();
        }
    }

    /// <summary>Detaches this accessor and disposes every per-worker <see cref="EntityAccessor"/> it owns; idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        IsAttached = false;

        if (_workerAccessors != null)
        {
            // Under a scope of its own, as the resets: the disposes commit what the accessors hold (EP-04).
            var guard = _dbe != null ? EpochGuard.Enter(_dbe.EpochManager) : default;
            try
            {
                for (var i = 0; i < _workerAccessors.Length; i++)
                {
                    CheckConfig.Require(CheckConfig.Enabled, _workerAccessors[i] is not { IsLeased: true },
                        $"PointInTimeAccessor disposed while worker {i} is leased");
                    _workerAccessors[i]?.Dispose();
                    _workerAccessors[i] = null;
                }
            }
            finally
            {
                if (_dbe != null)
                {
                    guard.Dispose();
                }
            }
        }
    }
}
