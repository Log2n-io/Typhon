using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Tests for <see cref="RecordDecoder.SetCurrentTickForContinuation(int)"/> — the seed variant used when decoding a continuation chunk
/// (intra-tick split, chunker v8+). Verifies that events in a continuation chunk carry the correct tick number even though no
/// <see cref="TraceEventKind.TickStart"/> record is present at the head.
/// </summary>
[TestFixture]
public class RecordDecoderContinuationTests
{
    private const int CommonHeaderSize = 12;
    /// <summary>
    /// Size of a <see cref="TraceEventKind.TickEnd"/> record: common header + u8 overloadLevel + u8 tickMultiplier. Using TickEnd for our
    /// synthetic events because it's a simple kind that (a) always decodes to a non-null <c>LiveTraceEvent</c>, (b) does NOT advance the
    /// tick counter (unlike TickStart), and (c) has a minimal 2-byte payload we can leave zeroed.
    /// </summary>
    private const int TickEndRecordSize = CommonHeaderSize + 2;
    /// <summary>Size of a <see cref="TraceEventKind.TickStart"/> record: common header only (empty payload).</summary>
    private const int TickStartRecordSize = CommonHeaderSize;

    /// <summary>
    /// Continuation-chunk path: seed via <c>SetCurrentTickForContinuation(N)</c>, feed a byte stream of Instant records (no TickStart),
    /// assert every decoded event's <c>TickNumber</c> equals N.
    /// </summary>
    [Test]
    public void SetCurrentTickForContinuation_AllEventsTaggedWithSeedTick()
    {
        const int seedTick = 42;
        const int recordCount = 10;
        var bytes = new byte[recordCount * TickEndRecordSize];
        for (var i = 0; i < recordCount; i++)
        {
            WriteInstantRecord(bytes, i * TickEndRecordSize, ts: 100 + i);
        }

        var decoder = new RecordDecoder(timestampFrequency: 10_000_000);
        decoder.SetCurrentTickForContinuation(seedTick);

        var events = new List<LiveTraceEvent>();
        decoder.DecodeBlock(bytes, events);

        Assert.That(events.Count, Is.EqualTo(recordCount), "every well-formed record should decode");
        foreach (var e in events)
        {
            Assert.That(e.TickNumber, Is.EqualTo(seedTick),
                "continuation-chunk decoding must tag every event with the seed tick (no pre-decrement, no waiting for TickStart)");
        }
    }

    /// <summary>
    /// Contrast test — verify that the non-continuation path still works: seed via <c>SetCurrentTick(N-1)</c>, include a TickStart
    /// at the head, assert every event from the TickStart onward is tagged with N. Protects against a regression where someone
    /// "cleans up" the two seed methods into one.
    /// </summary>
    [Test]
    public void SetCurrentTick_WithLeadingTickStart_BumpsCounterToFromTick()
    {
        const int fromTick = 42;
        const int instantCount = 9;
        // Layout: [TickStart: 12 B][Instant × 9: 20 B each] = 12 + 180 = 192 B.
        var bytes = new byte[TickStartRecordSize + instantCount * TickEndRecordSize];
        WriteTickStartRecord(bytes, 0, ts: 100);
        for (var i = 0; i < instantCount; i++)
        {
            WriteInstantRecord(bytes, TickStartRecordSize + i * TickEndRecordSize, ts: 100 + i + 1);
        }

        var decoder = new RecordDecoder(timestampFrequency: 10_000_000);
        decoder.SetCurrentTick(fromTick - 1);   // conventional seed for non-continuation chunks

        var events = new List<LiveTraceEvent>();
        decoder.DecodeBlock(bytes, events);

        Assert.That(events.Count, Is.EqualTo(1 + instantCount));
        // The TickStart record itself is tagged with fromTick (the decoder increments BEFORE tagging).
        foreach (var e in events)
        {
            Assert.That(e.TickNumber, Is.EqualTo(fromTick), "TickStart bumped counter to fromTick, all events land on it");
        }
    }

    /// <summary>
    /// Continuation chunk that itself contains a TickStart partway through (e.g., a chunk that carries the tail of one tick plus
    /// the whole of the next): events before the in-block TickStart stay on the seed tick, events after get the incremented value.
    /// This is the most nuanced case — a continuation chunk is NOT required to stay on one tick; it can contain subsequent ticks too.
    /// </summary>
    [Test]
    public void ContinuationChunk_WithInternalTickStart_IncrementsAtBoundary()
    {
        const int seedTick = 42;
        const int headInstants = 5;
        const int tailInstants = 4;
        // Layout: [Instant × 5][TickStart][Instant × 4]
        var bytes = new byte[headInstants * TickEndRecordSize + TickStartRecordSize + tailInstants * TickEndRecordSize];
        var offset = 0;
        for (var i = 0; i < headInstants; i++)
        {
            WriteInstantRecord(bytes, offset, ts: 100 + i);
            offset += TickEndRecordSize;
        }
        WriteTickStartRecord(bytes, offset, ts: 200);
        offset += TickStartRecordSize;
        for (var i = 0; i < tailInstants; i++)
        {
            WriteInstantRecord(bytes, offset, ts: 200 + i + 1);
            offset += TickEndRecordSize;
        }

        var decoder = new RecordDecoder(timestampFrequency: 10_000_000);
        decoder.SetCurrentTickForContinuation(seedTick);

        var events = new List<LiveTraceEvent>();
        decoder.DecodeBlock(bytes, events);

        Assert.That(events.Count, Is.EqualTo(headInstants + 1 + tailInstants));
        for (var i = 0; i < headInstants; i++)
        {
            Assert.That(events[i].TickNumber, Is.EqualTo(seedTick),
                $"event {i} (before internal TickStart) belongs to seed tick");
        }
        for (var i = headInstants; i < events.Count; i++)
        {
            Assert.That(events[i].TickNumber, Is.EqualTo(seedTick + 1),
                $"event {i} (TickStart or after) belongs to the next tick");
        }
    }

    /// <summary>Write a 14-byte TickEnd record at <paramref name="offset"/>. Payload (overloadLevel, tickMultiplier) is zero-filled.</summary>
    private static void WriteInstantRecord(byte[] bytes, int offset, long ts)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), TickEndRecordSize);
        bytes[offset + 2] = (byte)TraceEventKind.TickEnd;
        bytes[offset + 3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 4, 8), ts);
        // Payload bytes 12..13 are overloadLevel + tickMultiplier; zero is a valid, decodable value.
    }

    /// <summary>Write a 12-byte TickStart record at <paramref name="offset"/>.</summary>
    private static void WriteTickStartRecord(byte[] bytes, int offset, long ts)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), TickStartRecordSize);
        bytes[offset + 2] = (byte)TraceEventKind.TickStart;
        bytes[offset + 3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 4, 8), ts);
    }
}
