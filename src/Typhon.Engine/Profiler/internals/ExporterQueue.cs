using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Bounded handoff queue between the profiler consumer thread and one exporter. Wraps a <see cref="BlockingCollection{T}"/> with explicit
/// <b>drop-newest</b> semantics on full, and balances the batch refcount on drop so the pool doesn't leak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="BlockingCollection{T}"/>?</b> It's the BCL primitive that gives us a bounded queue plus the
/// <see cref="GetConsumingEnumerable(System.Threading.CancellationToken)"/> blocking-iterator pattern that the exporter's dedicated thread runs:
/// <code>foreach (var batch in queue.GetConsumingEnumerable(ct)) { exporter.ProcessBatch(batch); batch.Release(); }</code>
/// No <c>async</c>/<c>await</c>, no thread-pool churn, just a sync exporter thread per exporter draining a bounded channel.
/// </para>
/// <para>
/// <b>Drop-newest on full:</b> if <see cref="TryEnqueue"/> finds the queue at capacity, it drops the new batch (does not block the consumer thread)
/// and increments <see cref="DroppedBatches"/>. Critically, it ALSO calls <see cref="TraceRecordBatch.Release"/> on the dropped batch — without this,
/// the batch's refcount would never reach zero and the pool would leak. With it, the refcount stays balanced no matter how many exporter queues drop
/// a given batch on a given drain pass.
/// </para>
/// <para>
/// <b>Why drop the newest, not the oldest?</b> The newest batch hasn't started processing yet — dropping it costs nothing besides the loss of those
/// events, while dropping the oldest would either require a complex rewind or invalidate work the exporter is in the middle of doing. Tracy's design
/// philosophy: "prefer losing a sample to blocking the producer."
/// </para>
/// </remarks>
public sealed class ExporterQueue : IDisposable
{
    private readonly BlockingCollection<TraceRecordBatch> _queue;
    private long _droppedBatches;

    /// <summary>Total batches dropped due to a full queue. Read by diagnostics.</summary>
    public long DroppedBatches => _droppedBatches;

    public ExporterQueue(int boundedCapacity)
    {
        if (boundedCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(boundedCapacity), "must be ≥ 1");
        }
        _queue = new BlockingCollection<TraceRecordBatch>(boundedCapacity);
    }

    /// <summary>
    /// Try to enqueue a batch. Returns <c>true</c> on success. On failure (queue full or completed), increments <see cref="DroppedBatches"/>
    /// and calls <see cref="TraceRecordBatch.Release"/> on the batch to keep the refcount balanced.
    /// </summary>
    /// <remarks>Internal — only the profiler consumer thread enqueues batches.</remarks>
    internal bool TryEnqueue(TraceRecordBatch batch)
    {
        // BlockingCollection.TryAdd is non-blocking when called with no timeout argument — it returns false if the queue is at capacity. BUT it
        // throws InvalidOperationException if the collection has been marked complete-for-adding. We treat both failure modes as a drop, catching
        // the exception so the consumer thread never crashes during shutdown.
        try
        {
            if (_queue.TryAdd(batch))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding has been called — fall through to the drop path
        }

        _droppedBatches++;
        batch.Release();
        return false;
    }

    /// <summary>
    /// Marks the queue as complete-for-adding. The exporter thread's <see cref="GetConsumingEnumerable"/> loop will exit after draining whatever is
    /// already queued. Called by the consumer on shutdown.
    /// </summary>
    /// <remarks>Internal — only <c>TyphonProfiler.Stop</c> signals shutdown.</remarks>
    internal void CompleteAdding() => _queue.CompleteAdding();

    /// <summary>
    /// Returns the blocking iterator that the exporter thread foreaches over. Yields each enqueued batch and exits when
    /// <see cref="CompleteAdding"/> has been called and the queue is drained.
    /// </summary>
    public IEnumerable<TraceRecordBatch> GetConsumingEnumerable(CancellationToken cancellationToken)
        => _queue.GetConsumingEnumerable(cancellationToken);

    public void Dispose() => _queue.Dispose();
}
