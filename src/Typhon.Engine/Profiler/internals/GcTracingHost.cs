using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Ownership umbrella for the three components that together implement opt-in .NET runtime GC tracing: <see cref="GcEventQueue"/>,
/// <see cref="GcEventListener"/>, and <see cref="GcIngestionThread"/>. Instantiated and torn down by <c>TyphonProfiler.Start</c>/<c>Stop</c>
/// only when <see cref="TelemetryConfig.ProfilerGcTracingActive"/> is <c>true</c> — the host never exists otherwise, so neither the EventListener
/// subscription nor the ingestion thread are ever paid for on an opt-out session.
/// </summary>
internal sealed class GcTracingHost : IDisposable
{
    /// <summary>Default queue capacity — 256 slots. Holds a couple of seconds of worst-case GC storm events without dropping.</summary>
    private const int DefaultQueueCapacity = 256;

    private readonly AutoResetEvent _wake;
    private readonly GcEventQueue _queue;
    private readonly GcIngestionThread _ingest;
    private GcEventListener _listener;
    private bool _started;

    public GcTracingHost(int queueCapacity = DefaultQueueCapacity)
    {
        _wake = new AutoResetEvent(false);
        _queue = new GcEventQueue(queueCapacity, _wake);
        _ingest = new GcIngestionThread(_queue, _wake);
    }

    /// <summary>Slot index owned by the ingestion thread. Valid after <see cref="Start"/>.</summary>
    public byte Slot => _ingest.Slot;

    /// <summary>Total GC events dropped due to queue overflow since <see cref="Start"/>.</summary>
    public long DroppedEvents => _ingest.DroppedEvents;

    /// <summary>Total GC events successfully dequeued and processed by the ingestion thread.</summary>
    public long ProcessedEvents => _ingest.ProcessedEvents;

    /// <summary>Internal — test access to the queue for in-process drain verification without a real GC.</summary>
    internal GcEventQueue QueueForTests => _queue;

    /// <summary>
    /// Spin up the ingestion thread first (so it owns its slot and is ready to drain), then attach the <see cref="GcEventListener"/>.
    /// Ordering matters: the listener could begin receiving callbacks before the ctor returns (see <see cref="GcEventListener"/> remarks), and
    /// the ingestion thread must be running before those callbacks land records in the queue — otherwise the queue fills without a consumer
    /// and GC storm during profiler startup would drop events unnecessarily.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("GcTracingHost already started.");
        }
        _ingest.Start();
        _listener = new GcEventListener(_queue);
        _started = true;
    }

    /// <summary>
    /// Tear down in reverse order: detach listener (CLR stops delivering events), then stop the ingestion thread (drains remaining queue, joins).
    /// </summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            _listener?.Dispose();
        }
        catch
        {
            // swallow — we're shutting down
        }
        _listener = null;
        _ingest.Stop();
        _started = false;
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
