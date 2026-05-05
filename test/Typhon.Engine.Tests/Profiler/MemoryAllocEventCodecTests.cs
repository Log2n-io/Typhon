using System;
using System.Diagnostics;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format round-trip tests for <see cref="MemoryAllocEventCodec"/>. Exercises the codec helpers in isolation — no profiler state.
/// </summary>
[TestFixture]
public class MemoryAllocEventCodecTests
{
    [Test]
    public void Alloc_round_trip_preserves_all_fields()
    {
        Span<byte> buffer = stackalloc byte[MemoryAllocEventCodec.EventSize];
        var ts = Stopwatch.GetTimestamp();

        MemoryAllocEventCodec.WriteMemoryAllocEvent(
            buffer,
            threadSlot: 17,
            timestamp: ts,
            direction: MemoryAllocDirection.Alloc,
            sourceTag: MemoryAllocSource.WalStaging,
            sizeBytes: 64 * 1024UL,
            totalAfterBytes: 1_048_576UL,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(MemoryAllocEventCodec.EventSize));

        var decoded = MemoryAllocEventCodec.DecodeMemoryAllocEvent(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.ThreadSlot, Is.EqualTo((byte)17));
            Assert.That(decoded.Timestamp, Is.EqualTo(ts));
            Assert.That(decoded.Direction, Is.EqualTo(MemoryAllocDirection.Alloc));
            Assert.That(decoded.SourceTag, Is.EqualTo((ushort)MemoryAllocSource.WalStaging));
            Assert.That(decoded.SizeBytes, Is.EqualTo(64UL * 1024UL));
            Assert.That(decoded.TotalAfterBytes, Is.EqualTo(1_048_576UL));
        });
    }

    [Test]
    public void Free_round_trip_preserves_all_fields()
    {
        Span<byte> buffer = stackalloc byte[MemoryAllocEventCodec.EventSize];
        var ts = Stopwatch.GetTimestamp();

        MemoryAllocEventCodec.WriteMemoryAllocEvent(
            buffer,
            threadSlot: 3,
            timestamp: ts,
            direction: MemoryAllocDirection.Free,
            sourceTag: MemoryAllocSource.PageCache,
            sizeBytes: 8_192UL,
            totalAfterBytes: 0UL,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(MemoryAllocEventCodec.EventSize));

        var decoded = MemoryAllocEventCodec.DecodeMemoryAllocEvent(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.Direction, Is.EqualTo(MemoryAllocDirection.Free));
            Assert.That(decoded.SourceTag, Is.EqualTo((ushort)MemoryAllocSource.PageCache));
            Assert.That(decoded.SizeBytes, Is.EqualTo(8_192UL));
            Assert.That(decoded.TotalAfterBytes, Is.EqualTo(0UL));
        });
    }

    [Test]
    public void Wire_size_is_31_bytes()
    {
        // 12 B common header + 1 (direction) + 2 (sourceTag) + 8 (sizeBytes) + 8 (totalAfterBytes) = 31 B.
        Assert.That(MemoryAllocEventCodec.EventSize, Is.EqualTo(31));
    }

    [Test]
    public void Decode_throws_on_kind_mismatch()
    {
        // Forge a 12-byte buffer with kind=GcStart and try to decode as MemoryAllocEvent.
        var arr = new byte[MemoryAllocEventCodec.EventSize];
        TraceRecordHeader.WriteCommonHeader(arr, (ushort)MemoryAllocEventCodec.EventSize, TraceEventKind.GcStart, threadSlot: 0, startTimestamp: 0);
        Assert.Throws<ArgumentException>(() => MemoryAllocEventCodec.DecodeMemoryAllocEvent(arr));
    }

    [Test]
    public void Preserves_high_bit_values()
    {
        // Value at 0xFFFF...FFFF stresses the UInt64LittleEndian path.
        Span<byte> buffer = stackalloc byte[MemoryAllocEventCodec.EventSize];
        MemoryAllocEventCodec.WriteMemoryAllocEvent(
            buffer,
            threadSlot: 255,
            timestamp: long.MaxValue,
            direction: MemoryAllocDirection.Alloc,
            sourceTag: ushort.MaxValue,
            sizeBytes: ulong.MaxValue,
            totalAfterBytes: ulong.MaxValue - 1,
            out _);

        var decoded = MemoryAllocEventCodec.DecodeMemoryAllocEvent(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.ThreadSlot, Is.EqualTo((byte)255));
            Assert.That(decoded.Timestamp, Is.EqualTo(long.MaxValue));
            Assert.That(decoded.SourceTag, Is.EqualTo(ushort.MaxValue));
            Assert.That(decoded.SizeBytes, Is.EqualTo(ulong.MaxValue));
            Assert.That(decoded.TotalAfterBytes, Is.EqualTo(ulong.MaxValue - 1));
        });
    }
}
