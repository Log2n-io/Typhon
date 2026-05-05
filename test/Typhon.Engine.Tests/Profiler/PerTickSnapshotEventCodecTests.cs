using System;
using System.Diagnostics;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format round-trip tests for <see cref="PerTickSnapshotEventCodec"/>. Covers every <see cref="GaugeValueKind"/> variant plus
/// empty-payload and boundary-size edge cases.
/// </summary>
[TestFixture]
public class PerTickSnapshotEventCodecTests
{
    [Test]
    public void Empty_snapshot_round_trips_with_only_prefix_bytes()
    {
        var expectedSize = PerTickSnapshotEventCodec.PrefixSize;
        var buffer = new byte[expectedSize];
        var ts = Stopwatch.GetTimestamp();

        PerTickSnapshotEventCodec.WritePerTickSnapshot(
            buffer,
            threadSlot: 4,
            timestamp: ts,
            tickNumber: 42u,
            flags: 0u,
            values: ReadOnlySpan<GaugeValue>.Empty,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(expectedSize));

        var decoded = PerTickSnapshotEventCodec.DecodePerTickSnapshot(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.ThreadSlot, Is.EqualTo((byte)4));
            Assert.That(decoded.Timestamp, Is.EqualTo(ts));
            Assert.That(decoded.TickNumber, Is.EqualTo(42u));
            Assert.That(decoded.Flags, Is.EqualTo(0u));
            Assert.That(decoded.Values, Is.Empty);
        });
    }

    [Test]
    public void Prefix_size_is_22_bytes()
    {
        // 12 B common header + 4 B tickNumber + 2 B fieldCount + 4 B flags = 22 B.
        Assert.That(PerTickSnapshotEventCodec.PrefixSize, Is.EqualTo(22));
    }

    [Test]
    public void Mixed_value_kinds_round_trip()
    {
        var values = new[]
        {
            GaugeValue.FromU64(GaugeId.MemoryUnmanagedTotalBytes, 1_048_576UL),
            GaugeValue.FromU64(GaugeId.MemoryUnmanagedPeakBytes, 2_097_152UL),
            GaugeValue.FromU32(GaugeId.MemoryUnmanagedLiveBlocks, 87u),
            GaugeValue.FromU64(GaugeId.GcHeapGen2Bytes, 512UL * 1024UL),
            GaugeValue.FromU32(GaugeId.PageCacheFreePages, 64u),
            GaugeValue.FromU32(GaugeId.PageCacheDirtyUsedPages, 192u),
            GaugeValue.FromI64(GaugeId.UowRegistryVoidCount, -1L),
            GaugeValue.FromPercentHundredths(GaugeId.None, 5025u),
        };

        var expectedSize = PerTickSnapshotEventCodec.ComputeSize(values);
        var buffer = new byte[expectedSize];

        PerTickSnapshotEventCodec.WritePerTickSnapshot(
            buffer,
            threadSlot: 9,
            timestamp: 12345L,
            tickNumber: 7u,
            flags: 0u,
            values,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(expectedSize));

        var decoded = PerTickSnapshotEventCodec.DecodePerTickSnapshot(buffer);
        Assert.That(decoded.Values.Length, Is.EqualTo(values.Length));

        for (var i = 0; i < values.Length; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(decoded.Values[i].Id, Is.EqualTo(values[i].Id), $"id mismatch at {i}");
                Assert.That(decoded.Values[i].Kind, Is.EqualTo(values[i].Kind), $"kind mismatch at {i}");
                Assert.That(decoded.Values[i].RawValue, Is.EqualTo(values[i].RawValue), $"raw mismatch at {i}");
            });
        }
    }

    [Test]
    public void ComputeSize_matches_actual_write_count()
    {
        // The writer trusts ComputeSize — any drift would corrupt the Size header and break the ring's record-boundary detection.
        var values = new[]
        {
            GaugeValue.FromU32(GaugeId.TxChainActiveCount, 3u),
            GaugeValue.FromU32(GaugeId.TxChainPoolSize, 8u),
            GaugeValue.FromU64(GaugeId.WalStagingTotalRentsCumulative, 1_000_000UL),
        };

        var computed = PerTickSnapshotEventCodec.ComputeSize(values);
        var buffer = new byte[computed];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(buffer, 0, 0, 0, 0, values, out var written);
        Assert.That(written, Is.EqualTo(computed));
    }

    [Test]
    public void Decode_throws_on_kind_mismatch()
    {
        var arr = new byte[PerTickSnapshotEventCodec.PrefixSize];
        TraceRecordHeader.WriteCommonHeader(arr, (ushort)arr.Length, TraceEventKind.TickStart, threadSlot: 0, startTimestamp: 0);
        Assert.Throws<ArgumentException>(() => PerTickSnapshotEventCodec.DecodePerTickSnapshot(arr));
    }

    [Test]
    public void I64_signed_preserves_negative_values()
    {
        var values = new[]
        {
            GaugeValue.FromI64(GaugeId.UowRegistryActiveCount, long.MinValue),
            GaugeValue.FromI64(GaugeId.UowRegistryVoidCount, -1L),
            GaugeValue.FromI64(GaugeId.TxChainActiveCount, long.MaxValue),
        };

        var buffer = new byte[PerTickSnapshotEventCodec.ComputeSize(values)];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(buffer, 0, 0, 0, 0, values, out _);
        var decoded = PerTickSnapshotEventCodec.DecodePerTickSnapshot(buffer);

        Assert.Multiple(() =>
        {
            Assert.That(unchecked((long)decoded.Values[0].RawValue), Is.EqualTo(long.MinValue));
            Assert.That(unchecked((long)decoded.Values[1].RawValue), Is.EqualTo(-1L));
            Assert.That(unchecked((long)decoded.Values[2].RawValue), Is.EqualTo(long.MaxValue));
        });
    }

    [Test]
    public void GaugeValue_WireSize_matches_per_kind_payload()
    {
        // Prefix = 3 (id + kind), payload = 4 or 8 depending on kind.
        Assert.Multiple(() =>
        {
            Assert.That(GaugeValue.FromU32(GaugeId.None, 0).WireSize, Is.EqualTo(3 + 4));
            Assert.That(GaugeValue.FromU64(GaugeId.None, 0).WireSize, Is.EqualTo(3 + 8));
            Assert.That(GaugeValue.FromI64(GaugeId.None, 0).WireSize, Is.EqualTo(3 + 8));
            Assert.That(GaugeValue.FromPercentHundredths(GaugeId.None, 0).WireSize, Is.EqualTo(3 + 4));
        });
    }

    [Test]
    public void Flags_field_round_trips()
    {
        var buffer = new byte[PerTickSnapshotEventCodec.PrefixSize];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(buffer, 0, 0, tickNumber: 1u, flags: 0xDEADBEEFu, ReadOnlySpan<GaugeValue>.Empty, out _);
        var decoded = PerTickSnapshotEventCodec.DecodePerTickSnapshot(buffer);
        Assert.That(decoded.Flags, Is.EqualTo(0xDEADBEEFu));
    }

    [Test]
    public void Kind_76_is_not_classified_as_span()
    {
        // Regression guard: PerTickSnapshot's numeric ID is ≥ 10, but it has no span header extension. If IsSpan() returns true,
        // the consumer would misread 25 bytes of its payload as span-header metadata.
        Assert.That(TraceEventKind.PerTickSnapshot.IsSpan(), Is.False);
        // And every "real" span still classifies correctly.
        Assert.That(TraceEventKind.SchedulerChunk.IsSpan(), Is.True);
        Assert.That(TraceEventKind.GcSuspension.IsSpan(), Is.True);
        Assert.That(TraceEventKind.MemoryAllocEvent.IsSpan(), Is.False);
    }
}
