using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler.Events;

/// <summary>
/// Unit tests for <see cref="TraceRecordRing"/> — the new variable-size SPSC ring buffer that replaces the fixed-stride
/// <c>ThreadTraceBuffer</c> in Phase 3. Covers: basic reserve/publish/drain, size rounding, overflow, wrap sentinel handling, multi-record
/// sequential drain, and SPSC producer/consumer correctness under real concurrency.
/// </summary>
[TestFixture]
public class TraceRecordRingTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Write a fabricated minimum-size record whose u16 size field correctly reflects <paramref name="recordSize"/>, so the drain pass can walk
    /// it. Uses a specific <paramref name="marker"/> byte at offset 2 (the kind byte slot) to let tests distinguish records from each other.
    /// </summary>
    private static bool WriteTestRecord(TraceRecordRing ring, int recordSize, byte marker)
    {
        if (!ring.TryReserve(recordSize, out var slot))
        {
            return false;
        }

        slot.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)recordSize);
        slot[2] = marker; // kind byte — used as a test fingerprint
        // offset 3 = threadSlot, left at 0
        // offset 4..11 = timestamp, left at 0
        ring.Publish();
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Basic reserve/publish/drain
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reserve_Publish_Drain_SingleRecord_RoundTrips()
    {
        var ring = new TraceRecordRing(1024);
        Assert.That(WriteTestRecord(ring, recordSize: 12, marker: 0xAA), Is.True);

        Span<byte> drainBuffer = stackalloc byte[256];
        var drained = ring.Drain(drainBuffer);

        Assert.That(drained, Is.EqualTo(12));
        Assert.That(drainBuffer[2], Is.EqualTo(0xAA));
        Assert.That(ring.IsEmpty, Is.True);
    }

    [Test]
    public void Drain_EmptyRing_ReturnsZero()
    {
        var ring = new TraceRecordRing(1024);
        Span<byte> drainBuffer = stackalloc byte[64];

        Assert.That(ring.Drain(drainBuffer), Is.EqualTo(0));
        Assert.That(ring.IsEmpty, Is.True);
    }

    [Test]
    public void Drain_MultipleRecordsInOrder_AllEmergeContiguously()
    {
        var ring = new TraceRecordRing(1024);
        for (byte i = 1; i <= 10; i++)
        {
            Assert.That(WriteTestRecord(ring, recordSize: 12, marker: i), Is.True, $"record {i} reserve failed");
        }

        Span<byte> drainBuffer = stackalloc byte[256];
        var drained = ring.Drain(drainBuffer);

        Assert.That(drained, Is.EqualTo(120)); // 10 × 12
        for (byte i = 1; i <= 10; i++)
        {
            Assert.That(drainBuffer[(i - 1) * 12 + 2], Is.EqualTo(i), $"record {i} not in expected order");
        }
    }

    [Test]
    public void OddSizeReservation_IsRoundedUpToEven()
    {
        var ring = new TraceRecordRing(1024);
        // Request 37 bytes (BTreeInsert span header size — odd)
        Assert.That(ring.TryReserve(37, out var slot), Is.True);
        Assert.That(slot.Length, Is.EqualTo(38), "Odd sizes are rounded up to even");

        // Fill with a valid-looking record claiming the original 37 bytes
        slot.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(slot, 37);
        slot[2] = 0xBB;
        ring.Publish();

        // Consumer drains the actual record size (37), not the rounded 38
        Span<byte> dst = stackalloc byte[64];
        var drained = ring.Drain(dst);
        Assert.That(drained, Is.EqualTo(37), "Drain returns the record's declared size, not the rounded reservation");
        Assert.That(dst[2], Is.EqualTo(0xBB));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Overflow / drop-newest
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void FullRing_DropsOnTryReserve_IncrementsDroppedCounter()
    {
        // Small 128-byte ring; fill it with 12-byte records until full.
        var ring = new TraceRecordRing(128);
        var accepted = 0;

        while (WriteTestRecord(ring, recordSize: 12, marker: 1))
        {
            accepted++;
            if (accepted > 100) Assert.Fail("Ring should have filled up by now");
        }

        // The drop that broke the loop also bumped the counter.
        Assert.That(accepted, Is.GreaterThan(0));
        Assert.That(ring.DroppedEvents, Is.EqualTo(1),
            "The failing reservation that broke the fill loop incremented the drop counter");

        // Further failed reservations should keep incrementing the counter
        Assert.That(ring.TryReserve(12, out _), Is.False);
        Assert.That(ring.DroppedEvents, Is.EqualTo(2));
    }

    [Test]
    public void DrainingMakesRoom_ProducerCanResume()
    {
        var ring = new TraceRecordRing(128);
        // Fill it up
        while (WriteTestRecord(ring, recordSize: 12, marker: 1)) { }

        // Drain everything
        Span<byte> drainBuffer = stackalloc byte[256];
        ring.Drain(drainBuffer);
        Assert.That(ring.IsEmpty, Is.True);

        // Producer should be able to write again
        Assert.That(WriteTestRecord(ring, recordSize: 12, marker: 2), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Wrap sentinel
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WrapSentinel_SkippedByDrain_RecordsOnBothSidesOfWrapEmergeContiguously()
    {
        // Small ring, sized so that after writing ~9 records of 12 B each we're near the wrap, and the 10th record has to trigger a wrap.
        var ring = new TraceRecordRing(128);

        // Write 6 × 20 B records = 120 B (head at offset 120, 8 bytes remain before wrap)
        for (byte i = 1; i <= 6; i++)
        {
            Assert.That(WriteTestRecord(ring, recordSize: 20, marker: i), Is.True);
        }

        // Drain first 3 records to free up space so the ring isn't full
        Span<byte> partialDrain = stackalloc byte[60];
        ring.Drain(partialDrain);
        // tail is now at 60; head at 120.

        // Next 20-byte reservation: head offset 120, 8 bytes to end → must wrap.
        // Reservation needs 20 B after wrap, and wrap sentinel consumes 8 B — total 28 B needed from free space.
        // Free space = 128 - (120 - 60) = 68 B. 28 ≤ 68, so the reserve succeeds.
        Assert.That(WriteTestRecord(ring, recordSize: 20, marker: 7), Is.True,
            "Wrap should trigger a sentinel-based skip and the new record lands at offset 0");

        // Drain everything remaining
        Span<byte> finalDrain = stackalloc byte[256];
        var drained = ring.Drain(finalDrain);

        // We should see records 4, 5, 6, 7 (records 1-3 were drained earlier)
        // The wrap sentinel must be invisible in the drained stream.
        Assert.That(drained, Is.EqualTo(80), "4 records × 20 bytes = 80 bytes, wrap sentinel must be skipped");
        Assert.That(finalDrain[2], Is.EqualTo(4));
        Assert.That(finalDrain[22], Is.EqualTo(5));
        Assert.That(finalDrain[42], Is.EqualTo(6));
        Assert.That(finalDrain[62], Is.EqualTo(7), "Record 7 emerged on the other side of the wrap");
    }

    [Test]
    public void WrapSentinel_ProducerDrains_NoBuildup()
    {
        // Stress the wrap path: tight loop of reserve-publish-drain, ensuring the ring's head position wraps many times.
        var ring = new TraceRecordRing(128);
        Span<byte> drainBuffer = stackalloc byte[256];
        const int iterations = 10_000;

        for (var i = 0; i < iterations; i++)
        {
            Assert.That(WriteTestRecord(ring, recordSize: 14, marker: (byte)(i & 0xFF)), Is.True);
            var drained = ring.Drain(drainBuffer);
            Assert.That(drained, Is.EqualTo(14));
            Assert.That(drainBuffer[2], Is.EqualTo((byte)(i & 0xFF)));
        }

        Assert.That(ring.DroppedEvents, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // End-to-end integration with real typed events
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RealTypedEvent_EcsQueryExecute_RoundTripsThroughRing()
    {
        var ring = new TraceRecordRing(1024);

        var evt = new EcsQueryExecuteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = 5,
                StartTimestamp = 1000,
                SpanId = 0x0500000000000001UL,
            },
            ArchetypeTypeId = 77,
        };
        evt.ResultCount = 42;
        evt.ScanMode = EcsQueryScanMode.Targeted;

        var size = evt.ComputeSize();
        Assert.That(ring.TryReserve(size, out var slot), Is.True);
        evt.EncodeTo(slot, endTimestamp: 1500, out var written);
        Assert.That(written, Is.EqualTo(size));
        ring.Publish();

        Span<byte> drainBuffer = stackalloc byte[256];
        var drained = ring.Drain(drainBuffer);
        Assert.That(drained, Is.EqualTo(size));

        var decoded = (Typhon.Profiler.Events.EcsQueryExecuteEventDto)Typhon.Profiler.Events.TraceEventDecoder.Decode(drainBuffer[..drained], 0, 1);
        Assert.That(decoded.KindByte, Is.EqualTo((byte)TraceEventKind.EcsQueryExecute));
        Assert.That(decoded.ArchetypeTypeId, Is.EqualTo(77));
        Assert.That(decoded.ResultCount, Is.EqualTo(42));
        Assert.That(decoded.ScanMode, Is.EqualTo(EcsQueryScanMode.Targeted));
        Assert.That(decoded.DurationUs, Is.EqualTo(500.0));
    }

    [Test]
    public void MixedTypedEvents_RoundTripThroughRing()
    {
        var ring = new TraceRecordRing(2048);

        // Write one of each: BTreeInsert, TransactionCommit, PageCacheFetch
        var btInsert = new BTreeInsertEvent { Header = new TraceSpanHeader { ThreadSlot = 1, StartTimestamp = 100, SpanId = 1 } };
        {
            Assert.That(ring.TryReserve(btInsert.ComputeSize(), out var slot), Is.True);
            btInsert.EncodeTo(slot, endTimestamp: 200, out _);
            ring.Publish();
        }

        var txCommit = new TransactionCommitEvent { Header = new TraceSpanHeader { ThreadSlot = 2, StartTimestamp = 300, SpanId = 2 }, Tsn = 9999 };
        txCommit.ComponentCount = 3;
        {
            Assert.That(ring.TryReserve(txCommit.ComputeSize(), out var slot), Is.True);
            txCommit.EncodeTo(slot, endTimestamp: 500, out _);
            ring.Publish();
        }

        var pcFetch = new PageCacheFetchEvent { Header = new TraceSpanHeader { ThreadSlot = 3, StartTimestamp = 600, SpanId = 3 }, FilePageIndex = 42 };
        {
            Assert.That(ring.TryReserve(pcFetch.ComputeSize(), out var slot), Is.True);
            pcFetch.EncodeTo(slot, endTimestamp: 700, out _);
            ring.Publish();
        }

        // Drain and walk the record stream. The drain strips padding, so records in the destination are packed contiguously without the
        // even-size padding that lives inside the ring. Walker advances by the declared size, not by the rounded reservation size.
        var drainBuffer = new byte[512];
        var drained = ring.Drain(drainBuffer);
        var pos = 0;

        // Record 1: BTreeInsert
        var size1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(drainBuffer.AsSpan(pos));
        var btData = (Typhon.Profiler.Events.BTreeInsertEventDto)Typhon.Profiler.Events.TraceEventDecoder.Decode(drainBuffer.AsSpan(pos, size1), 0, 1);
        Assert.That(btData.KindByte, Is.EqualTo((byte)TraceEventKind.BTreeInsert));
        Assert.That(btData.DurationUs, Is.EqualTo(100.0));
        pos += size1;

        // Record 2: TransactionCommit
        var size2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(drainBuffer.AsSpan(pos));
        var txData = (Typhon.Profiler.Events.TransactionCommitEventDto)Typhon.Profiler.Events.TraceEventDecoder.Decode(drainBuffer.AsSpan(pos, size2), 0, 1);
        Assert.That(txData.KindByte, Is.EqualTo((byte)TraceEventKind.TransactionCommit));
        Assert.That(txData.Tsn, Is.EqualTo(9999L));
        Assert.That(txData.ComponentCount, Is.EqualTo(3));
        pos += size2;

        // Record 3: PageCacheFetch
        var size3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(drainBuffer.AsSpan(pos));
        var pcData = (Typhon.Profiler.Events.PageCacheFetchEventDto)Typhon.Profiler.Events.TraceEventDecoder.Decode(drainBuffer.AsSpan(pos, size3), 0, 1);
        Assert.That(pcData.KindByte, Is.EqualTo((byte)TraceEventKind.PageCacheFetch));
        Assert.That(pcData.FilePageIndex, Is.EqualTo(42));
        pos += size3;

        Assert.That(pos, Is.EqualTo(drained), "Walked exactly the drained bytes (no padding in the drained stream)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reset
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reset_ClearsStateForNewOwner()
    {
        var ring = new TraceRecordRing(128);
        WriteTestRecord(ring, recordSize: 12, marker: 1);
        while (WriteTestRecord(ring, recordSize: 12, marker: 1)) { } // fill + drop once
        Assert.That(ring.DroppedEvents, Is.GreaterThan(0));

        ring.Reset();

        Assert.That(ring.IsEmpty, Is.True);
        Assert.That(ring.DroppedEvents, Is.EqualTo(0));
        Assert.That(ring.BytesPending, Is.EqualTo(0));
        // Should be able to write fresh
        Assert.That(WriteTestRecord(ring, recordSize: 12, marker: 0xEE), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SPSC concurrency
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConcurrentSPSC_OneProducer_OneConsumer_PreservesRecordOrdering()
    {
        var ring = new TraceRecordRing(2048);
        const int recordCount = 50_000;
        const int recordSize = 16;

        var producerDone = 0;
        var consumerRecords = new byte[recordCount];
        var consumedCount = 0;

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < recordCount; i++)
            {
                // Producer retries on drop — the test guarantees no record is lost because the producer keeps trying until acceptance.
                // Each failed attempt does bump the ring's drop counter (by design: drop counter counts TryReserve failures, not
                // permanently-lost records), so don't assert on it.
                while (!WriteTestRecord(ring, recordSize, marker: (byte)(i & 0xFF)))
                {
                    Thread.SpinWait(10);
                }
            }
            Volatile.Write(ref producerDone, 1);
        });

        var consumer = Task.Run(() =>
        {
            var buffer = new byte[512];
            while (Volatile.Read(ref producerDone) == 0 || !ring.IsEmpty)
            {
                var drained = ring.Drain(buffer);
                if (drained == 0)
                {
                    Thread.SpinWait(5);
                    continue;
                }

                var pos = 0;
                while (pos < drained && consumedCount < consumerRecords.Length)
                {
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos));
                    consumerRecords[consumedCount++] = buffer[pos + 2]; // kind byte = test marker
                    pos += size;
                }
            }
        });

        Assert.That(Task.WhenAll(producer, consumer).Wait(TimeSpan.FromSeconds(10)), Is.True, "SPSC test must complete within 10 s");

        Assert.That(consumedCount, Is.EqualTo(recordCount), "Every produced record was consumed exactly once");
        for (var i = 0; i < recordCount; i++)
        {
            Assert.That(consumerRecords[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Record ordering broken at index {i}: expected {(byte)(i & 0xFF)} got {consumerRecords[i]}");
        }
    }
}
