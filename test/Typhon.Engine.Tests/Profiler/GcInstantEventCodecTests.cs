using System;
using System.Diagnostics;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format round-trip tests for <see cref="GcInstantEventCodec"/>. No profiler state is mutated — these exercise the codec helpers in
/// isolation, which is what the production emit path uses under the hood.
/// </summary>
[TestFixture]
public class GcInstantEventCodecTests
{
    [Test]
    public void GcStart_round_trip_preserves_all_fields()
    {
        Span<byte> buffer = stackalloc byte[GcInstantEventCodec.GcStartSize];
        var ts = Stopwatch.GetTimestamp();

        GcInstantEventCodec.WriteGcStart(
            buffer,
            threadSlot: 42,
            timestamp: ts,
            generation: 2,
            reason: GcReason.LowMemory,
            type: GcType.Background,
            count: 123_456u,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(GcInstantEventCodec.GcStartSize));

        var decoded = GcInstantEventCodec.DecodeGcStart(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.ThreadSlot, Is.EqualTo((byte)42));
            Assert.That(decoded.Timestamp, Is.EqualTo(ts));
            Assert.That(decoded.Generation, Is.EqualTo((byte)2));
            Assert.That(decoded.Reason, Is.EqualTo(GcReason.LowMemory));
            Assert.That(decoded.Type, Is.EqualTo(GcType.Background));
            Assert.That(decoded.Count, Is.EqualTo(123_456u));
        });
    }

    [Test]
    public void GcStart_wire_size_is_19_bytes()
    {
        // 12 B common header + 3 B (gen/reason/type) + 4 B (count) = 19 B.
        Assert.That(GcInstantEventCodec.GcStartSize, Is.EqualTo(19));
    }

    [Test]
    public void GcEnd_round_trip_preserves_all_fields()
    {
        Span<byte> buffer = stackalloc byte[GcInstantEventCodec.GcEndSize];
        var ts = Stopwatch.GetTimestamp();

        GcInstantEventCodec.WriteGcEnd(
            buffer,
            threadSlot: 7,
            timestamp: ts,
            generation: 1,
            count: 999u,
            pauseDurationTicks: 12345L,
            promotedBytes: 8_000_000UL,
            gen0SizeAfter: 1UL,
            gen1SizeAfter: 2UL,
            gen2SizeAfter: 3UL,
            lohSizeAfter: 4UL,
            pohSizeAfter: 5UL,
            totalCommittedBytes: 6UL,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(GcInstantEventCodec.GcEndSize));

        var decoded = GcInstantEventCodec.DecodeGcEnd(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.ThreadSlot, Is.EqualTo((byte)7));
            Assert.That(decoded.Timestamp, Is.EqualTo(ts));
            Assert.That(decoded.Generation, Is.EqualTo((byte)1));
            Assert.That(decoded.Count, Is.EqualTo(999u));
            Assert.That(decoded.PauseDurationTicks, Is.EqualTo(12345L));
            Assert.That(decoded.PromotedBytes, Is.EqualTo(8_000_000UL));
            Assert.That(decoded.Gen0SizeAfter, Is.EqualTo(1UL));
            Assert.That(decoded.Gen1SizeAfter, Is.EqualTo(2UL));
            Assert.That(decoded.Gen2SizeAfter, Is.EqualTo(3UL));
            Assert.That(decoded.LohSizeAfter, Is.EqualTo(4UL));
            Assert.That(decoded.PohSizeAfter, Is.EqualTo(5UL));
            Assert.That(decoded.TotalCommittedBytes, Is.EqualTo(6UL));
        });
    }

    [Test]
    public void GcEnd_wire_size_is_81_bytes()
    {
        // 12 B common header + 1 (gen) + 4 (count) + 8 (pause) + 8 (promoted) + 5*8 (gen sizes) + 8 (committed) = 81 B.
        Assert.That(GcInstantEventCodec.GcEndSize, Is.EqualTo(81));
    }

    [Test]
    public void DecodeGcStart_throws_on_kind_mismatch()
    {
        Span<byte> buffer = stackalloc byte[GcInstantEventCodec.GcEndSize];
        GcInstantEventCodec.WriteGcEnd(buffer, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out _);
        var span = buffer;

        // Capture for local function (can't use Span in lambda).
        byte[] arr = span.ToArray();
        Assert.Throws<ArgumentException>(() => GcInstantEventCodec.DecodeGcStart(arr));
    }
}
