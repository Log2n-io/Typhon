#pragma warning disable 1591   // as PagedMMF.cs: a public type of the internal namespace (Phase 2b entanglement)

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>A segment whose frees wait for a checkpoint before reuse (<see cref="FreeQuarantine"/>).</summary>
internal interface IQuarantinedFrees
{
    /// <summary>A checkpoint cycle starts: the frees made so far wait for it.</summary>
    void SealFrees();

    /// <summary>A cycle wrote every page it collected: the frees sealed at its start may be freed. True when there are some.</summary>
    bool MarkReleasable();

    /// <summary>Frees the frees <see cref="MarkReleasable"/> made releasable, in the segment's bitmap.</summary>
    void ReleaseSealed();

    /// <summary>Frees every held free, sealed or not — the close, or a test that counts chunks after a free.</summary>
    void ReleaseAll();
}

public partial class PagedMMF
{
    private readonly List<IQuarantinedFrees> _quarantinedSegments = [];
    private volatile bool _freeQuarantineEnabled;
    private long _quarantinedBytes;
    private long _quarantinePeakBytes;
    private long _quarantineCheckpointRequests;
    private int _quarantineRequestPending;
    private long _quarantineReleaseFailures;
    private long _quarantineBudgetBytes = long.MaxValue;

    // The thread that frees what a covered cycle made releasable. Off the checkpoint thread: a free touches its chunk's page, and a cache full of dirty
    // pages has a slot for it only once the checkpoint has written some — on the checkpoint thread, the release would wait for itself (CK-14).
    private readonly object _releaseLock = new();
    private readonly AutoResetEvent _releaseSignal = new(false);
    private Thread _releaseWorker;
    private volatile bool _releaseStop;

    /// <summary>Whether quarantined segments hold their frees back (<see cref="FreeQuarantine"/>): from the end of the open on (#1251).</summary>
    internal bool FreeQuarantineEnabled => _freeQuarantineEnabled;

    /// <summary>
    /// Quarantined segments hold their frees back from here: from the start of a crash open's log replay — recovery's own frees, its scrub's and its
    /// sweep's, wait for a checkpoint like any other, so a seal that does not complete leaves them held — or from the end of a clean open.
    /// </summary>
    internal void EnableFreeQuarantine()
    {
        lock (_releaseLock)
        {
            if (_releaseWorker == null)
            {
                _releaseStop = false;
                _releaseWorker = new Thread(ReleaseLoop) { IsBackground = true, Name = "Typhon free quarantine" };
                _releaseWorker.Start();
            }
        }

        _quarantineBudgetBytes = FreeQuarantineBudgetBytes;
        _freeQuarantineEnabled = true;
    }

    /// <summary>
    /// The engine is closing, before its final checkpoint and on the closing thread — the checkpoint thread still writes what the frees' pages need: with
    /// <paramref name="release"/>, every held free is freed, and any free from here on is immediate. Nothing will reuse them, and the final cycle writes
    /// them freed; held after it, they would stay allocated in the data file for good. Without — a crash being simulated — only the worker stops.
    /// </summary>
    internal void EndFreeQuarantine(bool release)
    {
        _freeQuarantineEnabled = false;
        StopReleaseWorker();
        if (release)
        {
            ReleaseHeldFreesNow();
        }
    }

