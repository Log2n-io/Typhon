using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>What the index rebuild's workers need from the engine (#1588, design §9): their scopes, their ChangeSets, the open's batch point.</summary>
internal sealed class RebuildEnvironment
{
    public EpochManager Epochs;
    public Func<ChangeSet> CreateChangeSet;

    /// <summary>The open's batch point (CK-08), with the calling worker's ChangeSet and pass.</summary>
    public Action<ChangeSet, DatabaseEngine.RecoveryPass> BatchPoint;

    /// <summary>Told of every worker failure but the first, which the pass rethrows.</summary>
    public Action<Exception> OtherFailure;

    /// <summary>The worker threads each phase may use, at least 1.</summary>
    public int Workers = 1;
}

/// <summary>One worker thread of a rebuild phase: its index, its ChangeSet, and its batch point.</summary>
internal sealed class RebuildWorker
{
    private readonly RebuildWorkers _owner;
    private readonly DatabaseEngine.RecoveryPass _pass;

    internal RebuildWorker(RebuildWorkers owner, int index, ChangeSet changeSet, DatabaseEngine.RecoveryPass pass)
    {
        _owner = owner;
        Index = index;
        ChangeSet = changeSet;
        _pass = pass;
    }

    public int Index { get; }

    /// <summary>
    /// The worker's own ChangeSet (PS-05a): released at each batch point; once every worker of the phase has exited, saved by the coordinator when none
    /// failed, released otherwise (<see cref="RebuildWorkers.Run"/>).
    /// </summary>
    public ChangeSet ChangeSet { get; }

    /// <summary>Batch points this worker made.</summary>
    public int BatchPoints { get; private set; }

    /// <summary>
    /// The open's batch point on this worker's thread, scope and ChangeSet (CK-08), after its accessors are committed; stops the worker once another has
    /// failed.
    /// </summary>
    public void BatchPoint()
    {
        _owner.ThrowIfAborted();
        try
        {
            _owner.Environment.BatchPoint?.Invoke(ChangeSet, _pass);
        }
        catch (Exception) when (_owner.SignalAbort())
        {
            // Never entered: the filter raises the abort while the exception is still being dispatched, before any finally below it runs (the probe's
            // lock among them), so the other workers stop at their next batch point rather than after this one has unwound its whole build.
        }

        BatchPoints++;
    }

    /// <summary>Stops the worker once another has failed: between work items, where it holds nothing.</summary>
    public void ThrowIfAborted() => _owner.ThrowIfAborted();
}

/// <summary>
/// Runs one phase of an index rebuild on dedicated threads (#1588, design §D8, §9, RB-09): each enters and exits its own epoch scope on its own thread
/// (EP-04) and owns its ChangeSet (PS-05a); the coordinator, which must hold no scope while they run, joins them all. The first failure stops the others
/// at their next batch point or work item and is rethrown once every thread has exited; the others are reported to
/// <see cref="RebuildEnvironment.OtherFailure"/>.
/// </summary>
/// <remarks>
/// Dedicated threads rather than the pool: a pool thread would keep its <c>[ThreadStatic]</c> warm accessors, and their page pins, alive past the open. A
/// dead worker's epoch slot is reclaimed by the next thread that claims one, so a thread per worker per phase never exhausts the registry.
/// </remarks>
internal sealed class RebuildWorkers
{
    private Exception _first;
    private readonly List<Exception> _others = [];
    private int _aborted;

    private RebuildWorkers(RebuildEnvironment environment) => Environment = environment;

    internal RebuildEnvironment Environment { get; }

    /// <summary>How often the coordinator, waiting on its workers, reports their progress: a log cadence, not a tunable (design §12).</summary>
    internal static readonly TimeSpan ProgressCadence = TimeSpan.FromSeconds(5);

    /// <summary>Test hook: when positive, the progress cadence instead of <see cref="ProgressCadence"/>.</summary>
    internal static TimeSpan ProgressCadenceForTest { get; set; }

    /// <summary>Worker threads alive now, every rebuild included. Test oracle.</summary>
    internal static int LiveThreadsForTest => Volatile.Read(ref s_liveThreads);

