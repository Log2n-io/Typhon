using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Cover for the OLC protocol's second parent validation in <c>OptimisticDescendToLeaf</c> — the <c>readUnlockOrRestart</c> half of a descent hop (#739/#297).
/// </summary>
/// <remarks>
/// A hop from parent to child needs TWO version checks on the parent, and the descent had only the first. The first answers "was the child pointer I read still
/// current when I read it"; the second answers "is it still current now that I hold a version for the child". Between them sits the child's <c>GetLatch</c> and
/// <c>ReadVersion</c>, and a structure modification landing in that gap is invisible to either check alone — the parent test has already passed, and the child
/// version is sampled AFTER the modification, so the child's own later validation compares against a version that never changes again. The descent then answers
/// for a leaf whose separators no longer route the key to it.
/// <para>
/// The gap is a couple of instructions wide, which is why it took a 25,701-iteration race harness to see six of them. This fixture closes it deterministically
/// instead: <c>OlcDescentTrace.RecordStep</c> fires exactly inside the window, so bumping the parent's version from that callback reproduces on one thread what
/// the harness needed contention and luck to produce.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeDescentHopAtomicityTests
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
                var raw = $"dha_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        OlcDescentTrace.RecordStep = null;
        (_serviceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The control for the test below: with no injection, the descent hops out of the root exactly once. Without this, "it hopped twice" could be an artefact of
    /// the tree, the trace hook or the lookup rather than evidence that the guard fired.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("IXS-07")]
    public unsafe void NoInjection_DescendsOnceAndDoesNotRestart()
    {
        int hops = Descend(injectVersionBump: false, out var found);

        Assert.Multiple(() =>
        {
            Assert.That(found.IsSuccess, Is.True);
            Assert.That(hops, Is.EqualTo(1), "an undisturbed descent must not restart, or the sibling test is measuring noise rather than the guard");
        });
    }

    /// <summary>
    /// Bump the root's version inside the hop window and the descent must start over. Before the second validation landed it carried on with a path it could no
    /// longer vouch for — verified by reverting the guard, where this test fails in 39 ms on the assertion below.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXS-07")]
    public unsafe void ParentVersionChangedWhileTakingTheChildVersion_RestartsTheDescent()
    {
        int hops = Descend(injectVersionBump: true, out var found);

        Assert.Multiple(() =>
        {
            Assert.That(found.IsSuccess, Is.True, "the key is present and no key moved — a restart must not change the answer");
            Assert.That(found.Value, Is.EqualTo(1234));
            Assert.That(hops, Is.GreaterThan(1),
                        "the descent hopped out of the root once and never came back: it accepted a parent whose version changed while it was taking the "
                      + "child's version. That is the missing readUnlockOrRestart (IXS-07), and it is #739/#297's residual.");
        });
    }

    /// <summary>
    /// #1237: a node on the path write-latched by a writer that is waiting — a multi-value buffer read under its leaf's latch, a page of it faulting in
    /// under page-cache back-pressure — is waited out, not counted as a modification on every pass. Counted, the pessimistic descent's 10 000 restarts
    /// ran out in ~380 ms and it threw IXW-01, past the WAL append in a commit, halting the engine. The root is held here, on every descent's path, for a
    /// second.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-01")]
    public unsafe void ADescentMeetingANodeAWriterHoldsForASecond_WaitsItOut()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 1; i <= 2000; i++)
            {
                tree.Add(i * 10, i, ref accessor);
            }

            var latch = tree.DiagnosticRoot.GetLatch(ref accessor);
            Assert.That(latch.TryWriteLock(), Is.True, "premise: the root is held");
            Exception failure = null;
            var found = -1;
            var reader = new Thread(() =>
            {
                var readerDepth = epochManager.EnterScope();
                var readerAccessor = segment.CreateChunkAccessor();
                try
                {
                    foreach (var kv in tree.EnumerateRange(12_340, 12_340))
                    {
                        found = kv.Value;
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    readerAccessor.Dispose();
                    epochManager.ExitScope(readerDepth);
                }
            });
            reader.Start();
            bool waitedPastTheBudget;
            try
            {
                // The holder's wait — a page fault under back-pressure, here simply time: past the ~380 ms the restart budget allowed.
                waitedPastTheBudget = !reader.Join(500);
            }
            finally
            {
                tree.DiagnosticRoot.GetLatch(ref accessor).WriteUnlock();
            }

            reader.Join();
            Assert.That(waitedPastTheBudget, Is.True, $"premise: the descent was still waiting when the holder let go ({failure?.Message})");

            Assert.That(failure, Is.Null, $"the descent gave up on a held node: {failure?.Message}");
            Assert.That(found, Is.EqualTo(1234), "the key, once the holder let go");
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>A hold past <see cref="BTreeHeldNode.MaxWait"/> is still named: the descent throws IXW-01.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-01")]
    public unsafe void ADescentMeetingANodeHeldPastTheBound_ThrowsIxw01()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        var bound = BTreeHeldNode.MaxWait;
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 1; i <= 2000; i++)
            {
                tree.Add(i * 10, i, ref accessor);
            }

            BTreeHeldNode.MaxWait = TimeSpan.FromMilliseconds(100);
            Assert.That(tree.DiagnosticRoot.GetLatch(ref accessor).TryWriteLock(), Is.True, "premise: the root is held");
            Exception failure = null;
            try
            {
                var reader = new Thread(() =>
                {
                    var readerDepth = epochManager.EnterScope();
                    try
                    {
                        foreach (var _ in tree.EnumerateRange(12_340, 12_340))
                        {
                        }
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                    finally
                    {
                        epochManager.ExitScope(readerDepth);
                    }
                });
                reader.Start();
                Assert.That(reader.Join(5_000), Is.True, "the descent waited past its bound");
            }
            finally
            {
                tree.DiagnosticRoot.GetLatch(ref accessor).WriteUnlock();
            }

            Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
            Assert.That(failure.Message, Does.Contain("write-latched for over").And.Contain("IXW-01"));
            accessor.Dispose();
        }
        finally
        {
            BTreeHeldNode.MaxWait = bound;
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// The bound is on one hold, not on the sum of a hot node's holders (#1237): writer after writer taking the root, each for less than
    /// <see cref="BTreeHeldNode.MaxWait"/> and together for over one and a half times it, is contention, and the descent waits it out. Summed, the storm's waiters named
    /// the market key's leaf a liveness defect past a commit's WAL append, and the engine stopped.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-01")]
    public unsafe void ADescentMeetingANodeHeldByWriterAfterWriter_WaitsPastTheBoundOfOneHold()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        var bound = BTreeHeldNode.MaxWait;
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 1; i <= 2000; i++)
            {
                tree.Add(i * 10, i, ref accessor);
            }

            BTreeHeldNode.MaxWait = TimeSpan.FromMilliseconds(200);
            Assert.That(tree.DiagnosticRoot.GetLatch(ref accessor).TryWriteLock(), Is.True, "premise: the root is held");
            var heldAt = Stopwatch.GetTimestamp();
            var holds = new List<long>();
            Exception failure = null;
            var found = -1;
            var reader = new Thread(() =>
            {
                var readerDepth = epochManager.EnterScope();
                try
                {
                    foreach (var kv in tree.EnumerateRange(12_340, 12_340))
                    {
                        found = kv.Value;
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    epochManager.ExitScope(readerDepth);
                }
            });
            reader.Start();
            var stillWaiting = true;
            try
            {
                // Eight holds of 40 ms: each a fifth of the bound, 320 ms in all. Each hand-off bumps the version, so each hold is another holder's.
                // Handed off, not unlocked and relocked: the descent got through that gap and found the key, ending the test before its last hold.
                // A hold ends when Join times out, and that timeout is a floor, not a deadline: on the macOS arm64 runner a 60 ms hold under a 100 ms
                // bound failed every run. The slack is now 160 ms, and each hold is measured so a premise failure says how long the holds really were.
                for (var hold = 0; hold < 8 && stillWaiting; hold++)
                {
                    stillWaiting = !reader.Join(40);
                    tree.DiagnosticRoot.GetLatch(ref accessor).HandOffWriteLockForTests();
                    holds.Add((long)Stopwatch.GetElapsedTime(heldAt).TotalMilliseconds);
                    heldAt = Stopwatch.GetTimestamp();
                }
            }
            finally
            {
                tree.DiagnosticRoot.GetLatch(ref accessor).WriteUnlock();
            }

            reader.Join();
            Assert.That(stillWaiting, Is.True,
                $"premise: the descent was still waiting after the last hold (holds of {string.Join(", ", holds)} ms; {failure?.Message})");
            Assert.That(failure, Is.Null, $"the descent summed the holders' holds into one: {failure?.Message}");
            Assert.That(found, Is.EqualTo(1234), "the key, once the last holder let go");
            accessor.Dispose();
        }
        finally
        {
            BTreeHeldNode.MaxWait = bound;
            epochManager.ExitScope(depth);
        }
    }

    private unsafe int Descend(bool injectVersionBump, out Result<int, BTreeLookupStatus> found)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 1; i <= 2000; i++)
            {
                tree.Add(i * 10, i, ref accessor);
            }

            int rootChunkId = tree.DiagnosticRoot.ChunkId;
            Assert.That(tree.DiagnosticRoot.GetIsLeaf(ref accessor), Is.False, "the tree must have interior nodes, or there is no hop to make atomic");

            int hopsFromRoot = 0;
            int injected = 0;

            OlcDescentTrace.RecordStep = (op, parentChunkId, _, _, _) =>
            {
                if (op != OlcDescentTrace.OpDescend || parentChunkId != rootChunkId)
                {
                    return;
                }

                Interlocked.Increment(ref hopsFromRoot);

                // Once, and only on the first hop out of the root: take and release the root's write lock. WriteUnlock bumps the version, which is exactly the
                // trace a completed structure modification leaves — without moving a single key, so the ANSWER stays correct either way and the only thing this
                // test can be measuring is whether the descent noticed.
                if (!injectVersionBump || Interlocked.Exchange(ref injected, 1) != 0)
                {
                    return;
                }

                var side = segment.CreateChunkAccessor();
                try
                {
                    var root = tree.DiagnosticRoot;
                    var latch = root.GetLatch(ref side);
                    Assert.That(latch.TryWriteLock(), Is.True, "the injection must actually take the lock whose release bumps the version");
                    latch.WriteUnlock();
                }
                finally
                {
                    side.Dispose();
                }
            };

            found = tree.TryGet(12_340, ref accessor);
            OlcDescentTrace.RecordStep = null;

            Assert.That(injected, Is.EqualTo(injectVersionBump ? 1 : 0), "the injection did not run as asked, so nothing below is measuring the window");

            accessor.Dispose();
            return hopsFromRoot;
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
}
