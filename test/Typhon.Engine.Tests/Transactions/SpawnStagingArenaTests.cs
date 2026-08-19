using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// Unit tests for <see cref="SpawnStagingArena"/>, the transaction-scoped address space that replaced the spawn-staging
/// content chunk in #839.
/// </summary>
/// <remarks>
/// The arena's contract is small but three of its properties are load-bearing, and each is asserted here rather than
/// left to the callers: handles are never 0 (callers test <c>== 0</c> for "no payload", the same convention chunk id 0
/// carried), slots come back zeroed (the chunk path allocated without clearing, so an unsupplied component used to
/// inherit recycled bytes — from fresh native memory that would be genuinely uninitialised), and above all
/// <b>pointers survive growth</b>, because a write hands a <c>ref</c> into a slot and spawning again must not move it.
/// </remarks>
unsafe class SpawnStagingArenaTests
{
    [Test]
    public void Alloc_NeverReturnsZero_SoCallersCanKeepUsingZeroAsNoPayload()
    {
        using var arena = new SpawnStagingArena();

        for (var i = 0; i < 64; i++)
        {
            Assert.That(arena.Alloc(24), Is.Not.Zero,
                "handle 0 must stay reserved — SpawnEntry consumers test `== 0` to mean 'this slot has no payload', "
                + "exactly as they did when the value was a chunk id");
        }
    }

    [Test]
    public void Alloc_ReturnsZeroedMemory_EvenAfterReset()
    {
        using var arena = new SpawnStagingArena();

        var first = arena.Alloc(32);
        var p = arena.Resolve(first);
        for (var i = 0; i < 32; i++)
        {
            p[i] = 0xAB;
        }

        arena.Reset();

        var second = arena.Alloc(32);
        var q = arena.Resolve(second);
        for (var i = 0; i < 32; i++)
        {
            Assert.That(q[i], Is.Zero,
                $"byte {i} of a reused slot must be zero — a spawn that supplies no value for a component never writes "
                + "over its slot, so whatever the previous transaction left there would be copied into the cluster");
        }
    }

    /// <summary>
    /// The property the whole design exists for: allocating past a block boundary must not move earlier payloads.
    /// A <c>NativeMemory.Realloc</c>-based arena — the obvious implementation, and the one the issue's suggested
    /// precedent uses — fails this.
    /// </summary>
    [Test]
    public void PointersStayValidAcrossBlockGrowth()
    {
        const int PayloadSize = 64;
        const int Count = 4096;   // 256 KiB of payload against an 8 KiB block — dozens of blocks, guaranteed

        using var arena = new SpawnStagingArena();

        var handles = new List<int>(Count);
        var pointers = new List<System.IntPtr>(Count);

        for (var i = 0; i < Count; i++)
        {
            var h = arena.Alloc(PayloadSize);
            var p = arena.Resolve(h);
            *(int*)p = i;                       // stamp through the pointer we captured
            handles.Add(h);
            pointers.Add((System.IntPtr)p);
        }

        for (var i = 0; i < Count; i++)
        {
            Assert.That((System.IntPtr)arena.Resolve(handles[i]), Is.EqualTo(pointers[i]),
                $"payload {i} moved. Blocks must be appended, never reallocated — a write hands out a ref into a slot "
                + "and a later spawn must not turn it into a dangling pointer");
            Assert.That(*(int*)pointers[i], Is.EqualTo(i),
                $"payload {i} was still addressable but its contents changed");
        }
    }

    /// <summary>
    /// An oversized payload as the very FIRST allocation must not encode to handle 0.
    /// </summary>
    /// <remarks>
    /// Regression guard: the oversized path returns offset 0 of its own block, so its handle is non-zero only if its
    /// block index is. In a fresh arena that block would be index 0 and the handle would be 0 — which every consumer
    /// reads as "this slot has no payload", silently dropping the component from the cluster. The sibling test allocates
    /// something small first and therefore walks straight past this.
    /// </remarks>
    [Test]
    public void OversizedPayload_AsTheFirstAllocation_DoesNotEncodeToZero()
    {
        using var arena = new SpawnStagingArena();

        var huge = arena.Alloc(64 * 1024);

        Assert.That(huge, Is.Not.Zero,
            "an oversized first allocation must still produce a non-zero handle — 0 means 'no payload' to every consumer");
        arena.Resolve(huge)[64 * 1024 - 1] = 0x7F;
        Assert.That(arena.Resolve(huge)[64 * 1024 - 1], Is.EqualTo(0x7F));
    }

