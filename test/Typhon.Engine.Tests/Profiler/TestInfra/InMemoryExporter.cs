using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Tests.Profiler.TestInfra;

/// <summary>
/// Test-only <see cref="IProfilerExporter"/> that copies every received batch's raw record bytes into a thread-safe list so test assertions
/// can inspect what flowed through the consumer drain pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage shape:</b> each received batch produces one <see cref="BatchSnapshot"/> entry containing a copy of the batch's record bytes plus
/// the offsets index. Tests walk records by iterating <see cref="BatchSnapshot.Offsets"/> and reading the u16 size at each offset.
/// </para>
/// </remarks>
internal sealed class InMemoryExporter : IProfilerExporter
{
    private readonly List<BatchSnapshot> _batches = new();
    private readonly object _lock = new();
    private ManualResetEventSlim _wakeOnNew;

    public InMemoryExporter(string name = "InMemoryExporter", int queueCapacity = 4)
    {
        Name = name;
        Queue = new ExporterQueue(queueCapacity);
    }

    public string Name { get; }
    public ExporterQueue Queue { get; }

    public void Initialize(ProfilerSessionMetadata metadata) { }

    public void ProcessBatch(TraceRecordBatch batch)
    {
        lock (_lock)
        {
            var payload = new byte[batch.PayloadBytes];
            batch.Payload.AsSpan(0, batch.PayloadBytes).CopyTo(payload);
            var offsets = new int[batch.Count];
            Array.Copy(batch.Offsets, offsets, batch.Count);
            _batches.Add(new BatchSnapshot(payload, offsets));
            _wakeOnNew?.Set();
        }
    }

    public void Flush() { }

    public void Dispose()
    {
        Queue.Dispose();
        _wakeOnNew?.Dispose();
    }

    /// <summary>Snapshot of all received batches, in arrival order.</summary>
    public IReadOnlyList<BatchSnapshot> Batches
    {
        get
        {
            lock (_lock)
            {
                return _batches.ToArray();
            }
        }
    }

    /// <summary>Total records received so far across all batches.</summary>
    public int RecordCount
    {
        get
        {
            lock (_lock)
            {
                var total = 0;
                foreach (var b in _batches) total += b.Offsets.Length;
                return total;
            }
        }
    }

    /// <summary>Block until at least <paramref name="targetRecordCount"/> records have been received, or timeout.</summary>
    public bool WaitForRecords(int targetRecordCount, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_lock)
        {
            _wakeOnNew ??= new ManualResetEventSlim(false);
        }

        while (RecordCount < targetRecordCount)
        {
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) return RecordCount >= targetRecordCount;
            _wakeOnNew.Reset();
            if (RecordCount >= targetRecordCount) return true;
            _wakeOnNew.Wait(remaining);
        }
        return true;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _batches.Clear();
        }
    }

    /// <summary>Immutable per-batch snapshot the exporter keeps for test inspection.</summary>
    public sealed class BatchSnapshot
    {
        public byte[] Payload { get; }
        public int[] Offsets { get; }

        public BatchSnapshot(byte[] payload, int[] offsets)
        {
            Payload = payload;
            Offsets = offsets;
        }
    }
}
