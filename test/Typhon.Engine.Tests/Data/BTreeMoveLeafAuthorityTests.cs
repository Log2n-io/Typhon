using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression cover for the leaf-authority guards on the <c>Move</c> / <c>MoveValue</c> write paths (#765).
/// </summary>
/// <remarks>
/// <c>Move</c> chose the leaf it would INSERT into with <c>OptimisticDescendToLeaf</c>'s default <c>followRightLink: true</c>. That walk is a reader's tool: it
/// hops right until the key lies inside a leaf's actual contents, which is how a reader still finds an existing key after landing left of a concurrent split.
/// A key being newly inserted is in no leaf at all, so the walk cannot terminate on a match and instead runs one leaf PAST the one whose separator range owns
/// the key. Move then inserted there, below that leaf's separator, and every subsequent search for keys in the gap routed left of the leaf that held them.
/// <para>
/// Reads survived it — the same right-link walk that caused it also recovers from it — which is exactly why it went unseen: <c>Stress_MoveSameLeaf</c> printed
/// the violation on every run since it was written and discarded the bool. The insert path does NOT recover, because it passes <c>followRightLink: false</c> on
/// purpose, so the damage was one stale separator away from being a lost key.
/// </para>
/// <para>
/// These tests assert three independent instruments at once — descent reachability, the leaf chain, and <c>CheckConsistency</c> — because the defect was
/// invisible to the first and only ever showed in the third.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeMoveLeafAuthorityTests
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
                var raw = $"mla_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    // Mirrors Stress_MoveSameLeaf's layout: disjoint ranges, every even key moved to the odd slot above it. The wide gap keeps ranges off shared boundary
    // leaves, so with one range and one thread there is no concurrency left to blame.
    private const int SlotsPerRange = 800;
    private const int KeysPerRange = 200;

    /// <summary>
    /// One thread, one key range, 200 moves — and before the guard landed this broke a separator/leaf pair every single run.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IXW-04")]
    public unsafe void MoveEvenToOdd_SingleThreaded_KeepsEverySeparatorRoutingToItsLeaf([Values(1, 4, 16)] int rangeCount)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            Populate(tree, rangeCount, ref accessor);

            int moveFailures = 0;
            for (int t = 0; t < rangeCount; t++)
            {
                for (int i = 0; i < KeysPerRange; i++)
                {
                    int oldKey = t * SlotsPerRange + i * 2;
                    if (!tree.Move(oldKey, oldKey + 1, oldKey * 10, ref accessor))
                    {
                        moveFailures++;
                    }
                }
            }

            var report = Verify(tree, rangeCount, ref accessor);
            accessor.Dispose();

            Assert.That(moveFailures, Is.Zero, "every Move must find its old key");
            Assert.That(report, Is.Empty, report);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// The concurrent shape, with the assertion the stress fixture never made.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void MoveEvenToOdd_Concurrent_KeepsEverySeparatorRoutingToItsLeaf()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        const int rangeCount = 16;
        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            Populate(tree, rangeCount, ref accessor);
            accessor.Dispose();

            using var barrier = new Barrier(rangeCount);
            int moveFailures = 0;
            var tasks = new Task[rangeCount];
            for (int t = 0; t < rangeCount; t++)
            {
                var threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var d = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();
                        for (int i = 0; i < KeysPerRange; i++)
                        {
                            int oldKey = threadId * SlotsPerRange + i * 2;
                            if (!tree.Move(oldKey, oldKey + 1, oldKey * 10, ref wa))
                            {
                                Interlocked.Increment(ref moveFailures);
                            }
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(d);
                    }
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);

            var va = segment.CreateChunkAccessor();
            var report = Verify(tree, rangeCount, ref va);
            va.Dispose();

            Assert.That(moveFailures, Is.Zero, "every Move must find its old key");
            Assert.That(report, Is.Empty, report);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    /// <summary>
    /// Drives inserts into the exact band a too-high separator routes left of, since the insert path is the one that cannot recover by walking right.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void MoveThenReinsertVacatedKeys_PlacesThemInTheAuthoritativeLeaf()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        const int rangeCount = 16;
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            Populate(tree, rangeCount, ref accessor);

            for (int t = 0; t < rangeCount; t++)
            {
                for (int i = 0; i < KeysPerRange; i++)
                {
                    int oldKey = t * SlotsPerRange + i * 2;
                    tree.Move(oldKey, oldKey + 1, oldKey * 10, ref accessor);
                }
            }

            for (int t = 0; t < rangeCount; t++)
            {
                for (int i = 0; i < KeysPerRange; i++)
                {
                    int k = t * SlotsPerRange + i * 2;
                    tree.Add(k, k * 10, ref accessor);
                }
            }

            var expected = new List<int>();
            for (int t = 0; t < rangeCount; t++)
            {
                for (int i = 0; i < KeysPerRange; i++)
                {
                    expected.Add(t * SlotsPerRange + i * 2);
                    expected.Add(t * SlotsPerRange + i * 2 + 1);
                }
            }
            expected.Sort();

            var report = Compare(tree, expected, ref accessor);
            accessor.Dispose();
            Assert.That(report, Is.Empty, report);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Proves the verifier above is not vacuous: hand it a tree missing a key the invariant says must be reachable, and it must say so.
    /// </summary>
    /// <remarks>
    /// The whole reason IXW-04 existed undetected is that the instrument watching for it reported PASSED unconditionally. A green test that cannot go red is
    /// worth less than no test, because it also stops anyone looking. So this drives <c>Compare</c> — the exact assertion path the verifier uses — with a tree
    /// that has had one moved key removed behind its back, and requires <c>Compare</c>'s own marker in the failure.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("IXW-04")]
    public unsafe void Mutant_AKeyMissingFromItsAuthoritativeLeaf_IsReported()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        const int rangeCount = 4;
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            Populate(tree, rangeCount, ref accessor);

            for (int t = 0; t < rangeCount; t++)
            {
                for (int i = 0; i < KeysPerRange; i++)
                {
                    int oldKey = t * SlotsPerRange + i * 2;
                    tree.Move(oldKey, oldKey + 1, oldKey * 10, ref accessor);
                }
            }

            // Green first — otherwise the mutant proves nothing about the mutation.
            Assert.That(Verify(tree, rangeCount, ref accessor), Is.Empty, "the unmutated tree must be clean, or this mutant tests nothing");

            // The mutation: a key the invariant requires to be reachable no longer is.
            Assert.That(tree.Remove(SlotsPerRange + 101, out _, ref accessor), Is.True, "the key chosen for removal must have been present");

            var report = Verify(tree, rangeCount, ref accessor);
            accessor.Dispose();

            RuleMutants.AssertDetects("IXW-04", "UNROUTABLE", () => Assert.That(report, Is.Empty, report));
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private static void Populate(IntSingleBTree<PersistentStore> tree, int rangeCount, ref ChunkAccessor<PersistentStore> accessor)
    {
        for (int t = 0; t < rangeCount; t++)
        {
            for (int i = 0; i < KeysPerRange; i++)
            {
                int key = t * SlotsPerRange + i * 2;
                tree.Add(key, key * 10, ref accessor);
            }
        }
    }

    private static string Verify(IntSingleBTree<PersistentStore> tree, int rangeCount, ref ChunkAccessor<PersistentStore> accessor)
    {
        var expected = new List<int>();
        for (int t = 0; t < rangeCount; t++)
        {
            for (int i = 0; i < KeysPerRange; i++)
            {
                expected.Add(t * SlotsPerRange + i * 2 + 1);
            }
        }
        return Compare(tree, expected, ref accessor);
    }

    // Three instruments, deliberately not one: a key can be routable but chained wrong, or chained right but unroutable, and only the disagreement names which.
    private static string Compare(IntSingleBTree<PersistentStore> tree, List<int> expected, ref ChunkAccessor<PersistentStore> accessor)
    {
        var sb = new StringBuilder();

        var unroutable = new List<int>();
        foreach (var k in expected)
        {
            if (tree.TryGet(k, ref accessor).IsFailure)
            {
                unroutable.Add(k);
            }
        }
        if (unroutable.Count > 0)
        {
            sb.AppendLine($"UNROUTABLE: {unroutable.Count} of {expected.Count} keys not reachable by descent; "
                        + $"first 20: {string.Join(", ", unroutable.GetRange(0, Math.Min(20, unroutable.Count)))}");
        }

        var chain = new List<int>();
        foreach (var kv in tree.EnumerateLeaves())
        {
            chain.Add(kv.Key);
        }
        for (int i = 1; i < chain.Count; i++)
        {
            if (chain[i] <= chain[i - 1])
            {
                sb.AppendLine($"CHAIN DISORDER at position {i}: {chain[i - 1]} followed by {chain[i]}");
                break;
            }
        }

        var chainSet = new HashSet<int>(chain);
        if (!chainSet.SetEquals(expected))
        {
            var missing = new List<int>(expected);
            missing.RemoveAll(chainSet.Contains);
            sb.AppendLine($"CHAIN SET: chain={chain.Count} expected={expected.Count} missing={missing.Count}");
            if (missing.Count > 0)
            {
                sb.AppendLine($"  missing first 20: {string.Join(", ", missing.GetRange(0, Math.Min(20, missing.Count)))}");
            }
        }

        if (tree.EntryCount != expected.Count)
        {
            sb.AppendLine($"ENTRYCOUNT: {tree.EntryCount}, expected {expected.Count}");
        }

        try
        {
            tree.CheckConsistency(ref accessor);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"CHECKCONSISTENCY: {ex.Message}");
        }

        return sb.ToString();
    }
}
