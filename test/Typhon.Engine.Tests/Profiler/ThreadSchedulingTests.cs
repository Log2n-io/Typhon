using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Profiler;
using Typhon.Profiler.Events;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Foundation tests for the OS thread scheduling pipeline: wait-reason enum wire stability, encoder/decoder round-trip
/// for <see cref="ThreadContextSwitchEventDto"/>, and OS-thread-id lookup on the registry.
///
/// The ETW pump itself is integration-only (needs Administrator + an unowned NT Kernel Logger session); covering it in
/// CI would require an isolated VM. The pieces it depends on are exercised here, so a regression in any of them shows
/// up before the pump runs.
/// </summary>
[TestFixture]
public sealed class ThreadSchedulingTests
{
    /// <summary>Size of the 12-byte trace-record common header (u16 size, u8 kind, u8 threadSlot, i64 timestamp).</summary>
    private const int CommonHeaderSize = 12;

    [Test]
    public void ThreadWaitReason_WireStableValues()
    {
        // Wire-stable kernel mirror — these specific numerics MUST not change. Newer Windows builds can append entries
        // (added entries get sentinel-mapped at decode), but renaming/renumbering an existing entry would silently
        // corrupt every cached .typhon-trace ever recorded.
        Assert.That((byte)ThreadWaitReason.Executive, Is.EqualTo(0));
        Assert.That((byte)ThreadWaitReason.PageIn, Is.EqualTo(2));
        Assert.That((byte)ThreadWaitReason.UserRequest, Is.EqualTo(6));
        Assert.That((byte)ThreadWaitReason.WrQueue, Is.EqualTo(15));
        Assert.That((byte)ThreadWaitReason.WrQuantumEnd, Is.EqualTo(30));
        Assert.That((byte)ThreadWaitReason.WrPreempted, Is.EqualTo(32));
        Assert.That((byte)ThreadWaitReason.WrYieldExecution, Is.EqualTo(33));
        Assert.That((byte)ThreadWaitReason.MaximumWaitReason, Is.EqualTo(37));
    }

