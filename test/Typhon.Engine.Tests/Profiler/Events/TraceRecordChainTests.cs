using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler.Events;

/// <summary>
/// Tests for the spillover-chain feature: <see cref="TraceRecordRing.Next"/>, <see cref="ThreadSlot.ChainHead"/>,
/// <see cref="ThreadSlot.ChainTail"/>, the <see cref="SpilloverRingPool"/>, and the producer's chain-aware reservation
/// helper <c>TyphonEvent.TryReserveOnChain</c>.
///
/// Most tests construct a synthetic <see cref="ThreadSlot"/> + primary ring directly so they don't depend on the
/// global <see cref="ThreadSlotRegistry"/> (which is hard to fully reset under parallel test runs). The few tests
/// that DO need the registry (e.g. <c>AssignClaim</c> behaviour) call <see cref="ThreadSlotRegistry.ResetForTests"/>
/// in their setup.
/// </summary>
[TestFixture]
[NonParallelizable] // shares static SpilloverRingPool + ThreadSlotRegistry state across tests; serialize.
public sealed class TraceRecordChainTests
{
    [TearDown]
    public void TearDown()
    {
        SpilloverRingPool.Shutdown();
    }

    /// <summary>Build a fabricated ThreadSlot with a primary ring of the given capacity. Chain pointers wired to primary.</summary>
    private static ThreadSlot MakeSlot(int primaryCapacity)
    {
        var slot = new ThreadSlot
        {
            Buffer = new TraceRecordRing(primaryCapacity),
        };
        slot.ChainHead = slot.Buffer;
        slot.ChainTail = slot.Buffer;
        return slot;
    }