    /// <summary>
    /// A request of exactly one block must not overrun the block, and must still be addressable.
    /// </summary>
    /// <remarks>
    /// Regression guard: block 0 reserves its first slot so no handle is 0, which leaves it short of a full block. An
    /// implementation that tests capacity only BEFORE appending hands out an offset past the end and writes into the
    /// allocator's memory.
    /// </remarks>
    [Test]
    public void AllocationOfExactlyOneBlock_FitsWithoutOverrunning()
    {
        const int BlockSize = 8 * 1024;

        using var arena = new SpawnStagingArena();

        var first = arena.Alloc(64);
        var exact = arena.Alloc(BlockSize);

        Assert.That(exact, Is.Not.Zero);

        // Touch both ends; an overrun shows up here or in the allocator's own bookkeeping on free.
        var p = arena.Resolve(exact);
        p[0] = 0x11;
        p[BlockSize - 1] = 0x22;

        Assert.That(arena.Resolve(exact)[0], Is.EqualTo(0x11));
        Assert.That(arena.Resolve(exact)[BlockSize - 1], Is.EqualTo(0x22));
        Assert.That((System.IntPtr)arena.Resolve(first), Is.Not.EqualTo((System.IntPtr)arena.Resolve(exact)),
            "the exact-size allocation must not have been placed over the earlier one");
    }

    [Test]
    public void OversizedPayload_GetsItsOwnBlock_AndStillResolves()
    {
        using var arena = new SpawnStagingArena();

        var small = arena.Alloc(16);
        var huge = arena.Alloc(64 * 1024);    // eight times the block size
        var alsoSmall = arena.Alloc(16);

        var hugePtr = arena.Resolve(huge);
        for (var i = 0; i < 64 * 1024; i += 4096)
        {
            hugePtr[i] = 0x5A;
        }

        Assert.That(huge, Is.Not.Zero);
        Assert.That((System.IntPtr)arena.Resolve(small), Is.Not.EqualTo((System.IntPtr)arena.Resolve(alsoSmall)),
            "two ordinary allocations must not overlap because an oversized one came between them");

        for (var i = 0; i < 64 * 1024; i += 4096)
        {
            Assert.That(arena.Resolve(huge)[i], Is.EqualTo(0x5A), $"oversized payload corrupted at byte {i}");
        }
    }

    /// <summary>
    /// Reset must REWIND, not free. This is the assertion the original suite lacked entirely.
    /// </summary>
    /// <remarks>
    /// Every other test here passes just as well against <c>Reset() =&gt; Dispose()</c>, which frees every block and re-allocates on the next spawn — a
    /// <c>NativeMemory</c> round trip per pooled transaction, i.e. exactly the per-operation allocation this arena exists to remove. The churn test that
    /// verified #839 measured <c>ComponentSegment.AllocatedChunkCount</c>, so it was blind by construction to native allocation: it proved the segment leak
    /// was gone while saying nothing about the cost that replaced it.
    /// </remarks>
    [Test]
    public void Reset_RetainsItsBlock_RatherThanFreeingAndReallocating()
    {
        using var arena = new SpawnStagingArena();

        arena.Alloc(64);
        var afterFirstUse = arena.BlockCount;
        Assert.That(afterFirstUse, Is.GreaterThan(0), "precondition: allocating must have created a block");

        for (var round = 0; round < 20; round++)
        {
            arena.Reset();
            Assert.That(arena.BlockCount, Is.EqualTo(1),
                $"round {round}: Reset must keep exactly one block. 0 means it freed and the next spawn pays a fresh NativeMemory allocation; "
                + "more than 1 means surplus blocks are pinned in a pooled transaction for the process lifetime");
            arena.Alloc(64);
            Assert.That(arena.BlockCount, Is.EqualTo(1),
                $"round {round}: allocating after a rewind must reuse the retained block, not append a new one");
        }
    }

    /// <summary>
    /// A big transaction must not pin its high-water mark in the pool: surplus blocks go back on reset.
    /// </summary>
    [Test]
    public void Reset_ReleasesSurplusBlocks_SoOneBigTransactionDoesNotPinThePool()
    {
        using var arena = new SpawnStagingArena();

        for (var i = 0; i < 2000; i++)   // well past one 8 KiB block
        {
            arena.Alloc(64);
        }
        Assert.That(arena.BlockCount, Is.GreaterThan(1), "precondition: this must have grown past a single block");

        arena.Reset();

        Assert.That(arena.BlockCount, Is.EqualTo(1),
            "a transaction that staged a large batch must not leave its whole block list attached to a pooled instance");
    }

    [Test]
    public void Alloc_AfterReset_ReusesTheArenaWithoutStalePointersOrZeroHandles()
    {
        using var arena = new SpawnStagingArena();

        for (var round = 0; round < 8; round++)
        {
            var handles = new List<int>();
            for (var i = 0; i < 2000; i++)   // enough to append blocks each round
            {
                var h = arena.Alloc(64);
                Assert.That(h, Is.Not.Zero);
                *(int*)arena.Resolve(h) = i;
                handles.Add(h);
            }

            for (var i = 0; i < handles.Count; i++)
            {
                Assert.That(*(int*)arena.Resolve(handles[i]), Is.EqualTo(i), $"round {round}, payload {i}");
            }

            arena.Reset();
        }
    }
}
