using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1399: the value buffer of an AllowMultiple key removed from the tree off the fence is retired, not freed — it stays live while any epoch pinned before
/// the removal is, because an optimistic reader in that epoch may have read the buffer id and not yet entered the buffer. On the fence's own thread it is
/// freed at once: EW-01's readers clause excludes readers there.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BTreeRetiredBufferTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"retired_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private delegate void Step(ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>Runs <paramref name="step"/> in an epoch scope of its own, entered and left around it, as each transaction's write is.</summary>
    private static void InScope(EpochManager epochManager, ChunkBasedSegment<PersistentStore> segment, Step step)
    {
        var depth = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            step(ref accessor);
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// The staged window: a reader pins its epoch, then the key's last value is removed in a scope of its own. Once the remover has left, only the reader
    /// pins anything older than the retirement — the buffer's root stays live, and out of the buffers created meanwhile, until the reader leaves.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-06")]
    public unsafe void ARemovedKeysBuffer_StaysLive_WhileAReaderPinnedBeforeTheRemovalIs(bool byMove)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index32Chunk));
        IntMultipleBTree<PersistentStore> tree = null;
        int elementId = 0, bufferId = 0;
        InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) =>
        {
            tree = new IntMultipleBTree<PersistentStore>(segment);
            elementId = tree.Add(5, 100, ref a, out bufferId);
            tree.Add(7, 200, ref a, out _);   // a live key the move can land in
        });
        Assert.That(bufferId, Is.Not.Zero, "premise: key 5 has a buffer");

        // The reader: pinned before the removal, as an optimistic reader that has read the buffer id and not yet entered the buffer.
        using var pinned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            var d = epochManager.EnterScope();
            try
            {
                pinned.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                epochManager.ExitScope(d);
            }
        }) { IsBackground = true };
        reader.Start();
        try
        {
            Assert.That(pinned.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the reader pinned");

            InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) =>
            {
                if (byMove)
                {
                    Assert.That(tree.MoveValue(5, 7, elementId, 100, ref a, out _, out _), Is.GreaterThanOrEqualTo(0));
                }
                else
                {
                    Assert.That(tree.RemoveValue(5, elementId, 100, ref a), Is.True);
                }
            });
            Assert.That(tree.RetiredBufferCount, Is.EqualTo(1), "key 5 left; its buffer retired, not freed");

            // The remover's own epoch is gone: a reclaim now is held back by the reader alone.
            epochManager.BumpEpochForRetire();
            InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) =>
            {
                tree.FlushRetiredBuffers(a.ChangeSet);
                Assert.That(tree.RetiredBufferCount, Is.EqualTo(1), "the reader's epoch keeps it");
                Assert.That(segment.IsChunkLive(bufferId), Is.True, "the root the reader may enter is still the buffer's");
                for (var k = 10; k < 60; k++)
                {
                    tree.Add(k, k, ref a, out var created);
                    Assert.That(created, Is.Not.EqualTo(bufferId), $"key {k}'s new buffer took the retired root");
                }
            });
        }
        finally
        {
            release.Set();
            reader.Join(TimeSpan.FromSeconds(10));
        }

        epochManager.BumpEpochForRetire();
        InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) =>
        {
            tree.FlushRetiredBuffers(a.ChangeSet);
            Assert.That(tree.RetiredBufferCount, Is.Zero);
            Assert.That(segment.IsChunkLive(bufferId), Is.False, "freed once nothing can reach it");
            tree.CheckConsistency(ref a);
        });
    }

    /// <summary>
    /// On the fence's own thread the emptied key's buffer is freed at once: no reader runs during the fence (EW-01's readers clause), and retiring would pay an
    /// epoch bump and a reclaim per key for one the contract excludes.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-06")]
    public unsafe void OnTheFence_ARemovedKeysBuffer_IsFreedAtOnce()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index32Chunk));
        IntMultipleBTree<PersistentStore> tree = null;
        int elementId = 0, bufferId = 0;
        InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) =>
        {
            tree = new IntMultipleBTree<PersistentStore>(segment);
            elementId = tree.Add(5, 100, ref a, out bufferId);
            tree.Add(9, 200, ref a, out _);   // an existing key to arrive in, so no new buffer takes the freed root
        });

        using (segment.FenceWindow.Open())
        {
            InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) =>
            {
                Span<IndexMove> moves = [new IndexMove { OldKey = KeyBytes8.FromInt(5), NewKey = KeyBytes8.FromInt(9), Value = 100, ElementId = elementId }];
                tree.ApplyMoves(moves, ref a);
            });
        }

        Assert.That(tree.RetiredBufferCount, Is.Zero, "nothing retired on the fence");
        Assert.That(segment.IsChunkLive(bufferId), Is.False, "key 5's buffer freed with its key");
        InScope(epochManager, segment, (ref ChunkAccessor<PersistentStore> a) => tree.CheckConsistency(ref a));
    }

    /// <summary>
    /// The retire queue drains oldest first and takes back, at its head, the entries a failed free did not try — what lets the reclaim never fail the write
    /// that runs it and never lose an untried buffer.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void RetiredBufferQueue_PopsOldestFirst_AndRequeuesAtTheHead()
    {
        var q = new IntMultipleBTree<PersistentStore>.RetiredBufferQueue();
        for (var i = 1; i <= 40; i++)
        {
            q.Enqueue(i, i);   // grows past its first 16
        }

        Span<int> ids = stackalloc int[16];
        Assert.That(q.TakeReclaimable(11, ids), Is.EqualTo(10), "stamps 1..10 are below 11");
        Assert.That(ids[0], Is.EqualTo(1));
        Assert.That(ids[9], Is.EqualTo(10));
        q.Requeue(ids[3..10], 10);
        Assert.That(q.Count, Is.EqualTo(37));
        Assert.That(q.TakeReclaimable(11, ids), Is.EqualTo(7), "the requeued ones, still reclaimable, come back first");
        Assert.That(ids[0], Is.EqualTo(4));
        Assert.That(q.TakeReclaimable(long.MaxValue, ids), Is.EqualTo(16));
        Assert.That(ids[0], Is.EqualTo(11));
    }
}
