using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1190 (IXW-07): a multi-value index reserves its value-buffer chunks before it latches a leaf — a new key's root chunk, and the chunk an append into a full
/// buffer takes — so nothing allocates under the leaf latch. Allocated there, a chunk could grow the segment and wait on back-pressure while every writer of
/// the leaf waited on it, or throw and fault the tree.
/// </summary>
/// <remarks>
/// The detector is the segment's own allocation seam, not the reservation's counter: it asks, at every allocation of the tree's segment on the test thread,
/// whether the thread holds an OLC latch. It therefore catches an allocation under a latch whichever path makes it, the ones the reservation does not know of.
/// </remarks>
[TestFixture]
[NonParallelizable]   // the allocation seam is static
class ValueBufferReservationTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var name = $"vbres_{TestContext.CurrentContext.Test.Name}";
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = 32UL * 1024 * 1024;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
        (_serviceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// New keys at the end, in the middle and at the front (a buffer each, and splits), values piled on five keys (each buffer grows to about ten chunks), values moved to new keys and to an existing
    /// one, and removals that park chunks for reuse: not one allocation of the tree's segment while the thread holds a latch.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-07")]
    [CancelAfter(20_000)]
    public unsafe void AMultiValueIndex_NeverAllocatesAValueBufferChunkUnderALatch()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index64Chunk));
        var tree = new DoubleMultipleBTree<PersistentStore>(segment);

        var me = Environment.CurrentManagedThreadId;
        // Relative to the count on entry: the count is per thread, and a test before this one on the same worker may have faulted a tree on purpose,
        // leaving its latches counted for good (IXW-09).
        var heldOnEntry = OlcLatchHeld.Count;
        var allocations = 0;
        var underLatch = 0;
        ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = s =>
        {
            if (ReferenceEquals(s, segment) && Environment.CurrentManagedThreadId == me)
            {
                allocations++;
                if (OlcLatchHeld.Count > heldOnEntry)
                {
                    underLatch++;
                }
            }
        };

        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var value = 0;
            for (var k = 0; k < 2_000; k++)
            {
                value++;
                tree.Add(k, value, ref accessor);
            }

            // New keys between existing ones (the general path, which reserves when its descent sees the key absent) and below the first one (the
            // prepend path).
            var rand = new Random(1190);
            for (var i = 0; i < 1_000; i++)
            {
                value++;
                tree.Add(rand.Next(1, 1_999) + 0.5, value, ref accessor);
            }

            for (var k = -1; k >= -300; k--)
            {
                value++;
                tree.Add(k, value, ref accessor);
            }

            var onKeyZero = new List<(int ElementId, int Value)>();
            for (var i = 0; i < 3_000; i++)
            {
                value++;
                var elementId = tree.Add(i % 5, value, ref accessor);
                if (i % 5 == 0)
                {
                    onKeyZero.Add((elementId, value));
                }
            }

            // Moves out of key 0: to new keys, and to key 1, whose buffer is full of the values above.
            for (var i = 0; i < 300; i++)
            {
                var (elementId, v) = onKeyZero[i];
                var newKey = i % 2 == 0 ? 10_000 + i : 1;
                Assert.That(tree.MoveValue(0, newKey, elementId, v, ref accessor, out _, out _), Is.GreaterThanOrEqualTo(0), $"move of {v}");
            }

            // Removals park chunks on key 0's free list; appends after them reuse those chunks.
            for (var i = 300; i < 500; i++)
            {
                var (elementId, v) = onKeyZero[i];
                Assert.That(tree.RemoveValue(0, elementId, v, ref accessor), Is.True, $"remove of {v}");
            }

            for (var i = 0; i < 400; i++)
            {
                value++;
                tree.Add(0, value, ref accessor);
            }

            ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
            Assert.That(tree.Height, Is.GreaterThan(1), "premise: the new keys split the tree");
            Assert.That(allocations, Is.GreaterThan(2_000), "premise: every new key took a buffer, and the piled-on keys grew theirs");
            Assert.That(underLatch, Is.Zero, $"{underLatch} of {allocations} allocations were made under a latch");
        }
        finally
        {
            ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }

    /// <summary>
    /// An allocation that fails while a buffer grows, or while a new key takes one, throws with no latch held: the tree is not faulted, and the writes after
    /// it — on another thread, so a held latch would show as a spin — complete.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-07")]
    [CancelAfter(30_000)]
    public unsafe void AnAllocationFault_ForAValueBuffer_LeavesTheTreeWritable()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index64Chunk));
        var tree = new DoubleMultipleBTree<PersistentStore>(segment);

        void Add(double key, int value)
        {
            var s = epochManager.EnterScope();
            var a = segment.CreateChunkAccessor();
            try
            {
                tree.Add(key, value, ref a);
            }
            finally
            {
                a.Dispose();
                epochManager.ExitScope(s);
            }
        }

        for (var v = 1; v <= 50; v++)
        {
            Add(7, v);   // one key, its buffer partly filled
        }

        var me = Environment.CurrentManagedThreadId;
        ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = s =>
        {
            if (ReferenceEquals(s, segment) && Environment.CurrentManagedThreadId == me)
            {
                throw new IOException("injected allocation fault");
            }
        };

        var faults = 0;
        try
        {
            // The append that fills key 7's chunk needs a new one; so does the first value of a new key.
            for (var v = 51; v <= 200 && faults == 0; v++)
            {
                try
                {
                    Add(7, v);
                }
                catch (IOException)
                {
                    faults++;
                }
            }

            try
            {
                Add(99, 1);
            }
            catch (IOException)
            {
                faults++;
            }
        }
        finally
        {
            ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
        }

        Assert.That(faults, Is.EqualTo(2), "premise: the full buffer and the new key each needed an allocation");
        Assert.That(tree.IsFaulted, Is.False, "the allocation failed with no latch held, so the tree is not faulted (IXW-09)");

        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (var v = 1_000; v < 1_500; v++)
                {
                    Add(v % 2 == 0 ? 7 : 99, v);
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(15)), Is.True, "writes after the fault did not finish: a latch was left held");
        Assert.That(failure, Is.Null, $"writes after the fault failed: {failure}");
    }
}
