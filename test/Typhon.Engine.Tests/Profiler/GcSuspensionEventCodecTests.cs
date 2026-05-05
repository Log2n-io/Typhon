using System;
using System.Diagnostics;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format round-trip tests for <see cref="GcSuspensionEventCodec"/>.
/// </summary>
[TestFixture]
public class GcSuspensionEventCodecTests
{
    [Test]
    public void Round_trip_preserves_all_fields()
    {
        Span<byte> buffer = stackalloc byte[GcSuspensionEventCodec.Size];
        var startTs = Stopwatch.GetTimestamp();
        var endTs = startTs + 50_000L;

        GcSuspensionEventCodec.Write(
            buffer,
            threadSlot: 3,
            startTimestamp: startTs,
            endTimestamp: endTs,
            spanId: 0xDEAD_BEEF_CAFE_F00DUL,
            parentSpanId: 0UL,
            reason: GcSuspendReason.ForGC,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(GcSuspensionEventCodec.Size));

        var decoded = GcSuspensionEventCodec.Decode(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.ThreadSlot, Is.EqualTo((byte)3));
            Assert.That(decoded.StartTimestamp, Is.EqualTo(startTs));
            Assert.That(decoded.DurationTicks, Is.EqualTo(endTs - startTs));
            Assert.That(decoded.SpanId, Is.EqualTo(0xDEAD_BEEF_CAFE_F00DUL));
            Assert.That(decoded.ParentSpanId, Is.EqualTo(0UL));
            Assert.That(decoded.Reason, Is.EqualTo(GcSuspendReason.ForGC));
            Assert.That(decoded.OptMask, Is.EqualTo((byte)0));
        });
    }

    [Test]
    public void Wire_size_is_39_bytes()
    {
        // 12 B common + 25 B span extension + 1 B reason + 1 B optMask = 39 B.
        Assert.That(GcSuspensionEventCodec.Size, Is.EqualTo(39));
    }
}