    [Test]
    public void ThreadContextSwitchEventDto_RoundTrip_PayloadFieldsPreserved()
    {
        // Build the wire layout by hand so this test doesn't depend on the producer's gating or ring-buffer state —
        // 12-byte common header + 13-byte payload = 25 bytes.
        const int recordSize = 25;
        const byte producerSlot = 7;        // The pump's slot in the header — the workbench re-projects to TargetSlotIdx.
        const long startQpc = 1_234_567_890L;
        const byte targetSlotIdx = 4;
        const byte processorNumber = 11;
        const ThreadWaitReason waitReason = ThreadWaitReason.WrQuantumEnd;
        const byte threadState = 1; // System.Diagnostics.ThreadState.Ready raw value
        const byte gettingIdleByte = 1;
        const uint durationQpc = 1_500_000u;
        const uint readyTimeQpc = 850u;

        Span<byte> buf = stackalloc byte[recordSize];
        TraceRecordHeader.WriteCommonHeader(buf, recordSize, TraceEventKind.ThreadContextSwitch, producerSlot, startQpc);

        var payload = buf[12..];
        payload[0] = targetSlotIdx;
        payload[1] = processorNumber;
        payload[2] = (byte)waitReason;
        payload[3] = threadState;
        payload[4] = gettingIdleByte;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[5..], durationQpc);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[9..], readyTimeQpc);

        // ── Decode via the per-kind DTO Decode entry point ──
        var dto = ThreadContextSwitchEventDto.Decode(buf, currentTick: 42, ticksPerUs: 10);

        Assert.That(dto.KindByte, Is.EqualTo((byte)TraceEventKind.ThreadContextSwitch));
        Assert.That(dto.ThreadSlot, Is.EqualTo(producerSlot));
        Assert.That(dto.TickNumber, Is.EqualTo(42));
        Assert.That(dto.TimestampUs, Is.EqualTo(startQpc / 10.0));
        Assert.That(dto.TargetSlotIdx, Is.EqualTo(targetSlotIdx));
        Assert.That(dto.ProcessorNumber, Is.EqualTo(processorNumber));
        Assert.That(dto.WaitReason, Is.EqualTo(waitReason));
        Assert.That(dto.ThreadState, Is.EqualTo(threadState));
        Assert.That(dto.GettingIdle, Is.True);
        Assert.That(dto.DurationQpc, Is.EqualTo(durationQpc));
        Assert.That(dto.ReadyTimeQpc, Is.EqualTo(readyTimeQpc));

        // ── Top-level dispatch should yield the same typed DTO. Guards against missing polymorphic registration. ──
        var via = TraceEventDecoder.Decode(buf, currentTick: 42, ticksPerUs: 10);
        Assert.That(via, Is.InstanceOf<ThreadContextSwitchEventDto>());
    }

    [Test]
    public void ThreadContextSwitchEventDto_GettingIdle_FalseRoundTrip()
    {
        const int recordSize = 25;
        Span<byte> buf = stackalloc byte[recordSize];
        TraceRecordHeader.WriteCommonHeader(buf, recordSize, TraceEventKind.ThreadContextSwitch, 0, 0);
        var payload = buf[12..];
        payload[0] = 1;
        payload[1] = 0;
        payload[2] = (byte)ThreadWaitReason.UserRequest;
        payload[3] = 0;
        payload[4] = 0; // gettingIdle = false
        BinaryPrimitives.WriteUInt32LittleEndian(payload[5..], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[9..], 0u);

        var dto = ThreadContextSwitchEventDto.Decode(buf, currentTick: 0, ticksPerUs: 1);
        Assert.That(dto.GettingIdle, Is.False);
    }

    [Test]
    public void ThreadSlotRegistry_TryGetSlotByOsThreadId_FindsClaimedSlotOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("OS thread id mapping is Windows-only.");
        }

        // Clean state so the assert against the freshly-claimed slot doesn't false-positive on a pre-existing slot.
        ThreadSlotRegistry.ResetForTests();
        try
        {
            // Claim a slot on this thread.
            var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
            Assert.That(slotIdx, Is.GreaterThanOrEqualTo(0));

            var slot = ThreadSlotRegistry.GetSlot(slotIdx);
            Assert.That(slot.OwnerOsThreadId, Is.Not.Zero, "AssignClaim should have captured a non-zero OS TID on Windows.");

            // Look it up.
            Assert.That(ThreadSlotRegistry.TryGetSlotByOsThreadId(slot.OwnerOsThreadId, out var found), Is.True);
            Assert.That(found, Is.EqualTo(slotIdx));
        }
        finally
        {
            ThreadSlotRegistry.ResetForTests();
        }
    }

    [Test]
    public void ThreadSlotRegistry_TryGetSlotByOsThreadId_ReturnsFalseForUnknownTid()
    {
        ThreadSlotRegistry.ResetForTests();
        try
        {
            Assert.That(ThreadSlotRegistry.TryGetSlotByOsThreadId(osThreadId: 0, out _), Is.False);
            // 0xDEADBEEF is far above any realistic TID — Windows TIDs are multiples of 4 starting at small numbers.
            Assert.That(ThreadSlotRegistry.TryGetSlotByOsThreadId(osThreadId: 0xDEADBEEF, out _), Is.False);
        }
        finally
        {
            ThreadSlotRegistry.ResetForTests();
        }
    }

    [Test]
    public void TraceEventKind_ThreadContextSwitch_IsNotSpan()
    {
        // Instant-shape carve-out — the wire layout has no 25-byte span header extension after the common header.
        // A decoder that mis-classifies this as a span would read 25 payload bytes as fake span metadata.
        Assert.That(TraceEventKind.ThreadContextSwitch.IsSpan(), Is.False);
    }

    [Test]
    public void ThreadContextSwitch_SurvivesTraceCacheRoundTrip()
    {
        // Regression guard for the Trace-mode (.typhon-cache) delivery path. IncrementalCacheBuilder.FeedRawRecords writes every record to the
        // chunk buffer unconditionally — FoldV12Event is a per-tick summary accumulator, NOT an allow-list filter. This test proves a kind-254
        // record fed through the builder is persisted into the cached chunk and decodes back to a ThreadContextSwitchEventDto with every payload
        // field intact. If a future fold-list refactor ever introduces a per-kind allow-list that omits 254, this test fails loudly.
        const int csCount = 4;
        const int csSize = 25;
        var buffer = new byte[CommonHeaderSize + csCount * csSize + CommonHeaderSize]; // TickStart + N×ThreadContextSwitch + TickEnd
        var pos = 0;
        long ts = 1000;

        // TickStart — a 12-byte common-header-only record so the builder opens a real tick (records outside a tick take the pre-tick path).
        TraceRecordHeader.WriteCommonHeader(buffer.AsSpan(pos, CommonHeaderSize), CommonHeaderSize, TraceEventKind.TickStart, 0, ts++);
        pos += CommonHeaderSize;

        // N ThreadContextSwitch records — payloads vary per index so the round-trip assertions catch field-offset / endianness regressions.
        for (var i = 0; i < csCount; i++)
        {
            var span = buffer.AsSpan(pos, csSize);
            TraceRecordHeader.WriteCommonHeader(span, csSize, TraceEventKind.ThreadContextSwitch, threadSlot: 9, ts++);
            var payload = span[CommonHeaderSize..];
            payload[0] = (byte)(3 + i);                                            // targetSlotIdx
            payload[1] = (byte)(10 + i);                                           // processorNumber
            payload[2] = (byte)ThreadWaitReason.WrPreempted;                       // waitReason
            payload[3] = 2;                                                        // threadState
            payload[4] = (byte)(i % 2);                                            // gettingIdle
            BinaryPrimitives.WriteUInt32LittleEndian(payload[5..], (uint)(1000 * (i + 1)));  // durationQpc
            BinaryPrimitives.WriteUInt32LittleEndian(payload[9..], (uint)(7 * (i + 1)));     // readyTimeQpc
            pos += csSize;
        }

        // TickEnd — closes the tick so the builder doesn't have to rely solely on Dispose's trailing flush.
        TraceRecordHeader.WriteCommonHeader(buffer.AsSpan(pos, CommonHeaderSize), CommonHeaderSize, TraceEventKind.TickEnd, 0, ts);

        var fingerprint = new byte[32];
        var profilerHeader = new ProfilerHeader { Version = TraceFileHeader.CurrentVersion, TimestampFrequency = 10_000_000 };
        var sink = new ListSink();
        using (var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, new Dictionary<int, string>(), new Dictionary<ushort, string>()))
        {
            builder.FeedRawRecords(buffer);
        }

        Assert.That(sink.Chunks.Count, Is.GreaterThan(0), "builder must have flushed at least one chunk");

        // Walk every cached chunk, decode each kind-254 record via the top-level polymorphic decoder, collect them in chunk order.
        var decoded = new List<ThreadContextSwitchEventDto>();
        foreach (var chunk in sink.Chunks)
        {
            var cpos = 0;
            while (cpos + CommonHeaderSize <= chunk.Length)
            {
                var size = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(cpos));
                if (size < CommonHeaderSize || cpos + size > chunk.Length)
                {
                    break;
                }
                if (chunk[cpos + 2] == (byte)TraceEventKind.ThreadContextSwitch)
                {
                    var dto = TraceEventDecoder.Decode(chunk.AsSpan(cpos, size), currentTick: 1, ticksPerUs: 10);
                    Assert.That(dto, Is.InstanceOf<ThreadContextSwitchEventDto>(), "kind 254 must dispatch to ThreadContextSwitchEventDto");
                    decoded.Add((ThreadContextSwitchEventDto)dto);
                }
                cpos += size;
            }
        }

        Assert.That(decoded.Count, Is.EqualTo(csCount), "every kind-254 record must survive the cache round-trip — none dropped by the fold path");
        for (var i = 0; i < csCount; i++)
        {
            var dto = decoded[i];
            Assert.That(dto.TargetSlotIdx, Is.EqualTo((byte)(3 + i)), $"record {i}: TargetSlotIdx");
            Assert.That(dto.ProcessorNumber, Is.EqualTo((byte)(10 + i)), $"record {i}: ProcessorNumber");
            Assert.That(dto.WaitReason, Is.EqualTo(ThreadWaitReason.WrPreempted), $"record {i}: WaitReason");
            Assert.That(dto.ThreadState, Is.EqualTo((byte)2), $"record {i}: ThreadState");
            Assert.That(dto.GettingIdle, Is.EqualTo(i % 2 == 1), $"record {i}: GettingIdle");
            Assert.That(dto.DurationQpc, Is.EqualTo((uint)(1000 * (i + 1))), $"record {i}: DurationQpc");
            Assert.That(dto.ReadyTimeQpc, Is.EqualTo((uint)(7 * (i + 1))), $"record {i}: ReadyTimeQpc");
        }
    }

    /// <summary>In-memory <see cref="ICacheChunkSink"/> — captures each appended chunk's uncompressed record bytes for inspection.</summary>
    private sealed class ListSink : ICacheChunkSink
    {
        public bool SupportsTrailer => false;
        public List<byte[]> Chunks { get; } = new();
        private long _offset;

        public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords)
        {
            var copy = uncompressedRecords.ToArray();
            Chunks.Add(copy);
            var off = _offset;
            _offset += copy.Length;
            return (off, (uint)copy.Length, (uint)copy.Length);
        }

        public void WriteTrailer(IReadOnlyList<TickSummary> ts, in GlobalMetricsFixed gm, IReadOnlyList<SystemAggregateDuration> sa,
            IReadOnlyList<ChunkManifestEntry> cm, IReadOnlyDictionary<int, string> sn, ReadOnlySpan<byte> sourceMetadataBytes, in CacheHeader h,
            IReadOnlyList<SystemTickSummary> sts, IReadOnlyList<QueueTickSummary> qts, IReadOnlyList<PostTickSummary> pts,
            IReadOnlyDictionary<ushort, string> qIdToName, IReadOnlyList<SystemArchetypeTouchSummary> sat)
            => throw new NotSupportedException("ListSink does not support trailer.");

        public void Dispose() { }
    }
}
