using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// SUB-04's inbound half: the ingress ring's release/acquire pairs, driven by two real threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot prove.</b> On x64 the hardware supplies the ordering whether or not the code asks for it, so a green run here is evidence of
/// framing correctness rather than of memory ordering; the ordering claim is only genuinely exercised on arm64, which is why this fixture is one of the cases
/// the nightly runs natively there (archive/Subscriptions/foundation/05-ingress-rings.md § 5, criterion 7). What the fixture buys on every architecture is a
/// check that CAN fail — see the mutant below, which drives the same assertion over a buffer that publishes its cursor before its bytes.
/// </para>
/// <para>
/// <b>Every payload byte is derived from the record's own sequence number</b>, so a record observed through a head that ran ahead of its bytes is caught by
/// its own contents rather than inferred from a count. A zero byte, a byte from the previous tenant of that slot, or a torn boundary all fail the same check.
/// </para>
/// </remarks>
[TestFixture]
unsafe class IngressRingOrderingTests
{
    private const int Capacity = 4096;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private PinnedMemoryBlock _block;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "IngressRingOrderingTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "IngressOrderingAllocator" });
        _block = _allocator.AllocatePinned("IngressOrderingBuffer", _registry.Runtime, Capacity, true, 64);
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    /// <summary>
    /// A producer thread frames records while a consumer drains them; every record that arrives is whole and self-consistent.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-04")]
    public void BytesArePublishedBeforeTheHeadRelease()
    {
        const int Records = 20_000;
        var ring = new IngressRing(_block.DataAsPointer, Capacity);

        var produced = 0;
        var dropped = 0;
        var checkedCount = 0;
        Exception failure = null;

        var stop = 0;
        var producer = Task.Run(() =>
        {
            Span<byte> record = stackalloc byte[64];
            for (var i = 0; i < Records; i++)
            {
                var length = 8 + (i % 48);
                Fill(record[..length], (uint)i);
                if (ring.TryWrite(record[..length]))
                {
                    produced++;
                }
                else
                {
                    dropped++;
                }
            }

            Volatile.Write(ref stop, 1);
        });

        var consumer = Task.Run(() =>
        {
            var scratch = new byte[Capacity];
            try
            {
                while (true)
                {
                    var done = Volatile.Read(ref stop) == 1;
                    var written = ring.Drain(scratch, out var records);
                    if (records == 0 && done)
                    {
                        return;
                    }

                    var framed = new ReadOnlySpan<byte>(scratch, 0, written);
                    while (!framed.IsEmpty)
                    {
                        var payload = IngressRing.ReadFramed(framed, out var consumed);
                        framed = framed[consumed..];
                        AssertRecordIsWhole(payload);
                        checkedCount++;
                    }
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        Assert.That(Task.WaitAll([producer, consumer], TimeSpan.FromSeconds(10)), Is.True, "the two-thread ring case did not finish");

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null, "a drained record was not whole; the head was observed ahead of the bytes it guards");
            Assert.That(checkedCount, Is.EqualTo(produced), "every published record must be drained exactly once");
            Assert.That(produced + dropped, Is.EqualTo(Records), "every write either published or was counted as a drop");
            Assert.That(ring.DroppedRecords, Is.EqualTo(dropped), "the ring's own drop counter must agree with the producer's");
        });
    }

    /// <summary>
    /// The genuineness proof: the same checking loop, over a buffer that advances its cursor before writing the bytes, must fail.
    /// </summary>
    /// <remarks>
    /// The mutant is a buffer rather than an edit to <c>IngressRing</c>, because the property under test is the ORDER of two stores and a production type
    /// cannot be asked to get it wrong. Inverting the order in program order — cursor, pause, bytes — makes the violation observable on any architecture,
    /// which is precisely what proves the assertion is the thing catching it rather than the hardware.
    /// </remarks>
    [Test]
    [RuleMutant("SUB-04")]
    public void AHeadPublishedBeforeItsBytesIsDetected()
    {
        var buffer = new byte[256];
        var head = 0;
        var caught = 0;

        var writer = Task.Run(() =>
        {
            Span<byte> record = stackalloc byte[24];
            for (var i = 0; i < 500; i++)
            {
                Volatile.Write(ref head, 0);
                Array.Clear(buffer);

                // THE MUTATION: the cursor is published before the payload lands. A release store AFTER the bytes is what SUB-04 requires, and its absence is
                // exactly what the reader below is supposed to notice.
                Volatile.Write(ref head, 24);
                Thread.SpinWait(400);
                Fill(record, (uint)i);
                record.CopyTo(buffer);
                Thread.SpinWait(400);
            }
        });

        while (!writer.IsCompleted)
        {
            if (Volatile.Read(ref head) == 0)
            {
                continue;
            }

            try
            {
                AssertRecordIsWhole(new ReadOnlySpan<byte>(buffer, 0, 24));
            }
            catch (InvalidOperationException)
            {
                caught++;
            }
        }

        writer.Wait(TimeSpan.FromSeconds(10));
        Assert.That(caught, Is.GreaterThan(0),
            "the verifier's own assertion never rejected a record published ahead of its bytes, so a green BytesArePublishedBeforeTheHeadRelease proves nothing");
    }

    /// <summary>Writes a record whose every byte follows from <paramref name="sequence"/>, so its contents alone say whether it is whole.</summary>
    private static void Fill(Span<byte> record, uint sequence)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record, sequence);
        for (var i = 4; i < record.Length; i++)
        {
            record[i] = (byte)(sequence + (uint)i);
        }
    }

    /// <summary>
    /// Requires every byte of a drained record to match the sequence number it carries.
    /// </summary>
    /// <param name="payload">The drained record.</param>
    /// <exception cref="InvalidOperationException">The record is not whole.</exception>
    /// <remarks>
    /// A plain throw rather than <c>Assert</c>: this runs on threads NUnit does not own, and an assertion raised off the test thread is recorded against
    /// whatever test the runner happens to be executing. The caller turns it back into an assertion where that is safe.
    /// </remarks>
    private static void AssertRecordIsWhole(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidOperationException("a record shorter than its own sequence number cannot have been framed by the producer");
        }

        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        for (var i = 4; i < payload.Length; i++)
        {
            if (payload[i] != (byte)(sequence + (uint)i))
            {
                throw new InvalidOperationException(
                    $"record {sequence} byte {i} is {payload[i]}, expected {(byte)(sequence + (uint)i)}: the record was read before it was whole");
            }
        }
    }
}
