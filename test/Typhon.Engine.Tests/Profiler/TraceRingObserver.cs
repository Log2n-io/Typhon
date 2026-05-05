using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Test helper — an <see cref="IProfilerExporter"/> that captures every record it receives into per-kind
/// counters and (optionally) a list of raw record bytes, for assertion in tests. Replaces the pre-#280
/// <c>MockContentionTarget</c> pattern with a real trace-ring observation pathway.
/// </summary>
/// <remarks>
/// <para>
/// Usage from a test fixture (must be on a process where the relevant Tier-2 flags are enabled — see
/// <see cref="ConcurrencyTracingStressTests"/> for the env-var setup):
/// </para>
/// <code>
/// using var observer = new TraceRingObserver();
/// TyphonProfiler.AttachExporter(observer);
/// TyphonProfiler.Start(_resourceParent, BuildTestMetadata());
/// try { /* drive workload */ }
/// finally { TyphonProfiler.Stop(); }
/// // observer.CountOf(TraceEventKind.X) is now populated
/// </code>
/// </remarks>
public sealed class TraceRingObserver : ResourceNode, IProfilerExporter
{
    private readonly long[] _countsByKind = new long[256];
    private readonly ConcurrentQueue<(TraceEventKind Kind, byte[] Bytes)> _records = new();
    private readonly bool _captureRawBytes;
    private long _batchesProcessed;
    private long _recordsProcessed;

    /// <summary>
    /// Construct a new observer.
    /// </summary>
    /// <param name="parent">Resource parent for graph integration. <c>null</c> = orphan node.</param>
    /// <param name="captureRawBytes">
    /// If <c>true</c>, every record's bytes are copied into <see cref="GetRecords"/>. If <c>false</c>, only counters are kept
    /// (cheaper for stress tests). Default <c>false</c>.
    /// </param>
    public TraceRingObserver(IResource parent = null, bool captureRawBytes = false)
        : base("TraceRingObserver", ResourceType.Service, parent)
    {
        _captureRawBytes = captureRawBytes;
        Queue = new ExporterQueue(boundedCapacity: 256);
    }

    /// <inheritdoc />
    public string Name => "TraceRingObserver";

    /// <inheritdoc />
    public ExporterQueue Queue { get; }

    /// <summary>Total batches received.</summary>
    public long BatchesProcessed => _batchesProcessed;

    /// <summary>Total records received across all kinds.</summary>
    public long RecordsProcessed => _recordsProcessed;

    /// <summary>Number of records observed of the given kind. Thread-safe.</summary>
    public long CountOf(TraceEventKind kind) => Volatile.Read(ref _countsByKind[(byte)kind]);

    /// <summary>
    /// Snapshot of all captured raw record bytes (only when constructed with <c>captureRawBytes: true</c>).
    /// Each entry is (kind, bytes) for one record. Bytes start at offset 0 = the common header's size field.
    /// </summary>
    public IReadOnlyList<(TraceEventKind Kind, byte[] Bytes)> GetRecords()
    {
        var snapshot = new List<(TraceEventKind, byte[])>();
        foreach (var record in _records)
        {
            snapshot.Add(record);
        }
        return snapshot;
    }

    /// <summary>
    /// Block until <paramref name="targetCount"/> records of <paramref name="kind"/> have been observed,
    /// or <paramref name="timeout"/> elapses. Returns the actual count observed.
    /// </summary>
    public long WaitFor(TraceEventKind kind, long targetCount, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var count = CountOf(kind);
            if (count >= targetCount)
            {
                return count;
            }
            Thread.Sleep(5);
        }
        return CountOf(kind);
    }

    /// <inheritdoc />
    public void Initialize(ProfilerSessionMetadata metadata)
    {
        // Nothing to set up — counters are pre-allocated.
    }

    /// <inheritdoc />
    public void ProcessBatch(TraceRecordBatch batch)
    {
        if (batch.PayloadBytes == 0 || batch.Count == 0)
        {
            return;
        }

        var payload = batch.Payload;
        var offsets = batch.Offsets;
        for (var i = 0; i < batch.Count; i++)
        {
            var offset = offsets[i];
            // Common header: bytes 0..1 = size (u16), byte 2 = kind, byte 3 = threadSlot.
            if (offset + 3 >= payload.Length)
            {
                continue;
            }
            var size = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
            var kindByte = payload[offset + 2];
            Interlocked.Increment(ref _countsByKind[kindByte]);

            if (_captureRawBytes && offset + size <= payload.Length)
            {
                var bytes = new byte[size];
                Array.Copy(payload, offset, bytes, 0, size);
                _records.Enqueue(((TraceEventKind)kindByte, bytes));
            }
        }

        Interlocked.Increment(ref _batchesProcessed);
        Interlocked.Add(ref _recordsProcessed, batch.Count);
    }

    /// <inheritdoc />
    public void Flush()
    {
        // Nothing to flush — counters are eagerly updated.
    }

    /// <summary>Reset all counters and clear captured records.</summary>
    public void Reset()
    {
        for (var i = 0; i < _countsByKind.Length; i++)
        {
            Volatile.Write(ref _countsByKind[i], 0);
        }
        Volatile.Write(ref _batchesProcessed, 0);
        Volatile.Write(ref _recordsProcessed, 0);
        while (_records.TryDequeue(out _)) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Queue.Dispose();
        }
        base.Dispose(disposing);
    }
}