    /// <summary>Every held free, freed now on this thread — the close's, and a test's that counts allocated chunks after a free.</summary>
    internal void ReleaseHeldFreesNow()
    {
        Exception failure = null;
        lock (_releaseLock)
        {
            foreach (var segment in QuarantinedSegments())
            {
                try
                {
                    segment.ReleaseAll();
                }
                catch (Exception e)
                {
                    failure ??= e;
                }
            }
        }

        Volatile.Write(ref _quarantineRequestPending, 0);
        if (failure != null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Frees, on this thread, what covered cycles made releasable and is not freed yet; returns once what the worker is freeing is freed too.</summary>
    internal void DrainReleases()
    {
        // Segment by segment: one whose release throws leaves the others' to run — stopped, they stayed releasable, a state no seal advances (review).
        Exception failure = null;
        lock (_releaseLock)
        {
            foreach (var segment in QuarantinedSegments())
            {
                try
                {
                    segment.ReleaseSealed();
                }
                catch (Exception e)
                {
                    failure ??= e;
                }
            }
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    /// Test seam: the lock every release runs under. A test that holds it while it makes frees releasable and drains them frees them on its own thread:
    /// the worker, signaled by an earlier cycle, waits for the lock and then finds nothing releasable.
    /// </summary>
    internal object ReleaseLockForTests => _releaseLock;

    /// <summary>Releases the worker could not complete — retried at the next covered cycle. Diagnostic.</summary>
    internal long QuarantineReleaseFailures => Interlocked.Read(ref _quarantineReleaseFailures);

    private void ReleaseLoop()
    {
        while (!_releaseStop)
        {
            _releaseSignal.WaitOne();
            try
            {
                DrainReleases();
            }
            catch (Exception)
            {
                // A free that cannot load its page (back-pressure past its deadline): the rest of the generation stays held and releasable, for the next
                // covered cycle's signal to retry — never a chunk with two owners.
                Interlocked.Increment(ref _quarantineReleaseFailures);
            }
        }
    }

    private void StopReleaseWorker()
    {
        Thread worker;
        lock (_releaseLock)
        {
            worker = _releaseWorker;
            _releaseWorker = null;
        }

        if (worker != null)
        {
            _releaseStop = true;
            _releaseSignal.Set();
            worker.Join();
        }
    }

    /// <summary>
    /// Bytes of held frees past which the quarantine asks for a checkpoint (<see cref="FreeQuarantineFull"/>): <see cref="PagedMMFOptions.FreeQuarantineCacheFraction"/>
    /// of the page cache.
    /// </summary>
    internal long FreeQuarantineBudgetBytes => (long)(Options.DatabaseCacheSize * Math.Clamp(Options.FreeQuarantineCacheFraction, 0.0, 1.0));

    /// <summary>Sets the budget <see cref="NoteQuarantined"/> asks a checkpoint past, in place of the cache fraction's.</summary>
    internal void SetFreeQuarantineBudgetForTest(long bytes) => _quarantineBudgetBytes = bytes;

    /// <summary>Asks for a checkpoint: the held frees passed <see cref="FreeQuarantineBudgetBytes"/>. Set by the engine; null asks nothing.</summary>
    internal Action FreeQuarantineFull { get; set; }

    /// <summary>Bytes of frees held back from reuse now. Diagnostic.</summary>
    internal long QuarantinedBytes => Interlocked.Read(ref _quarantinedBytes);

    /// <summary>The most bytes of frees held at once since the engine opened. Diagnostic.</summary>
    internal long QuarantinePeakBytes => Interlocked.Read(ref _quarantinePeakBytes);

    /// <summary>Checkpoints the quarantine asked for, its held frees past the budget. Diagnostic.</summary>
    internal long QuarantineCheckpointRequests => Interlocked.Read(ref _quarantineCheckpointRequests);

    internal void RegisterFreeQuarantine(IQuarantinedFrees segment)
    {
        lock (_quarantinedSegments)
        {
            _quarantinedSegments.Add(segment);
        }
    }

    internal void UnregisterFreeQuarantine(IQuarantinedFrees segment)
    {
        lock (_quarantinedSegments)
        {
            _quarantinedSegments.Remove(segment);
        }
    }

    /// <summary>A free of <paramref name="bytes"/> is held: past the budget, a checkpoint is asked for — once, until a cycle releases what it can.</summary>
    internal void NoteQuarantined(int bytes)
    {
        // One shared write per free: the peak is sampled at each seal, and the request flag read plainly before its exchange.
        var now = Interlocked.Add(ref _quarantinedBytes, bytes);
        if (now > _quarantineBudgetBytes && Volatile.Read(ref _quarantineRequestPending) == 0
            && Interlocked.CompareExchange(ref _quarantineRequestPending, 1, 0) == 0)
        {
            Interlocked.Increment(ref _quarantineCheckpointRequests);
            FreeQuarantineFull?.Invoke();
        }
    }

    /// <summary>Held frees of <paramref name="bytes"/> were freed: a later free past the budget asks for a checkpoint again.</summary>
    internal void NoteReleased(long bytes)
    {
        Interlocked.Add(ref _quarantinedBytes, -bytes);
        Volatile.Write(ref _quarantineRequestPending, 0);
    }

    /// <summary>A checkpoint cycle starts, before it collects its dirty pages: the frees made so far wait for it.</summary>
    internal void SealQuarantinedFrees()
    {
        var held = Interlocked.Read(ref _quarantinedBytes);
        if (held > Interlocked.Read(ref _quarantinePeakBytes))
        {
            Interlocked.Exchange(ref _quarantinePeakBytes, held);
        }

        foreach (var segment in QuarantinedSegments())
        {
            segment.SealFrees();
        }
    }

    /// <summary>
    /// A cycle wrote every page it collected: the frees sealed at its start are safe to reuse — the pages that stopped naming them are on disk. Handed to
    /// the release worker, which frees them off the checkpoint thread.
    /// </summary>
    internal void ReleaseQuarantinedFrees()
    {
        var any = false;
        foreach (var segment in QuarantinedSegments())
        {
            any |= segment.MarkReleasable();
        }

        if (any)
        {
            _releaseSignal.Set();
        }
    }

    private IQuarantinedFrees[] QuarantinedSegments()
    {
        lock (_quarantinedSegments)
        {
            return _quarantinedSegments.Count == 0 ? [] : [.. _quarantinedSegments];
        }
    }
}
