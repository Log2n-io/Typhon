using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #956 — the per-session inbound ring: SPSC framing over native memory, ordered publication, and an overflow that drops rather than blocks.
/// </summary>
/// <remarks>
/// <para>
/// The properties here are the ones a client notices when they fail. Order is the contract SUB-08 rests on; a dropped record must be counted rather than
/// silently lost; and the producer is a network thread, so a full ring must never make it wait or throw.
/// </para>
/// <para>
/// The buffer is real native memory from the engine allocator, not a fixture array — partly because that is what the ring is for, and partly because "it is
/// not a GC object" is itself one of the acceptance criteria. v1 put its buffer on the pinned object heap, which stops the GC moving an array but not freeing
/// it, and a pointer into one was the SWG x64 heap-corruption crash.
/// </para>
/// </remarks>
[TestFixture]
unsafe class IngressRingTests
{
    private const int Capacity = 4096;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private PinnedMemoryBlock _block;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "IngressRingTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "IngressRingAllocator" });
        _block = _allocator.AllocatePinned("IngressRingBuffer", _registry.Runtime, Capacity, true, 64);
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private IngressRing NewRing() => new(_block.DataAsPointer, Capacity);

    private static byte[] Record(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    /// <summary>Walks a drained buffer back into the individual payloads the producer wrote.</summary>
    private static List<byte[]> Unframe(ReadOnlySpan<byte> drained, int records)
    {
        var result = new List<byte[]>(records);
        var offset = 0;
        for (var i = 0; i < records; i++)
        {
            var payload = IngressRing.ReadFramed(drained[offset..], out var consumed);
            result.Add(payload.ToArray());
            offset += consumed;
        }

        return result;
    }

    [Test]
    public void AFreshRingIsEmptyAndDropsNothing()
    {
        var ring = NewRing();

        Assert.Multiple(() =>
        {
            Assert.That(ring.IsEmpty, Is.True);
            Assert.That(ring.BytesPending, Is.Zero);
            Assert.That(ring.DroppedRecords, Is.Zero);
            Assert.That(ring.Capacity, Is.EqualTo(Capacity));
        });
    }

    [Test]
    public void ARecordSurvivesTheRoundTripByteForByte()
    {
        var ring = NewRing();
        var payload = Record(37, 0x40);

        Assert.That(ring.TryWrite(payload), Is.True);
        Assert.That(ring.IsEmpty, Is.False, "a published record must be visible to the consumer");

        // Unframed before the assertion block: a Span local cannot be captured by the lambda Assert.Multiple takes.
        Span<byte> destination = stackalloc byte[256];
        var bytes = ring.Drain(destination, out var records);
        var roundTripped = Unframe(destination[..bytes], records);

        Assert.Multiple(() =>
        {
            Assert.That(records, Is.EqualTo(1));
            Assert.That(roundTripped[0], Is.EqualTo(payload));
            Assert.That(ring.IsEmpty, Is.True, "draining must free the space it consumed");
        });
    }

    /// <summary>
    /// Per-session order is what SUB-08 rests on, and it is the one property a client cannot recover from losing: commands are intents applied in sequence.
    /// </summary>
    [Test]
    public void RecordsDrainInTheOrderTheyWereWritten()
    {
        var ring = NewRing();
        var written = new List<byte[]>();
        for (var i = 0; i < 24; i++)
        {
            var payload = Record(8 + (i % 5), (byte)i);
            Assert.That(ring.TryWrite(payload), Is.True);
            written.Add(payload);
        }

        Span<byte> destination = stackalloc byte[2048];
        var bytes = ring.Drain(destination, out var records);

        Assert.That(records, Is.EqualTo(written.Count));
        Assert.That(Unframe(destination[..bytes], records), Is.EqualTo(written));
    }

    /// <summary>
    /// Odd-length payloads are the case the even-size invariant exists for: without the rounding, a record could start at an odd offset and the two-byte size
    /// prefix could straddle the wrap boundary.
    /// </summary>
    [Test]
    public void OddAndEvenLengthsInterleaveWithoutCorruption()
    {
        var ring = NewRing();
        var written = new List<byte[]>();
        for (var length = 1; length <= 24; length++)
        {
            var payload = Record(length, (byte)(length * 7));
            Assert.That(ring.TryWrite(payload), Is.True);
            written.Add(payload);
        }

        Span<byte> destination = stackalloc byte[2048];
        var bytes = ring.Drain(destination, out var records);

        Assert.That(Unframe(destination[..bytes], records), Is.EqualTo(written));
    }

    /// <summary>
    /// The wrap sentinel, exercised by writing far more bytes than the ring holds while draining as we go. Without the sentinel a record would be split across
    /// the buffer end and the consumer would read a size field out of two unrelated halves.
    /// </summary>
    [Test]
    public void WritingPastTheBufferEndWrapsWithoutLosingOrReorderingRecords()
    {
        var ring = NewRing();
        var expected = new List<byte[]>();
        var received = new List<byte[]>();

        // Hoisted, and an array rather than a stackalloc: a stackalloc inside the loop below would not be freed per iteration — the frame keeps growing until
        // the method returns, so 4 000 iterations of a 4 KiB reservation is a stack overflow rather than a reused buffer.
        var scratch = new byte[Capacity];

        for (var i = 0; i < 4_000; i++)
        {
            var payload = Record(17 + (i % 23), (byte)i);
            if (!ring.TryWrite(payload))
            {
                // Full: drain and retry once, which is what a producer paced by its own rate limit would experience.
                var midBytes = ring.Drain(scratch, out var midRecords);
                received.AddRange(Unframe(scratch.AsSpan(0, midBytes), midRecords));
                Assert.That(ring.TryWrite(payload), Is.True, "a drained ring must accept the record that had just been refused");
            }

            expected.Add(payload);
        }

        int tailBytes;
        do
        {
            tailBytes = ring.Drain(scratch, out var tailRecords);
            received.AddRange(Unframe(scratch.AsSpan(0, tailBytes), tailRecords));
        }
        while (tailBytes > 0);

        Assert.That(received, Is.EqualTo(expected), "every record must come back exactly once, in order, across many wraps");
    }

    /// <summary>
    /// A full ring must refuse and count, never block and never throw — the producer is a transport thread and the tick must never be able to stall it.
    /// </summary>
    [Test]
    public void AFullRingDropsAndCountsAndNeitherBlocksNorThrows()
    {
        var ring = NewRing();
        var accepted = 0;
        var payload = Record(200, 0x11);

        for (var i = 0; i < 200; i++)
        {
            if (ring.TryWrite(payload))
            {
                accepted++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.GreaterThan(0), "some records must fit, or this proves nothing about the full case");
            Assert.That(accepted, Is.LessThan(200), "the ring must actually have filled");
            Assert.That(ring.DroppedRecords, Is.EqualTo(200 - accepted), "every refusal must be counted");
            Assert.That(ring.DroppedBytes, Is.EqualTo((200 - accepted) * 200L));
        });
    }