    private static int s_liveThreads;

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="count"/> threads, worker <c>i</c> as <c>body(worker i)</c>; returns the batch points they made.
    /// Throws the first failure once all have exited. <paramref name="progress"/>, if any, runs on the calling thread every
    /// <see cref="ProgressCadence"/> it spends waiting.
    /// </summary>
    public static int Run(RebuildEnvironment environment, DatabaseEngine.RecoveryPass pass, int count, Action<RebuildWorker> body, Action progress = null)
    {
        // Its pin would be the minimum every worker's pages wait behind: none of them could be evicted (RB-09, design §9.3).
        if (environment.Epochs.CurrentThreadDepth != 0)
        {
            ThrowHelper.ThrowInvalidOp("Index rebuild: the coordinating thread holds an epoch scope; every page its workers touch would stay held.");
        }

        var run = new RebuildWorkers(environment);
        var workers = new RebuildWorker[count];
        var threads = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            var index = i;
            threads[i] = new Thread(() => run.WorkerMain(index, pass, body, workers)) { IsBackground = true, Name = $"Typhon.IndexRebuild.{pass}.{i}" };
        }

        var started = 0;
        try
        {
            for (; started < count; started++)
            {
                Interlocked.Increment(ref s_liveThreads);
                threads[started].Start();
            }
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref s_liveThreads);   // the thread that failed to start
            run.Fail(ex);
        }

        var cadence = ProgressCadenceForTest > TimeSpan.Zero ? ProgressCadenceForTest : ProgressCadence;
        for (var i = 0; i < started; i++)
        {
            while (!threads[i].Join(cadence))
            {
                try
                {
                    progress?.Invoke();
                }
                catch (Exception ex)
                {
                    // A report that throws neither fails the open nor leaves the workers unjoined: logged as another failure, not called again.
                    lock (run._others)
                    {
                        run._others.Add(ex);
                    }

                    progress = null;
                }
            }
        }

        // The workers' ChangeSets are saved here, after the join, never by a worker: a save writes its pages from live memory, its checksums stamped first
        // (PagedMMF.SavePages), and the workers of a phase write the same pages — the trees of an archetype share their index segment and its directory
        // chunk. A worker that saved while another still wrote put a page on disk whose bytes did not match its checksum: the next open failed its
        // CRC (MigrationSegmentReclaimTests, under load). Saved in a scope of the coordinator's own, while no worker runs.
        var depth = -1;
        try
        {
            depth = environment.Epochs.EnterScope();
        }
        catch (Exception ex)
        {
            run.Fail(ex);   // no scope to save in: every ChangeSet below is released instead, which needs none
        }

        try
        {
            foreach (var w in workers)
            {
                var changeSet = w?.ChangeSet;
                if (changeSet == null)
                {
                    continue;
                }

                try
                {
                    if (run._first == null)
                    {
                        changeSet.SaveChanges();
                    }
                    else
                    {
                        changeSet.ReleaseDirtyMarks();   // a failed pass gives its marks back (CK-08); the open fails and the next one rebuilds
                    }
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                }
            }
        }
        finally
        {
            if (depth >= 0)
            {
                environment.Epochs.ExitScope(depth);
            }
        }

        foreach (var other in run._others)
        {
            environment.OtherFailure?.Invoke(other);
        }

        if (run._first != null)
        {
            ExceptionDispatchInfo.Capture(run._first).Throw();
        }

        var batchPoints = 0;
        foreach (var w in workers)
        {
            batchPoints += w.BatchPoints;
        }

        return batchPoints;
    }

    /// <summary>Raises the abort; returns false, for an exception filter that must not catch.</summary>
    internal bool SignalAbort()
    {
        Volatile.Write(ref _aborted, 1);
        return false;
    }

    internal void ThrowIfAborted()
    {
        if (Volatile.Read(ref _aborted) != 0)
        {
            throw new OperationCanceledException("Index rebuild: another worker failed.");
        }
    }

    private void WorkerMain(int index, DatabaseEngine.RecoveryPass pass, Action<RebuildWorker> body, RebuildWorker[] workers)
    {
        try
        {
            var depth = Environment.Epochs.EnterScope();
            try
            {
                var worker = workers[index] = new RebuildWorker(this, index, Environment.CreateChangeSet(), pass);
                body(worker);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _aborted) != 0)
            {
                // Stopped by another worker's failure, which is the one reported.
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                Environment.Epochs.ExitScope(depth);   // its ChangeSet is saved or released by the coordinator, once every worker has exited
            }
        }
        catch (Exception ex)
        {
            Fail(ex);   // the scope itself failed
        }
        finally
        {
            workers[index] ??= new RebuildWorker(this, index, null, pass);
            Interlocked.Decrement(ref s_liveThreads);
        }
    }

    private void Fail(Exception ex)
    {
        lock (_others)
        {
            if (_first == null)
            {
                _first = ex;
            }
            else
            {
                _others.Add(ex);
            }
        }

        Volatile.Write(ref _aborted, 1);
    }
}
