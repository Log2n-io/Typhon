using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1399: the value buffer of an AllowMultiple key removed from the tree is retired, not freed — it stays live while any epoch pinned before the
/// removal is, because an optimistic reader in that epoch may have read the buffer id and not yet entered the buffer.
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

    /// <summary>
    /// The staged window: a reader pins its epoch, then the key's last value is removed. The buffer's root stays live and is not handed to the buffers
    /// created meanwhile; once the reader leaves and the epoch moves on, the root is freed.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-06")]
    public unsafe void ARemovedKeysBuffer_StaysLive_WhileAReaderPinnedBeforeTheRemovalIs()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index32Chunk));

        IntMultipleBTree<PersistentStore> tree;
        int elementId, bufferId;
        var depth = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            tree = new IntMultipleBTree<PersistentStore>(segment);
            elementId = tree.Add(5, 100, ref accessor, out bufferId);
            Assert.That(bufferId, Is.Not.Zero, "premise: key 5 has a buffer");
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }

        // The reader: pinned before the removal, as an optimistic reader that has read the buffer id and not yet entered the buffer.
        using var pinned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            var d = epochManager.EnterScope();
            pinned.Set();
            release.Wait();
            epochManager.ExitScope(d);
        });
        reader.Start();
        pinned.Wait();

        depth = epochManager.EnterScope();
        accessor = segment.CreateChunkAccessor();
        try
        {
            Assert.That(tree.RemoveValue(5, elementId, 100, ref accessor), Is.True);
            Assert.That(tree.EntryCount, Is.Zero, "key 5 left the tree");
            Assert.That(tree.RetiredBufferCount, Is.EqualTo(1), "its buffer retired, not freed");
            Assert.That(segment.IsChunkLive(bufferId), Is.True, "the root the reader may enter is still the buffer's");

            for (var k = 10; k < 60; k++)
            {
                tree.Add(k, k, ref accessor, out var created);
                Assert.That(created, Is.Not.EqualTo(bufferId), $"key {k}'s new buffer took the retired root");
            }
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }

        release.Set();
        reader.Join();

        // Every epoch pinned before the retirement is gone; a later one frees it.
        epochManager.BumpEpochForRetire();
        depth = epochManager.EnterScope();
        try
        {
            tree.FlushRetiredBuffers(null);
            Assert.That(tree.RetiredBufferCount, Is.Zero);
            Assert.That(segment.IsChunkLive(bufferId), Is.False, "freed once nothing can reach it");
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
}
