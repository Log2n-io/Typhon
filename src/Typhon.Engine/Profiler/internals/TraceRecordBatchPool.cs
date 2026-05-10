using System.Collections.Concurrent;

namespace Typhon.Engine.Internals;

/// <summary>
/// Object pool for <see cref="TraceRecordBatch"/> instances. Pre-allocates a small number and reuses them across drain passes — at steady state
/// the consumer + exporters churn the same handful of batches with zero GC pressure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sizing rationale:</b> <see cref="MaxPayloadBytes"/> is sized to match <see cref="Typhon.Profiler.TraceFileWriter.MaxBlockBytes"/> so a single
/// batch maps directly onto one compressed file block. <see cref="MaxRecords"/> is a soft estimate based on the average minimum-size record
/// (37 B span header) — if a drain pass ever produces more records than fit in <c>Offsets</c>, the consumer seals the current batch and starts a
/// new one.
/// </para>
/// </remarks>
internal static class TraceRecordBatchPool
{
    /// <summary>Maximum bytes per batch — matches <c>TraceFileWriter.MaxBlockBytes</c> so one batch = one compressed block.</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>Maximum records per batch — roughly <see cref="MaxPayloadBytes"/> ÷ 37 B (min span header) rounded up to a power-of-2 array size.</summary>
    public const int MaxRecords = 8192;

    /// <summary>Number of batches pre-allocated at static init. 8 × 256 KB = 2 MB at startup.</summary>
    public const int InitialPoolSize = 8;

    private static readonly ConcurrentBag<TraceRecordBatch> s_pool = new();

    static TraceRecordBatchPool()
    {
        for (var i = 0; i < InitialPoolSize; i++)
        {
            s_pool.Add(new TraceRecordBatch(MaxPayloadBytes, MaxRecords));
        }
    }

    /// <summary>Rent a batch, configuring it with the given exporter count for refcount tracking. Allocates a new batch if the pool is empty.</summary>
    public static TraceRecordBatch Rent(int exporterCount)
    {
        if (!s_pool.TryTake(out var batch))
        {
            batch = new TraceRecordBatch(MaxPayloadBytes, MaxRecords);
        }
        batch.InitForRent(exporterCount);
        return batch;
    }

    /// <summary>Return a batch to the pool. Called by <see cref="TraceRecordBatch.Release"/> when the refcount hits zero.</summary>
    public static void Return(TraceRecordBatch batch)
    {
        s_pool.Add(batch);
    }

    /// <summary>Approximate pool size for diagnostics. Not authoritative under concurrent access.</summary>
    public static int ApproximateSize => s_pool.Count;
}
