using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1329 (IXW-10): the root and the tail pointer change only under the latch of the node they stop naming. Both used to be updated after the write had
/// released its latches — the root collapse at the end of the pessimistic remove, the tail by a lazy repair at the end of every pessimistic insert and remove —
/// and under churn a split of the collapsing root's only child lost its separator, and the tail went on naming a leaf a merge had retired and freed.
/// </summary>
[TestFixture]
[NonParallelizable]   // the probes are static
class BTreeRootAndTailTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var name = $"btroottail_{TestContext.CurrentContext.Test.Name}";
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = 16UL * 1024 * 1024;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        OlcDescentTrace.OnRootCollapse = null;
        OlcDescentTrace.OnPessimisticWriteReturned = null;
        (_serviceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The remover is parked at the collapse, root latched, while a second writer appends until the surviving leaf splits. The split cannot publish its
    /// separator into the root before the collapse completes; it restarts and splits the new root instead. With the collapse after the unlock, the appender
    /// finished during the park, its separator went into the old root, and the swap dropped it: the new leaf on the chain and under no ancestor.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-10")]
    [CancelAfter(15_000)]
    public unsafe void ASplitRacingTheRootCollapse_CannotPublishIntoTheCollapsingRoot()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);

        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            // Two leaves under one root: ascending inserts until the first root split.
            var n = 0;
            while (tree.Height < 2)
            {
                n++;
                tree.Add(n, n, ref accessor);
            }

            var top = n;
            var collapses = 0;
            var appenderFinishedDuringThePark = false;
            Task appender = null;
            var remover = Environment.CurrentManagedThreadId;
            OlcDescentTrace.OnRootCollapse = () =>
            {
                if (Environment.CurrentManagedThreadId != remover)
                {
                    return;
                }

                collapses++;
                // Enough appends to fill the surviving leaf and split it: the split must publish a separator into the root this remover holds.
                appender = Task.Factory.StartNew(() =>
                {
                    var s = epochManager.EnterScope();
                    var a = segment.CreateChunkAccessor();
                    try
                    {
                        for (var k = top + 1; k <= top + 200; k++)
                        {
                            tree.Add(k, k, ref a);
                        }
                    }
                    finally
                    {
                        a.Dispose();
                        epochManager.ExitScope(s);
                    }
                }, TaskCreationOptions.LongRunning);
                // Short: the appender retries against the latched root meanwhile, and its retry budget is finite. Long enough for it to finish when nothing
                // holds the root — 200 appends and a split are well under a millisecond.
                appenderFinishedDuringThePark = ((IAsyncResult)appender).AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
            };

            // Empty the right leaf from its end until it merges into the left one and the root collapses.
            for (var k = top; k > 0 && collapses == 0; k--)
            {
                Assert.That(tree.Remove(k, out _, ref accessor), Is.True, $"key {k}");
            }

            OlcDescentTrace.OnRootCollapse = null;
            Assert.That(collapses, Is.EqualTo(1), "premise: the removals collapsed the root");
            Assert.That(((IAsyncResult)appender).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), Is.True, "the appender completes once the collapse released the root");
            Assert.That(appender.Exception, Is.Null, "the appender must not throw");
            Assert.That(appenderFinishedDuringThePark, Is.False,
                "the appender split a leaf under a root another writer was collapsing — the collapse must hold the root's latch (IXW-10)");
            Assert.That(tree.ValidateDescentAndChainAgree(ref accessor), Is.Null, "every leaf on the chain is reachable by descent");
            Assert.That(tree.ValidateLeafDepths(ref accessor), Is.Null);
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }

    /// <summary>
    /// The other merge: the LEFT leaf absorbs the tail. Removing from the front empties the leftmost leaf, which has no left sibling, so its right
    /// neighbour — the tail — is merged into it, and the survivor must be the tail by the time the remove returns.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-10")]
    [CancelAfter(15_000)]
    public unsafe void TheTailNamesTheLastLeaf_WhenTheLeftLeafAbsorbsIt()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);

        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var n = 0;
            while (tree.Height < 2)
            {
                n++;
                tree.Add(n, n, ref accessor);
            }

            var stale = new List<string>();
            var probeAccessor = segment.CreateChunkAccessor();
            OlcDescentTrace.OnPessimisticWriteReturned = () =>
            {
                var r = tree.ValidateLeafChain(ref probeAccessor);
                if (r != null && stale.Count < 4)
                {
                    stale.Add(r);
                }
            };

            for (var k = 1; k <= n && tree.Height > 1; k++)
            {
                Assert.That(tree.Remove(k, out _, ref accessor), Is.True, $"key {k}");
            }

            OlcDescentTrace.OnPessimisticWriteReturned = null;
            probeAccessor.Dispose();
            Assert.That(tree.Height, Is.EqualTo(1), "premise: the left leaf absorbed the tail and the root collapsed");
            Assert.That(stale, Is.Empty, "the tail was stale when the merge returned: " + string.Join(" | ", stale));
            Assert.That(tree.ValidateLeafChain(ref accessor), Is.Null);
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }

    /// <summary>
    /// The tail is current the moment a pessimistic write returns: after every split of the tail leaf (ascending inserts) and every merge that absorbs it
    /// (removals from the end), before any later code could repair it.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-10")]
    [CancelAfter(15_000)]
    public unsafe void TheTailNamesTheLastLeaf_WhenEverySplitAndMergeReturns()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);

        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var stale = new List<string>();
            var checks = 0;
            var probeAccessor = segment.CreateChunkAccessor();
            OlcDescentTrace.OnPessimisticWriteReturned = () =>
            {
                checks++;
                var r = tree.ValidateLeafChain(ref probeAccessor);
                if (r != null && stale.Count < 4)
                {
                    stale.Add(r);
                }
            };

            const int n = 3000;
            for (var k = 1; k <= n; k++)
            {
                tree.Add(k, k, ref accessor);
            }

            var peakHeight = tree.Height;
            for (var k = n; k > 0; k--)
            {
                Assert.That(tree.Remove(k, out _, ref accessor), Is.True, $"key {k}");
            }

            OlcDescentTrace.OnPessimisticWriteReturned = null;
            probeAccessor.Dispose();
            Assert.That(peakHeight, Is.GreaterThanOrEqualTo(3), "premise: the inserts split the tail leaf repeatedly");
            Assert.That(tree.Height, Is.EqualTo(1), "premise: the removals merged the tail away down to one leaf");
            Assert.That(checks, Is.GreaterThan(0), "premise: pessimistic writes ran");
            Assert.That(stale, Is.Empty, "the tail was stale when a split or a merge returned: " + string.Join(" | ", stale));
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }
}
