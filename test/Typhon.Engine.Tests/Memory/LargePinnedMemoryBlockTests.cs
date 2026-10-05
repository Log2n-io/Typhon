using System;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Memory;

/// <summary>
/// <see cref="LargePinnedMemoryBlock"/> at sizes a unit test can afford: alignment, the zeroing contract, accounting and lifetime. A block past
/// 2 GiB is exercised by <see cref="LargePageCacheTests"/> (nightly).
/// </summary>
[TestFixture]
internal sealed unsafe class LargePinnedMemoryBlockTests : AllocatorTestBase
{
    private MemoryAllocator Allocator => (MemoryAllocator)MemoryAllocator;

    [TestCase(1L, 64)]
    [TestCase(4097L, 64)]
    [TestCase(1_048_579L, 4096)]
    public void AZeroedBlock_IsAligned_AndReadsZero(long size, int alignment)
    {
        using var block = Allocator.AllocateLargePinned("test.large.zeroed", AllocationResource, size, alignment, LargeBlockContents.Zeroed);

        Assert.That((long)block.DataAsPointer % alignment, Is.Zero, "aligned");
        Assert.That(new ReadOnlySpan<byte>(block.DataAsPointer, (int)size).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "every byte reads zero");

        // The round-up keeps the raw pointer for Free: the aligned one is inside [raw, raw + alignment).
        var shift = (long)block.DataAsPointer - block.RawPointerForTests;
        Assert.That(shift, Is.InRange(0, alignment - 1));

        block.DataAsPointer[size - 1] = 0x5A;
        Assert.That(block.DataAsPointer[size - 1], Is.EqualTo(0x5A), "the last byte is ours");
    }

    [Test]
    public void AnUndefinedBlock_IsPageAligned_AndWritableAtBothEnds()
    {
        const long size = 3 * 4096 + 17;
        using var block = Allocator.AllocateLargePinned("test.large.undefined", AllocationResource, size, 4096, LargeBlockContents.Undefined);

        Assert.That((long)block.DataAsPointer % 4096, Is.Zero);
        Assert.That((long)block.RawPointerForTests, Is.EqualTo((long)block.DataAsPointer), "AlignedAlloc's pointer is the one freed");
        block.DataAsPointer[0] = 1;
        block.DataAsPointer[size - 1] = 2;
        Assert.That(block.DataAsPointer[0] + block.DataAsPointer[size - 1], Is.EqualTo(3));
    }

    [Test]
    public void TheAllocator_CountsTheBlock_UntilItIsDisposed_Once()
    {
        var bytes = Allocator.PinnedBytes;
        var blocks = Allocator.PinnedLiveBlocks;

        var block = Allocator.AllocateLargePinned("test.large.accounting", AllocationResource, 123_456, 64, LargeBlockContents.Zeroed);
        Assert.That(Allocator.PinnedBytes, Is.EqualTo(bytes + 123_456));
        Assert.That(Allocator.PinnedLiveBlocks, Is.EqualTo(blocks + 1));
        Assert.That(block.EstimatedMemorySize, Is.EqualTo(123_456L));

        block.Dispose();
        block.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(block.IsDisposed, Is.True);
            Assert.That(Allocator.PinnedBytes, Is.EqualTo(bytes), "freed once, counted once");
            Assert.That(Allocator.PinnedLiveBlocks, Is.EqualTo(blocks));
        });
    }

    [Test]
    public void DisposingTheOwner_FreesTheBlock()
    {
        var owner = new ResourceNode("test.large.owner", ResourceType.Memory, AllocationResource);
        var block = Allocator.AllocateLargePinned("test.large.child", owner, 8192, 64, LargeBlockContents.Zeroed);

        owner.Dispose();

        Assert.That(block.IsDisposed, Is.True);
    }

    [TestCase(0L, 64)]
    [TestCase(-1L, 64)]
    [TestCase(64L, 3)]
    [TestCase(64L, 0)]
    [TestCase(64L, 8192)]
    public void BadArguments_Throw(long size, int alignment) =>
        Assert.That(() => Allocator.AllocateLargePinned("test.large.bad", AllocationResource, size, alignment, LargeBlockContents.Undefined),
            Throws.InstanceOf<ArgumentException>());
}