    /// <summary>
    /// Write a fabricated record of given size with a marker byte at offset 2 so we can identify records during
    /// drain. Returns true on success, false if the ring is full. Uses the typed (kind-tracking) overload.
    /// </summary>
    private static bool WriteRecord(TraceRecordRing ring, int size, byte kind, byte marker)
    {
        if (!ring.TryReserve(size, kind, out var dst))
        {
            return false;
        }
        dst.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)size);
        dst[2] = marker;
        ring.Publish();
        return true;
    }

    private static int DrainAll(TraceRecordRing ring, Span<byte> destination)
    {
        return ring.Drain(destination);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reset clears chain link + per-kind counters
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reset_ClearsNextLink()
    {
        var primary = new TraceRecordRing(1024);
        var spill = new TraceRecordRing(1024);
        primary.SetNext(spill);
        Assert.That(primary.Next, Is.SameAs(spill));
        primary.Reset();
        Assert.That(primary.Next, Is.Null, "Reset should clear forward link");
    }

    [Test]
    public void Reset_ClearsPerKindDropCounters()
    {
        var ring = new TraceRecordRing(128);
        var kind = (byte)TraceEventKind.SchedulerChunk;
        // Fill the ring to force a drop.
        while (ring.TryReserve(64, kind, out var dst))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dst, 64);
            ring.Publish();
        }
        Assert.That(ring.DroppedEventsForKind(kind), Is.GreaterThan(0));
        ring.Reset();
        Assert.That(ring.DroppedEventsForKind(kind), Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Single ring, no chain (compatibility)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NoChainExtension_WhenPrimaryDoesNotOverflow()
    {
        SpilloverRingPool.Initialize(2, 64 * 1024);
        var slot = MakeSlot(1024);
        // Reserve via the chain-aware helper. Should land on the primary, no chain extension.
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, (byte)TraceEventKind.TickStart, out var dst, out var reservedOn), Is.True);
        BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
        reservedOn.Publish();
        Assert.That(reservedOn, Is.SameAs(slot.Buffer));
        Assert.That(slot.ChainTail, Is.SameAs(slot.Buffer));
        Assert.That(slot.Buffer.Next, Is.Null);
        Assert.That(SpilloverRingPool.AcquiredCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Two-ring chain (the AntHill case)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ProducerExtendsChain_OnPrimaryOverflow()
    {
        const int spillSize = 64 * 1024;
        SpilloverRingPool.Initialize(2, spillSize);
        var slot = MakeSlot(128); // tiny primary so it overflows immediately
        // Fill the primary to overflow.
        var kind = (byte)TraceEventKind.SchedulerChunk;
        while (WriteRecord(slot.Buffer, 32, kind, 0x11)) { }
        // Now the primary is full. The chain-aware helper should extend.
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out var dst, out var reservedOn), Is.True);
        BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
        dst[2] = 0x22;
        reservedOn.Publish();
        Assert.That(reservedOn, Is.Not.SameAs(slot.Buffer), "extension should land on a spillover");
        Assert.That(slot.Buffer.Next, Is.SameAs(reservedOn), "Next link is set on primary");
        Assert.That(slot.ChainTail, Is.SameAs(reservedOn), "ChainTail advances to spillover");
        Assert.That(slot.ChainHead, Is.SameAs(slot.Buffer), "ChainHead stays on primary until drained");
        Assert.That(SpilloverRingPool.AcquiredCount, Is.EqualTo(1));
    }

    [Test]
    public void DrainReadsPrimaryThenSpillover_InOrder()
    {
        const int primaryCapacity = 128;
        SpilloverRingPool.Initialize(2, 64 * 1024);
        var slot = MakeSlot(primaryCapacity);

        // Fill primary with records marked 0x10..0x1F until it overflows. Track the count.
        var kind = (byte)TraceEventKind.SchedulerChunk;
        var written = new List<byte>();
        byte mark = 0x10;
        while (WriteRecord(slot.Buffer, 32, kind, mark))
        {
            written.Add(mark);
            mark++;
        }
        // Now extend the chain and write some more records.
        for (var i = 0; i < 5; i++)
        {
            Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out var dst, out var reservedOn), Is.True);
            BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
            dst[2] = mark;
            reservedOn.Publish();
            written.Add(mark);
            mark++;
        }

        // Drain — primary first, then chain advances. Loop per-ring until empty (Drain stops on dest-full).
        var destBytes = new byte[8 * 1024];
        Span<byte> dest = destBytes;
        var observed = new List<byte>();
        var head = slot.ChainHead;
        while (head != null)
        {
            while (!head.IsEmpty)
            {
                var drained = head.Drain(dest);
                if (drained == 0) break;
                var pos = 0;
                while (pos < drained)
                {
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(dest[pos..]);
                    observed.Add(dest[pos + 2]);
                    pos += size;
                }
            }
            if (head.Next == null) break;
            head = head.Next;
        }

        CollectionAssert.AreEqual(written, observed, "records must emerge in producer-write order across chain");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pool exhaustion mid-chain
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void PoolExhaustion_MidChain_TriggersDrop()
    {
        SpilloverRingPool.Initialize(1, 64 * 1024);
        var slot = MakeSlot(64);
        var kind = (byte)TraceEventKind.SchedulerChunk;
        // Fill primary
        while (WriteRecord(slot.Buffer, 32, kind, 0x11)) { }
        // First overflow extends to the only spillover
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out _, out var first), Is.True);
        Assert.That(first, Is.Not.SameAs(slot.Buffer));
        // Fill that spillover too
        while (WriteRecord(first, 32, kind, 0x22)) { }
        // Next overflow has no pool buffer to acquire — must fall to drop
        var preDrops = first.DroppedEventsForKind(kind);
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out _, out var second), Is.False);
        Assert.That(second, Is.Null);
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(1), "ExhaustedCount bumped when pool empty");
        Assert.That(first.DroppedEventsForKind(kind), Is.GreaterThan(preDrops),
            "the failed reserve on `first` increments its per-kind drop counter");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Recycling
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RecycleSpillover_AfterDrain_ReturnsToPool_AndIsClean()
    {
        SpilloverRingPool.Initialize(1, 64 * 1024);
        var slot = MakeSlot(64);
        var kind = (byte)TraceEventKind.SchedulerChunk;
        // Fill primary, force extension
        while (WriteRecord(slot.Buffer, 32, kind, 0x11)) { }
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out var dst, out var spill), Is.True);
        BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
        spill.Publish();
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(1));

        // Drain primary completely
        Span<byte> tmp = new byte[16 * 1024];
        slot.Buffer.Drain(tmp);
        // Drain spillover completely
        spill.Drain(tmp);
        Assert.That(slot.Buffer.IsEmpty, Is.True);
        Assert.That(spill.IsEmpty, Is.True);

        // Simulate the consumer's recycle step: ChainHead is on the (empty) primary, Next == spill, recycle spillover
        Assert.That(slot.Buffer.Next, Is.SameAs(spill));
        SpilloverRingPool.Release(spill);
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(0));

        // Re-acquire; should be the same instance, reset.
        var reacquired = SpilloverRingPool.TryAcquire();
        Assert.That(reacquired, Is.SameAs(spill));
        Assert.That(reacquired.IsEmpty, Is.True);
        Assert.That(reacquired.Next, Is.Null);
        Assert.That(reacquired.DroppedEvents, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Deep chain
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DeepChain_ThreeRings_ExtendsAndDrainsInOrder()
    {
        const int capacity = 64;
        SpilloverRingPool.Initialize(8, 64 * 1024);
        var slot = MakeSlot(capacity);
        var kind = (byte)TraceEventKind.SchedulerChunk;
        var written = new List<byte>();
        byte mark = 1;
        // Fill primary
        while (WriteRecord(slot.Buffer, 32, kind, mark))
        {
            written.Add(mark);
            mark++;
        }
        // Extend twice — fill spillover A, fill spillover B
        var fillsRequired = 2;
        for (var fillIdx = 0; fillIdx < fillsRequired; fillIdx++)
        {
            // Extend (one record always succeeds because the new ring is fresh + 64 KiB)
            Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out var dst, out var ringUsed), Is.True);
            BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
            dst[2] = mark;
            ringUsed.Publish();
            written.Add(mark);
            mark++;
            // Now keep filling that ring to overflow
            while (WriteRecord(ringUsed, 32, kind, mark))
            {
                written.Add(mark);
                mark++;
            }
        }
        // One more extend to reach 3-ring chain — write a single record, no fill loop
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kind, out var lastDst, out var lastRing), Is.True);
        BinaryPrimitives.WriteUInt16LittleEndian(lastDst, 32);
        lastDst[2] = mark;
        lastRing.Publish();
        written.Add(mark);

        // Verify chain depth (primary + 3 spillovers)
        var chainDepth = 0;
        var ring = slot.Buffer;
        while (ring != null)
        {
            chainDepth++;
            ring = ring.Next;
        }
        Assert.That(chainDepth, Is.EqualTo(4), "primary + 3 spillovers = 4 rings");

        // Drain in-order — loop per ring until empty since each Drain stops when destination fills.
        var observed = new List<byte>();
        ring = slot.Buffer;
        var tmpBytes = new byte[16 * 1024];
        Span<byte> tmp = tmpBytes;
        while (ring != null)
        {
            while (!ring.IsEmpty)
            {
                int drained = ring.Drain(tmp);
                int pos = 0;
                while (pos < drained)
                {
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(tmp[pos..]);
                    observed.Add(tmp[pos + 2]);
                    pos += size;
                }
                if (drained == 0) break; // safety against infinite loop on malformed data
            }
            ring = ring.Next;
        }
        CollectionAssert.AreEqual(written, observed, "records emerge in producer-write order across deep chain");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SPSC stress
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpscStressWithChainExtension_MaintainsOrdering()
    {
        // Tiny primary forces frequent chain extension; small spillovers force frequent recycling.
        const int primaryCapacity = 4 * 1024;
        const int spillCapacity = 64 * 1024;
        const int recordCount = 50_000;
        const int recordSize = 32;
        SpilloverRingPool.Initialize(8, spillCapacity);
        var slot = MakeSlot(primaryCapacity);
        var kind = (byte)TraceEventKind.SchedulerChunk;

        // Producer task: emit recordCount records via the chain-aware helper. Marker byte rolls over since we
        // only have 1 byte; we encode a 4-byte sequence number into the record body to verify ordering.
        var producer = Task.Run(() =>
        {
            for (var seq = 0; seq < recordCount; seq++)
            {
                while (true)
                {
                    if (TyphonEvent.TryReserveOnChain(slot, recordSize, kind, out var dst, out var ringUsed))
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)recordSize);
                        BinaryPrimitives.WriteInt32LittleEndian(dst[12..], seq);
                        ringUsed.Publish();
                        break;
                    }
                    // Pool exhausted — back off briefly to let consumer recycle
                    Thread.SpinWait(50);
                }
            }
        });

        // Consumer task: drain the chain in-order, recycling spillovers as they empty.
        var consumed = new List<int>(recordCount);
        var consumer = Task.Run(() =>
        {
            Span<byte> tmp = new byte[64 * 1024];
            while (consumed.Count < recordCount)
            {
                var head = slot.ChainHead;
                if (head == null) { Thread.SpinWait(20); continue; }
                int drained = head.Drain(tmp);
                int pos = 0;
                while (pos < drained)
                {
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(tmp[pos..]);
                    var seq = BinaryPrimitives.ReadInt32LittleEndian(tmp[(pos + 12)..]);
                    consumed.Add(seq);
                    pos += size;
                }
                // Walk: if head empty AND has Next, recycle and advance
                if (head.IsEmpty)
                {
                    var next = head.Next;
                    if (next != null)
                    {
                        var spent = head;
                        slot.ChainHead = next;
                        if (spent != slot.Buffer)
                        {
                            SpilloverRingPool.Release(spent);
                        }
                    }
                }
            }
        });

        Assert.That(Task.WaitAll(new[] { producer, consumer }, TimeSpan.FromSeconds(10)), Is.True,
            "stress test should complete within 10 s");
        Assert.That(consumed.Count, Is.EqualTo(recordCount));
        for (var i = 0; i < recordCount; i++)
        {
            Assert.That(consumed[i], Is.EqualTo(i), $"record {i} out of order");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Drop-by-kind aggregation walks chain
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DroppedEventsForKind_Aggregates_FromPrimaryAndSpillovers()
    {
        // We aggregate via slot's ChainHead walk in the test (the production aggregator in TyphonEvent does the
        // same walk via the registry). This test verifies the data structure supports aggregation correctly.
        const int capacity = 128;
        SpilloverRingPool.Initialize(2, 64 * 1024);
        var slot = MakeSlot(capacity);
        var kindPrimary = (byte)TraceEventKind.SchedulerChunk;
        var kindSpill = (byte)TraceEventKind.EcsSpawn;

        // Force drops on primary via overflow with kindPrimary. Capture how many drops accumulate from filling
        // the ring (the exit-on-false call itself counts as one drop) plus N additional explicit drops.
        while (WriteRecord(slot.Buffer, 32, kindPrimary, 0x11)) { }
        var primaryDropsFromFill = slot.Buffer.DroppedEventsForKind(kindPrimary);
        Assert.That(primaryDropsFromFill, Is.GreaterThanOrEqualTo(1));
        var primaryDropAttempts = 5;
        for (var i = 0; i < primaryDropAttempts; i++)
        {
            // these will cascade into chain extension, so use direct ring API to ensure DROP not extension
            slot.Buffer.TryReserve(32, kindPrimary, out _);
        }
        var expectedPrimaryDrops = primaryDropsFromFill + primaryDropAttempts;

        // Extend the chain via the helper. The TryReserveOnChain rescinds the (transient) drop on the primary
        // when the spillover satisfies the record, so primary's kindSpill counter stays 0.
        Assert.That(TyphonEvent.TryReserveOnChain(slot, 32, kindSpill, out var dst, out var spill), Is.True);
        BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
        spill.Publish();
        // Force drops on the spillover with kindSpill. Same accounting: fill-loop's failing call counts as a drop.
        while (WriteRecord(spill, 32, kindSpill, 0x22)) { }
        var spillDropsFromFill = spill.DroppedEventsForKind(kindSpill);
        Assert.That(spillDropsFromFill, Is.GreaterThanOrEqualTo(1));
        var spillDropAttempts = 7;
        for (var i = 0; i < spillDropAttempts; i++)
        {
            spill.TryReserve(32, kindSpill, out _);
        }
        // Total kindSpill drops across chain = spillover's count (primary's transient drop was rescinded).
        var expectedSpillDrops = spillDropsFromFill + spillDropAttempts;

        // Walk the chain and aggregate
        long primaryKindTotal = 0;
        long spillKindTotal = 0;
        var ring = slot.ChainHead;
        while (ring != null)
        {
            primaryKindTotal += ring.DroppedEventsForKind(kindPrimary);
            spillKindTotal += ring.DroppedEventsForKind(kindSpill);
            ring = ring.Next;
        }
        Assert.That(primaryKindTotal, Is.EqualTo(expectedPrimaryDrops));
        Assert.That(spillKindTotal, Is.EqualTo(expectedSpillDrops));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ThreadSlotRegistry interaction (re-claim collapses chain)
    // ═══════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════
    // Pool exhaustion under concurrent producers
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConcurrentProducersOnDifferentSlots_SharePool_EnoughBuffers()
    {
        // 4 producers, 4 buffers in pool — exactly enough; no exhaustion expected.
        SpilloverRingPool.Initialize(4, 64 * 1024);
        const int producerCount = 4;
        var slots = new ThreadSlot[producerCount];
        for (var i = 0; i < producerCount; i++)
        {
            slots[i] = MakeSlot(64);
            // Pre-fill primary so the very first chain-aware reserve will extend.
            while (WriteRecord(slots[i].Buffer, 32, (byte)TraceEventKind.SchedulerChunk, 0x11)) { }
        }
        var barrier = new Barrier(producerCount);
        var acquiredOk = 0;
        var tasks = new Task[producerCount];
        for (var t = 0; t < producerCount; t++)
        {
            var local = slots[t];
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                if (TyphonEvent.TryReserveOnChain(local, 32, (byte)TraceEventKind.SchedulerChunk, out var dst, out var ringUsed))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
                    ringUsed.Publish();
                    Interlocked.Increment(ref acquiredOk);
                }
            });
        }
        Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        Assert.That(acquiredOk, Is.EqualTo(producerCount), "all 4 producers should get a spillover");
        Assert.That(SpilloverRingPool.AcquiredCount, Is.EqualTo(4));
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(0));
    }

    [Test]
    public void ConcurrentProducers_PoolStarvation_SomeFailToExtend()
    {
        // 4 producers, only 1 buffer — three should fail extension and bump per-kind drop.
        SpilloverRingPool.Initialize(1, 64 * 1024);
        const int producerCount = 4;
        var slots = new ThreadSlot[producerCount];
        for (var i = 0; i < producerCount; i++)
        {
            slots[i] = MakeSlot(64);
            while (WriteRecord(slots[i].Buffer, 32, (byte)TraceEventKind.SchedulerChunk, 0x11)) { }
        }
        var barrier = new Barrier(producerCount);
        var winners = 0;
        var losers = 0;
        var tasks = new Task[producerCount];
        for (var t = 0; t < producerCount; t++)
        {
            var local = slots[t];
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                if (TyphonEvent.TryReserveOnChain(local, 32, (byte)TraceEventKind.SchedulerChunk, out _, out _))
                {
                    Interlocked.Increment(ref winners);
                }
                else
                {
                    Interlocked.Increment(ref losers);
                }
            });
        }
        Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        Assert.That(winners, Is.EqualTo(1));
        Assert.That(losers, Is.EqualTo(3));
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Bulk-spawn scenario (the AntHill use case)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BulkBurst_FitsInChain_NoDrops()
    {
        // Mirror the AntHill burst scenario at small scale: many records that exceed primary capacity but fit
        // when chain extension to a single spillover is allowed. Verifies the headline win.
        const int primaryCapacity = 4 * 1024;       // ~128 records of 32 B fit in primary
        const int spillCapacity = 64 * 1024;        // ~2K records of 32 B fit in one spillover
        const int recordSize = 32;
        const int recordCount = 500;                // ~16 KiB total — exceeds primary, fits one spillover
        SpilloverRingPool.Initialize(2, spillCapacity);
        var slot = MakeSlot(primaryCapacity);
        var kind = (byte)TraceEventKind.EcsSpawn;
        for (var seq = 0; seq < recordCount; seq++)
        {
            Assert.That(TyphonEvent.TryReserveOnChain(slot, recordSize, kind, out var dst, out var ringUsed), Is.True,
                $"record {seq} should not drop — pool should have a spillover available");
            BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)recordSize);
            ringUsed.Publish();
        }
        // Chain depth should be 2 (primary + 1 spillover) — 500 records of 32 B = 16 KiB, under 64 KiB spillover.
        Assert.That(slot.Buffer.Next, Is.Not.Null, "primary overflowed; chain should have one spillover");
        Assert.That(slot.Buffer.Next.Next, Is.Null, "16 KiB fits in one 64 KiB spillover");
        Assert.That(slot.ChainTail, Is.SameAs(slot.Buffer.Next));
        // No drops on the chain
        Assert.That(slot.Buffer.DroppedEvents, Is.EqualTo(0));
        Assert.That(slot.Buffer.Next.DroppedEvents, Is.EqualTo(0));
    }

    [Test]
    public void BulkBurst_PoolDisabled_DropsOnPrimaryOverflow_ParitiyWithPreSpilloverBehavior()
    {
        // SpilloverBufferCount = 0 (pool empty) reproduces the pre-spillover behavior: producer drops on primary
        // overflow. This is the regression-check for "I can disable spillover and the engine still works."
        SpilloverRingPool.Initialize(0, 64 * 1024);
        const int primaryCapacity = 1024;
        var slot = MakeSlot(primaryCapacity);
        var kind = (byte)TraceEventKind.EcsSpawn;
        var reserved = 0;
        var dropped = 0;
        for (var i = 0; i < 200; i++)
        {
            if (TyphonEvent.TryReserveOnChain(slot, 32, kind, out var dst, out var ringUsed))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dst, 32);
                ringUsed.Publish();
                reserved++;
            }
            else
            {
                dropped++;
            }
        }
        Assert.That(reserved, Is.GreaterThan(0));
        Assert.That(dropped, Is.GreaterThan(0));
        Assert.That(slot.Buffer.Next, Is.Null, "no chain extension when pool is empty");
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(dropped),
            "every drop attempted to acquire a spillover and saw an empty pool");
    }

    [Test]
    public void RegistryCollapseAllChains_ReleasesSpilloversToPool()
    {
        ThreadSlotRegistry.ResetForTests();
        SpilloverRingPool.Initialize(4, 64 * 1024);

        // Claim a slot, build a 2-ring chain by directly manipulating its chain pointers.
        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        Assert.That(slotIdx, Is.GreaterThanOrEqualTo(0));
        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var spillover = SpilloverRingPool.TryAcquire();
        Assert.That(spillover, Is.Not.Null);
        slot.Buffer.SetNext(spillover);
        slot.ChainTail = spillover;
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(1));

        // Collapse all chains — the held spillover must return to the pool.
        ThreadSlotRegistry.CollapseAllChainsToPrimary();
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(0));
        Assert.That(slot.ChainHead, Is.SameAs(slot.Buffer));
        Assert.That(slot.ChainTail, Is.SameAs(slot.Buffer));
        Assert.That(slot.Buffer.Next, Is.Null);

        ThreadSlotRegistry.ReleaseCurrentThreadForTests();
        ThreadSlotRegistry.ResetForTests();
    }
}
