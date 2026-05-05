using System.Diagnostics;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 2 server-decoder tests for the two new gauge-related record kinds: <see cref="TraceEventKind.MemoryAllocEvent"/> (kind 9) and
/// <see cref="TraceEventKind.PerTickSnapshot"/> (kind 76). Exercises the match-arm dispatch in <see cref="RecordDecoder"/> and the wire →
/// <see cref="LiveTraceEvent"/> DTO mapping.
/// </summary>
/// <remarks>
/// These don't re-verify the codec internals (Phase 0 codec round-trip tests already cover that). They verify:
/// <list type="bullet">
///   <item><see cref="RecordDecoder.DecodeBlock"/> routes kind 9 / 76 through the new decoder methods instead of the generic instant path.</item>
///   <item>DTO fields match the payload (<c>Direction</c>, <c>SourceTag</c>, <c>SizeBytes</c>, <c>TotalAfterBytes</c> for kind 9;
///         <c>TickNumber</c>, <c>Flags</c>, <c>Gauges</c> dictionary for kind 76).</item>
///   <item>The decoder's <c>_currentTick</c> counter gets tagged onto the emitted DTO (TickStart → alloc inside tick → correct TickNumber).</item>
/// </list>
/// </remarks>
[TestFixture]
public class RecordDecoderGaugeTests
{
    // Stopwatch frequency — the RecordDecoder divides raw timestamps by (freq / 1_000_000) to produce µs. Using the real frequency keeps
    // the timestamp conversion realistic; individual assertions only check relative ordering, not absolute µs.
    private static readonly long TimestampFrequency = Stopwatch.Frequency;

    [Test]
    public void DecodeBlock_maps_MemoryAllocEvent_to_flat_DTO()
    {
        var buffer = new byte[MemoryAllocEventCodec.EventSize];
        var ts = Stopwatch.GetTimestamp();

        MemoryAllocEventCodec.WriteMemoryAllocEvent(
            buffer,
            threadSlot: 5,
            timestamp: ts,
            direction: MemoryAllocDirection.Alloc,
            sourceTag: MemoryAllocSource.PageCache,
            sizeBytes: 262_144UL,
            totalAfterBytes: 4_194_304UL,
            out _);

        var decoder = new RecordDecoder(TimestampFrequency);
        var output = new System.Collections.Generic.List<LiveTraceEvent>();
        decoder.DecodeBlock(buffer, output);

        Assert.That(output, Has.Count.EqualTo(1));
        var dto = output[0];
        Assert.Multiple(() =>
        {
            Assert.That(dto.Kind, Is.EqualTo((int)TraceEventKind.MemoryAllocEvent));
            Assert.That(dto.ThreadSlot, Is.EqualTo((byte)5));
            Assert.That(dto.Direction, Is.EqualTo((int)MemoryAllocDirection.Alloc));
            Assert.That(dto.SourceTag, Is.EqualTo((int)MemoryAllocSource.PageCache));
            Assert.That(dto.SizeBytes, Is.EqualTo(262_144.0));
            Assert.That(dto.TotalAfterBytes, Is.EqualTo(4_194_304.0));
            Assert.That(dto.TimestampUs, Is.GreaterThan(0));
            // Instant event — no span fields
            Assert.That(dto.SpanId, Is.Null);
            Assert.That(dto.DurationUs, Is.Null);
        });
    }

    [Test]
    public void DecodeBlock_Free_direction_round_trips()
    {
        var buffer = new byte[MemoryAllocEventCodec.EventSize];
        MemoryAllocEventCodec.WriteMemoryAllocEvent(
            buffer, threadSlot: 1, timestamp: 1000L,
            direction: MemoryAllocDirection.Free,
            sourceTag: MemoryAllocSource.WalStaging,
            sizeBytes: 64_000UL, totalAfterBytes: 0UL, out _);

        var decoder = new RecordDecoder(TimestampFrequency);
        var output = new System.Collections.Generic.List<LiveTraceEvent>();
        decoder.DecodeBlock(buffer, output);

        Assert.That(output[0].Direction, Is.EqualTo((int)MemoryAllocDirection.Free));
        Assert.That(output[0].SourceTag, Is.EqualTo((int)MemoryAllocSource.WalStaging));
    }

