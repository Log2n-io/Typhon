using System;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Profiler.Events;

namespace Typhon.Engine.Tests.Profiler.Events;

/// <summary>
/// A <see cref="TraceEventKind.PerTickSnapshot"/> record decodes to a typed DTO carrying its gauges, not to <c>kind: "other"</c>, which hid every
/// gauge from a reader of the decoded-chunk API.
/// </summary>
[TestFixture]
public class PerTickSnapshotDtoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Test]
    public void A_per_tick_snapshot_decodes_with_its_gauges()
    {
        GaugeValue[] values =
        [
            GaugeValue.FromU64(GaugeId.MemoryUnmanagedTotalBytes, 123_456_789),
            GaugeValue.FromI64(GaugeId.MemoryUnmanagedPeakBytes, -5),
            GaugeValue.FromPercentHundredths(GaugeId.MemoryUnmanagedTotalBytes, 4250),
        ];
        var buffer = new byte[PerTickSnapshotEventCodec.ComputeSize(values)];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(buffer, 3, 2_000, 42, 7, values, out var written);

        var dto = TraceEventDecoder.Decode(buffer.AsSpan(0, written), 41, 1);

        Assert.That(dto, Is.TypeOf<PerTickSnapshotEventDto>());
        var snap = (PerTickSnapshotEventDto)dto;
        Assert.Multiple(() =>
        {
            Assert.That(snap.ThreadSlot, Is.EqualTo(3));
            Assert.That(snap.TickNumber, Is.EqualTo(41));
            Assert.That(snap.SnapshotTick, Is.EqualTo(42u));
            Assert.That(snap.Flags, Is.EqualTo(7u));
            Assert.That(snap.Gauges, Is.EqualTo(new[]
            {
                new GaugeValueDto(nameof(GaugeId.MemoryUnmanagedTotalBytes), nameof(GaugeValueKind.U64Bytes), 123_456_789),
                new GaugeValueDto(nameof(GaugeId.MemoryUnmanagedPeakBytes), nameof(GaugeValueKind.I64Signed), -5),
                new GaugeValueDto(nameof(GaugeId.MemoryUnmanagedTotalBytes), nameof(GaugeValueKind.U32PercentHundredths), 42.5),
            }));
            Assert.That(JsonSerializer.Serialize<TraceEventDto>(dto, Json), Does.Contain("\"kind\":\"perTickSnapshot\""));
        });
    }

    [Test]
    public void A_kind_this_build_does_not_know_falls_back_with_its_number_as_name()
    {
        // 255 is not a declared kind: a record from a newer producer takes the fallback, and its name is its number.
        var buffer = new byte[TraceRecordHeader.CommonHeaderSize];
        TraceRecordHeader.WriteCommonHeader(buffer, (ushort)buffer.Length, (TraceEventKind)255, 0, 100);
        var dto = TraceEventDecoder.Decode(buffer, 1, 1);

        Assert.That(dto, Is.TypeOf<OtherTraceEventDto>());
        Assert.That(((OtherTraceEventDto)dto).KindName, Is.EqualTo("255"));
    }
}
