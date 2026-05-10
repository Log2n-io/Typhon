using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A pooled, refcounted batch of raw trace records passed from the consumer drain loop to one or more exporters. Replaces the Phase 1/2
/// <c>TraceEventBatch</c> (which held a fixed <c>TraceEvent[]</c>) with a variable-size byte payload plus an offsets index for record walking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout:</b> the consumer drain pass copies a run of size-prefixed records into <see cref="Payload"/>, building <see cref="Offsets"/> alongside
/// so the per-exporter loop can parse records without re-walking size fields. <see cref="PayloadBytes"/> is the number of valid bytes in
/// <c>Payload</c>; <see cref="Count"/> is the number of records in the batch.
/// </para>
/// <para>
/// <b>Lifecycle:</b> rented from <see cref="TraceRecordBatchPool"/> by the consumer thread on every drain pass with a refcount equal to the
/// number of exporters that will receive it. Each exporter calls <see cref="Release"/> after processing; the last release returns the batch to
/// the pool.
/// </para>
/// </remarks>
public sealed class TraceRecordBatch
{
    /// <summary>Backing byte buffer — capacity fixed at <see cref="TraceRecordBatchPool.MaxPayloadBytes"/>. Exporters read this; never write.</summary>
    public readonly byte[] Payload;

    /// <summary>Record-start offsets into <see cref="Payload"/>. Exporters read this; never write.</summary>
    public readonly int[] Offsets;

    /// <summary>Number of valid bytes in <see cref="Payload"/>.</summary>
    public int PayloadBytes { get; internal set; }

    /// <summary>Number of records in the batch (valid entries in <see cref="Offsets"/>).</summary>
    public int Count { get; internal set; }

    private int _refCount;

    internal TraceRecordBatch(int maxPayloadBytes, int maxRecords)
    {
        Payload = new byte[maxPayloadBytes];
        Offsets = new int[maxRecords];
    }

    internal void InitForRent(int exporterCount)
    {
        PayloadBytes = 0;
        Count = 0;
        _refCount = exporterCount;
    }

    /// <summary>
    /// Release one reference. The last release returns the batch to the pool. Called by the engine's exporter-thread loop after each
    /// <see cref="IProfilerExporter.ProcessBatch"/> returns — exporter implementations must NOT call this themselves.
    /// </summary>
    internal void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            TraceRecordBatchPool.Return(this);
        }
    }
}
