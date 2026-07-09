using System;

namespace Typhon.Engine;

/// <summary>
/// Tunable parameters for <c>TyphonProfiler.Start</c>. All defaults are tuned for typical Typhon workloads (~30 producer threads, ~3 exporters).
/// </summary>
public sealed class ProfilerOptions
{
    /// <summary>
    /// Cadence at which the consumer thread wakes to drain all slot rings and fan out to exporters. Default: 1 ms.
    /// Lower = lower end-to-end latency for live viewers, higher CPU. Higher = batched I/O efficiency, more records buffered.
    /// </summary>
    public TimeSpan ConsumerCadence { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Bounded queue depth between the consumer thread and each exporter. Default: 4.
    /// When an exporter is slow, this many cadence ticks of batches can buffer before drop-newest kicks in.
    /// </summary>
    public int PerExporterChannelDepth { get; set; } = 4;

    /// <summary>
    /// Capacity of the consumer's per-pass merge scratch buffer in <b>bytes</b>. Drains from all slots accumulate here before sorting and slicing into
    /// <see cref="TraceRecordBatch"/>es. Default: 512 KB — large enough that a single drain pass absorbs a typical burst without leaving bytes in the
    /// producer rings for a subsequent pass. Must be at least <see cref="TraceRecordBatchPool.MaxPayloadBytes"/> (enforced by <see cref="Validate"/>).
    /// Under the observability "better to over-buffer than drop" priority, raise this when a workload shows records carrying over between drain passes.
    /// </summary>
    public int MergeBufferBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// Number of pre-allocated <b>spillover</b> ring buffers held in the <see cref="SpilloverRingPool"/>. When a thread's
    /// primary 1 MiB ring overflows, the producer pops a spillover from the pool and chains it onto the slot, continuing
    /// to write there. The consumer drains the chain in FIFO order and recycles each spillover back to the pool when
    /// it's emptied. Default: 8 (covers AntHill's ~11 MiB bulk-spawn burst with 7× headroom and supports concurrent
    /// bursts on multiple workers). Set to <c>0</c> to disable spillover entirely (matches pre-spillover behavior:
    /// producers drop on primary overflow).
    /// </summary>
    public int SpilloverBufferCount { get; set; } = 8;

    /// <summary>
    /// Size of each spillover buffer in bytes. Must be a power of two and at least 64 KiB. Default: 16 MiB — sized so
    /// one spillover absorbs a 200K-entity bulk-spawn burst (~11 MiB at 56 B per <c>EcsSpawn</c> record). Total
    /// per-process spillover memory at default config: <c>SpilloverBufferCount × SpilloverBufferSizeBytes</c> = 128 MiB,
    /// allocated only while the profiler is running.
    /// </summary>
    public int SpilloverBufferSizeBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Validates options and throws if any field is invalid.</summary>
    public void Validate()
    {
        if (ConsumerCadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConsumerCadence), "must be > 0");
        }
        if (PerExporterChannelDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PerExporterChannelDepth), "must be ≥ 1");
        }
        if (MergeBufferBytes < TraceRecordBatchPool.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MergeBufferBytes), $"must be ≥ TraceRecordBatchPool.MaxPayloadBytes ({TraceRecordBatchPool.MaxPayloadBytes})");
        }
        if (SpilloverBufferCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SpilloverBufferCount), "must be ≥ 0 (use 0 to disable spillover)");
        }
        if (SpilloverBufferCount > 0)
        {
            if (SpilloverBufferSizeBytes < 64 * 1024 || (SpilloverBufferSizeBytes & (SpilloverBufferSizeBytes - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(SpilloverBufferSizeBytes), "must be a power of two and ≥ 64 KiB");
            }
        }
    }
}