    [Test]
    public void DrainingIntoAShortDestinationLeavesTheRestForNextTime()
    {
        var ring = NewRing();
        for (var i = 0; i < 8; i++)
        {
            Assert.That(ring.TryWrite(Record(32, (byte)i)), Is.True);
        }

        // Room for two framed records only. The length is captured into a local because a Span cannot cross into the assertion lambda.
        const int Room = 2 * 34;
        Span<byte> small = stackalloc byte[Room];
        var bytes = ring.Drain(small, out var records);

        Assert.Multiple(() =>
        {
            Assert.That(records, Is.EqualTo(2), "a record that does not fit must be left, not truncated");
            Assert.That(bytes, Is.LessThanOrEqualTo(Room));
            Assert.That(ring.IsEmpty, Is.False, "the remainder is still pending");
        });
    }

    [Test]
    public void AnEmptyOrOversizeRecordIsAProgrammingErrorAndThrows()
    {
        var ring = NewRing();

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.TryWrite(ReadOnlySpan<byte>.Empty), "an empty record carries no command");
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.TryWrite(new byte[Capacity]), "a record larger than the ring can never succeed");
            Assert.That(ring.DroppedRecords, Is.Zero, "a rejected argument is not an overflow drop and must not be counted as one");
        });
    }

    [Test]
    public void ResetClearsCursorsAndCountersForTheNextSession()
    {
        var ring = NewRing();
        var payload = Record(64, 0x7A);
        while (ring.TryWrite(payload))
        {
        }

        Assert.That(ring.DroppedRecords, Is.GreaterThan(0));

        ring.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(ring.IsEmpty, Is.True);
            Assert.That(ring.DroppedRecords, Is.Zero);
            Assert.That(ring.DroppedBytes, Is.Zero);
            Assert.That(ring.TryWrite(payload), Is.True, "a reset ring is usable again");
        });
    }

    /// <summary>
    /// The real topology: one producer thread and one consumer thread, which is what the release/acquire pairs exist for. A ring that relied on x64 store
    /// ordering — as v1's SendBuffer did — passes single-threaded tests and is still wrong on arm64, so this at least drives the concurrent path.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void OneProducerAndOneConsumerPreserveEveryRecordInOrder()
    {
        var ring = NewRing();
        const int Total = 20_000;
        var received = new List<byte[]>(Total);
        var produced = 0;

        var consumer = new Thread(() =>
        {
            var buffer = new byte[Capacity];
            while (received.Count < Total)
            {
                var bytes = ring.Drain(buffer, out var records);
                if (records > 0)
                {
                    received.AddRange(Unframe(buffer.AsSpan(0, bytes), records));
                }

                if (bytes == 0)
                {
                    Thread.SpinWait(8);
                }
            }
        });

        consumer.Start();

        for (var i = 0; i < Total; i++)
        {
            var payload = Record(12 + (i % 31), (byte)i);
            while (!ring.TryWrite(payload))
            {
                Thread.SpinWait(8);   // full: the producer never blocks on a lock, it simply retries
            }

            produced++;
        }

        Assert.That(consumer.Join(TimeSpan.FromSeconds(20)), Is.True, "the consumer must drain everything the producer wrote");
        Assert.That(produced, Is.EqualTo(Total));
        Assert.That(received, Has.Count.EqualTo(Total), "no record may be lost when the producer retries rather than dropping");

        for (var i = 0; i < Total; i++)
        {
            Assert.That(received[i], Is.EqualTo(Record(12 + (i % 31), (byte)i)), $"record {i} came back changed or out of order");
        }
    }
}
