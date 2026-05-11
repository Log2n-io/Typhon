using System;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Background worker thread that drains <see cref="GcEventQueue"/> and emits records via the profiler's typed-event pipeline. Owns exactly one
/// <see cref="ThreadSlot"/> — preserving the per-thread-slot SPSC invariant of <see cref="TraceRecordRing"/> without further synchronization.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this thread exists:</b> GC events are delivered by the CLR on its own internal threads. Writing to a <see cref="ThreadSlot"/> ring
/// from those threads would either (a) steal random engine-thread slots (polluting their SPSC invariant) or (b) require a dedicated slot with
/// heavy cross-thread synchronization. The cleaner solution is to copy the event into a lock-bounded queue on the callback thread, wake this
/// thread, and let it write into its own slot. CLR GC threads are never blocked on our pipeline.
/// </para>
/// <para>
/// <b>Suspension window pairing:</b> <see cref="TraceEventKind.GcSuspension"/> spans from <c>GCSuspendEEBegin</c> to <c>GCRestartEEEnd</c>, but
/// those events are separated by the GC itself plus a <c>GCEnd</c> in the middle. We buffer the start timestamp + reason in per-thread state
/// (<c>_suspensionOpen</c>) and emit the complete span record only on <c>RestartEnd</c>. If a Stop() happens mid-suspension, the half-open window
/// is dropped — acceptable at shutdown.
/// </para>
/// </remarks>
internal sealed class GcIngestionThread
{
    private readonly GcEventQueue _queue;
    private readonly AutoResetEvent _wake;
    private readonly CancellationTokenSource _cts = new();
    private Thread _thread;
    private byte _slot;
    private long _processedEvents;

    // Suspension window state (mutated only by the drain loop — no synchronization needed).
    private long _suspensionStartTs;
    private byte _suspensionReason;
    private bool _suspensionOpen;

    public GcIngestionThread(GcEventQueue queue, AutoResetEvent wake)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(wake);
        _queue = queue;
        _wake = wake;
    }

    /// <summary>Assigned slot index. Valid after <see cref="Start"/>.</summary>
    public byte Slot => _slot;

    /// <summary>Total records dropped due to queue overflow (pass-through from <see cref="GcEventQueue"/>).</summary>
    public long DroppedEvents => _queue.DroppedEvents;

    /// <summary>Running count of records dequeued and processed by the drain loop. Tests poll this for drain-completion. Read-only for observers.</summary>
    public long ProcessedEvents => _processedEvents;

    public void Start()
    {
        if (_thread != null)
        {
            throw new InvalidOperationException("GcIngestionThread already started.");
        }
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TyphonProfilerGcIngest",
        };
        _thread.Start();
        // Wait for the ingestion thread to claim its own slot. MUST happen on the ingestion thread itself — if we called
        // GetOrAssignSlot here on the caller's thread, we'd end up sharing that thread's slot (likely slot 0, the main
        // thread's), violating the per-slot SPSC invariant of TraceRecordRing and interleaving ingestion-thread writes
        // with main-thread writes in the same ring. The symptom of that violation is a consumer Drain reading a size
        // field that doesn't match any record boundary it expects, breaking defensively and stranding the ring for the
        // rest of the session. Blocking here until _slotReady is trivial — the ingestion thread only needs microseconds
        // to run GetOrAssignSlot on its own TLS.
        _slotReady.Wait(TimeSpan.FromSeconds(5));
    }

    private readonly ManualResetEventSlim _slotReady = new(false);

    public void Stop()
    {
        if (_thread == null)
        {
            return;
        }
        _cts.Cancel();
        _wake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Best-effort — the thread is cooperative and will exit on the next wake.
        }
        _thread = null;
        _cts.Dispose();
    }

    private void Run()
    {
        try
        {
            // Claim the slot from THIS thread so [ThreadStatic] TLS is the ingestion thread's — this thread now owns a
            // dedicated slot and is the sole producer of its ring, preserving the SPSC invariant that TraceRecordRing
            // relies on.
            var idx = ThreadSlotRegistry.GetOrAssignSlot();
            if (idx < 0)
            {
                // Registry full — nothing we can do; skip processing. Consumers will just see zero GC records.
                _slotReady.Set();
                return;
            }
            _slot = (byte)idx;
            _slotReady.Set();

            while (!_cts.IsCancellationRequested)
            {
                _wake.WaitOne(TimeSpan.FromMilliseconds(100));
                DrainQueue();
            }
            DrainQueue();
        }
        catch
        {
            _slotReady.Set();
            // Swallow — the ingestion thread must never crash the process on transient errors. Future: log via [LoggerMessage].
        }
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var record))
        {
            ProcessOne(in record);
            Interlocked.Increment(ref _processedEvents);
        }
    }

    private void ProcessOne(in GcEventRecord record)
    {
        switch (record.Kind)
        {
            case GcEventRecordKind.GcStart:
                TyphonEvent.EmitGcStart(_slot, record.Timestamp,
                    record.Generation, (GcReason)record.Reason, (GcType)record.Type, record.Count);
                break;

            case GcEventRecordKind.GcEnd:
                var info = GC.GetGCMemoryInfo();
                long pauseTicks = 0;
                if (info.PauseDurations.Length > 0)
                {
                    pauseTicks = info.PauseDurations[0].Ticks;
                }

                var genInfo = info.GenerationInfo;
                ulong gen0 = genInfo.Length > 0 ? (ulong)genInfo[0].SizeAfterBytes : 0UL;
                ulong gen1 = genInfo.Length > 1 ? (ulong)genInfo[1].SizeAfterBytes : 0UL;
                ulong gen2 = genInfo.Length > 2 ? (ulong)genInfo[2].SizeAfterBytes : 0UL;
                ulong loh  = genInfo.Length > 3 ? (ulong)genInfo[3].SizeAfterBytes : 0UL;
                ulong poh  = genInfo.Length > 4 ? (ulong)genInfo[4].SizeAfterBytes : 0UL;

                TyphonEvent.EmitGcEnd(_slot, record.Timestamp,
                    record.Generation, record.Count,
                    pauseTicks,
                    (ulong)info.PromotedBytes,
                    gen0, gen1, gen2, loh, poh,
                    (ulong)info.TotalCommittedBytes);
                break;

            case GcEventRecordKind.SuspendBegin:
                _suspensionStartTs = record.Timestamp;
                _suspensionReason = record.Reason;
                _suspensionOpen = true;
                break;

            case GcEventRecordKind.RestartEnd:
                if (_suspensionOpen)
                {
                    TyphonEvent.EmitGcSuspension(_slot, _suspensionStartTs, record.Timestamp, (GcSuspendReason)_suspensionReason);
                    _suspensionOpen = false;
                }
                break;
        }
    }
}
