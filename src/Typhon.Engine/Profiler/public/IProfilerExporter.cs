using System;

namespace Typhon.Engine;

/// <summary>
/// Sink for batches of trace records drained by the profiler consumer thread. Implementations receive batches on their own dedicated thread
/// (one OS thread per attached exporter, owned by <c>TyphonProfiler</c>) and write them to a file, TCP socket, OTLP collector, in-memory list,
/// etc.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle:</b>
/// <list type="number">
///   <item><c>TyphonProfiler.AttachExporter(this)</c> registers the exporter — does NOT start its thread yet.</item>
///   <item><c>TyphonProfiler.Start()</c> calls <see cref="Initialize"/> with session metadata, then spawns the dedicated exporter thread that
///         iterates <see cref="Queue"/>'s blocking enumerator.</item>
///   <item>The exporter thread receives batches via <see cref="Queue"/> and calls <see cref="ProcessBatch"/> for each.
///         After processing, the thread calls <c>batch.Release()</c> to decrement the refcount and return the batch to the pool.</item>
///   <item><c>TyphonProfiler.Stop()</c> calls <see cref="Queue"/>.<c>CompleteAdding</c> so the exporter thread's foreach loop exits after draining
///         what's already queued, then calls <see cref="Flush"/> + <see cref="IDisposable.Dispose"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Threading contract:</b> <see cref="Initialize"/> runs on the caller of <c>TyphonProfiler.Start</c> before any batches arrive.
/// <see cref="ProcessBatch"/> and <see cref="Flush"/> are called from the dedicated exporter thread only — implementations don't need internal
/// synchronization on per-exporter state.
/// </para>
/// </remarks>
public interface IProfilerExporter : IDisposable
{
    /// <summary>Human-readable name for diagnostics and resource-tree display.</summary>
    string Name { get; }

    /// <summary>The bounded handoff queue this exporter consumes from. Created by the implementation in its constructor.</summary>
    ExporterQueue Queue { get; }

    /// <summary>Called once at session start, before any <see cref="ProcessBatch"/> calls. Use it to write headers, open files, accept clients, etc.</summary>
    void Initialize(ProfilerSessionMetadata metadata);

    /// <summary>
    /// Process one batch of raw trace records. Called from the dedicated exporter thread. The exporter reads <see cref="TraceRecordBatch.Payload"/>
    /// and <see cref="TraceRecordBatch.Offsets"/> but must NOT call <c>batch.Release()</c> — the caller owns that.
    /// </summary>
    void ProcessBatch(TraceRecordBatch batch);

    /// <summary>Called once at shutdown after the queue is drained. Flushes any pending state.</summary>
    void Flush();
}