    [Test]
    public void DecodeBlock_maps_PerTickSnapshot_to_gauges_dictionary()
    {
        var values = new[]
        {
            GaugeValue.FromU64(GaugeId.MemoryUnmanagedTotalBytes, 1_048_576UL),
            GaugeValue.FromU32(GaugeId.MemoryUnmanagedLiveBlocks, 42u),
            GaugeValue.FromU32(GaugeId.PageCacheFreePages, 200u),
            GaugeValue.FromU32(GaugeId.PageCacheDirtyUsedPages, 12u),
            GaugeValue.FromI64(GaugeId.UowRegistryVoidCount, -1L),
        };

        var size = PerTickSnapshotEventCodec.ComputeSize(values);
        var buffer = new byte[size];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(
            buffer, threadSlot: 3, timestamp: 5000L,
            tickNumber: 99u, flags: 0u, values, out _);

        var decoder = new RecordDecoder(TimestampFrequency);
        var output = new System.Collections.Generic.List<LiveTraceEvent>();
        decoder.DecodeBlock(buffer, output);

        Assert.That(output, Has.Count.EqualTo(1));
        var dto = output[0];
        Assert.Multiple(() =>
        {
            Assert.That(dto.Kind, Is.EqualTo((int)TraceEventKind.PerTickSnapshot));
            Assert.That(dto.ThreadSlot, Is.EqualTo((byte)3));
            Assert.That(dto.TickNumber, Is.EqualTo(99), "TickNumber must come from the snapshot payload, not the decoder's running counter");
            Assert.That(dto.Flags, Is.EqualTo(0u));
            Assert.That(dto.Gauges, Is.Not.Null);
            Assert.That(dto.Gauges.Count, Is.EqualTo(values.Length));
            Assert.That(dto.Gauges[(int)GaugeId.MemoryUnmanagedTotalBytes], Is.EqualTo(1_048_576.0));
            Assert.That(dto.Gauges[(int)GaugeId.MemoryUnmanagedLiveBlocks], Is.EqualTo(42.0));
            Assert.That(dto.Gauges[(int)GaugeId.PageCacheFreePages], Is.EqualTo(200.0));
            Assert.That(dto.Gauges[(int)GaugeId.PageCacheDirtyUsedPages], Is.EqualTo(12.0));
            // i64 signed must preserve sign through the double conversion
            Assert.That(dto.Gauges[(int)GaugeId.UowRegistryVoidCount], Is.EqualTo(-1.0));
        });
    }

    [Test]
    public void DecodeBlock_tick_counter_tags_non_snapshot_records_inside_a_tick()
    {
        // Sequence: TickStart → MemoryAllocEvent → TickEnd. The alloc event should pick up TickNumber = 1 via the decoder's _currentTick counter.
        var tickStart = new byte[TraceRecordHeader.CommonHeaderSize];
        InstantEventCodec.WriteTickStart(tickStart, threadSlot: 0, timestamp: 100L, out _);

        var alloc = new byte[MemoryAllocEventCodec.EventSize];
        MemoryAllocEventCodec.WriteMemoryAllocEvent(alloc, 0, 200L, MemoryAllocDirection.Alloc, MemoryAllocSource.Unattributed, 64UL, 64UL, out _);

        var tickEnd = new byte[TraceRecordHeader.CommonHeaderSize + 2];
        InstantEventCodec.WriteTickEnd(tickEnd, threadSlot: 0, timestamp: 300L, overloadLevel: 0, tickMultiplier: 1, out _);

        var combined = new byte[tickStart.Length + alloc.Length + tickEnd.Length];
        System.Buffer.BlockCopy(tickStart, 0, combined, 0, tickStart.Length);
        System.Buffer.BlockCopy(alloc, 0, combined, tickStart.Length, alloc.Length);
        System.Buffer.BlockCopy(tickEnd, 0, combined, tickStart.Length + alloc.Length, tickEnd.Length);

        var decoder = new RecordDecoder(TimestampFrequency);
        var output = new System.Collections.Generic.List<LiveTraceEvent>();
        decoder.DecodeBlock(combined, output);

        Assert.That(output, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(output[0].Kind, Is.EqualTo((int)TraceEventKind.TickStart));
            Assert.That(output[0].TickNumber, Is.EqualTo(1));
            Assert.That(output[1].Kind, Is.EqualTo((int)TraceEventKind.MemoryAllocEvent));
            Assert.That(output[1].TickNumber, Is.EqualTo(1), "alloc event must inherit the current tick from TickStart");
            Assert.That(output[2].Kind, Is.EqualTo((int)TraceEventKind.TickEnd));
            Assert.That(output[2].TickNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public void DecodeBlock_snapshot_TickNumber_wins_over_decoder_counter()
    {
        // Emit a snapshot whose intrinsic TickNumber disagrees with the decoder's _currentTick. The DTO should surface the payload value
        // (authoritative from the scheduler), not the decoder's local counter. This protects the mid-session-reconnect case where the decoder
        // starts at 0 but the scheduler has been running for thousands of ticks.
        var values = new[] { GaugeValue.FromU32(GaugeId.PageCacheFreePages, 8u) };
        var buffer = new byte[PerTickSnapshotEventCodec.ComputeSize(values)];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(
            buffer, threadSlot: 0, timestamp: 1L,
            tickNumber: 12_345u, flags: 0u, values, out _);

        var decoder = new RecordDecoder(TimestampFrequency);
        // Decoder's _currentTick is 0 — no TickStart has advanced it. Snapshot says 12345. DTO must show 12345.
        var output = new System.Collections.Generic.List<LiveTraceEvent>();
        decoder.DecodeBlock(buffer, output);

        Assert.That(output[0].TickNumber, Is.EqualTo(12_345));
    }
}
