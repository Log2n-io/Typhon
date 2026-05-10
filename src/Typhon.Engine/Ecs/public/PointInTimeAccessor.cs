using System;
using System.Diagnostics;
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
        Debug.Assert(!_disposed, "PointInTimeAccessor used after disposal");

        Debug.Assert(_dbe == null || _dbe == dbe, "PointInTimeAccessor.Attach called with a different DatabaseEngine than the initial Attach");
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
        for (var i = 0; i < _workerCount; i++)
        {
            _workerAccessors[i]?.ResetForNewSnapshot(newTsn);
        }
    }

    /// <summary>
    /// Get or create the EntityAccessor for the given worker. Called ONCE per chunk at chunk start.
    /// The returned accessor is used directly for all Open/OpenMut calls — zero per-entity overhead.
    /// </summary>
    public EntityAccessor GetWorkerAccessor(int workerId)
    {
        Debug.Assert(!_disposed, "PointInTimeAccessor used after disposal");
        Debug.Assert(IsAttached, "PointInTimeAccessor used before Attach");
        Debug.Assert(workerId >= 0 && workerId < _workerAccessors.Length,
            $"workerId {workerId} out of range [0, {_workerAccessors.Length})");

        var acc = _workerAccessors[workerId];
        if (acc != null)
        {
            return acc;
        }

        acc = new EntityAccessor();
        acc.InitLightweight(_dbe, TSN);
        _workerAccessors[workerId] = acc;
        return acc;
    }

    /// <summary>
    /// Advance to a new MVCC snapshot. Equivalent to <see cref="Attach"/> but reuses the existing engine and worker count.
    /// </summary>
    public void AdvanceSnapshot()
    {
        Debug.Assert(!_disposed, "PointInTimeAccessor used after disposal");
        Debug.Assert(IsAttached, "PointInTimeAccessor.AdvanceSnapshot called before Attach");
        var newTsn = _dbe.TransactionChain.AllocateTSN();
        TSN = newTsn;

        for (var i = 0; i < _workerCount; i++)
        {
            _workerAccessors[i]?.ResetForNewSnapshot(newTsn);
        }
    }

    /// <summary>
    /// Flush the given worker's EntityAccessor and refresh its epoch scope.
    /// Called at the end of each parallel chunk callback on the worker thread.
    /// </summary>
    public void FlushWorker(int workerId)
    {
        Debug.Assert(!_disposed, "PointInTimeAccessor used after disposal");
        if (workerId >= 0 && workerId < _workerCount)
        {
            _workerAccessors[workerId]?.RefreshEpochScope();
        }
    }

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
            for (var i = 0; i < _workerAccessors.Length; i++)
            {
                _workerAccessors[i]?.Dispose();
                _workerAccessors[i] = null;
            }
        }
    }
}
