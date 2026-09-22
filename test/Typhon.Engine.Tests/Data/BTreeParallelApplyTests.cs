using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// W workers applying one batch to one tree concurrently, with no latch, no version validation and no restart handling (#872 step 6, <c>EW-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// The whole safety argument is <see cref="BTreeLeafPartitionTests"/>'s: parts never share a leaf, and the bulk descent writes leaves and nothing else. This
/// fixture is what turns that argument into evidence — it actually runs the workers and compares the resulting tree, byte for byte, against the same batch
/// applied on one thread.
/// </para>
/// <para>
/// <b>Byte comparison rather than key lookups.</b> A lookup-based check would pass on a tree whose values are right but whose control words, chain pointers
/// or OLC versions have been disturbed, and those are the bytes a stray write corrupts first. The comparison includes <c>OlcVersion</c> for the same reason
/// step 5's does: this path takes no latch, so the version must not move at all.
/// </para>
/// <para>
/// <b>The byte comparison does NOT verify leaf disjointness, and that was measured rather than assumed.</b> Ablating the boundary snap so that parts overlap
/// leaves all five <c>ProducesTheSameLeafBytesAsOneThread</c> cases GREEN and reddens only
/// <see cref="ParallelApply_EachWorkersLeafSetIsDisjointFromEveryOthers"/>. The reason is that today's only leaf mutation is one 4-byte aligned store into a
/// value slot, and a key belongs to exactly one part, so two workers sharing a leaf write disjoint slots and the result is the same either way.
/// </para>
/// <para>
/// So AC-6.2 rests on the disjointness test and on <see cref="BTreeLeafPartitionTests"/> — not on this fixture's byte equality, which is AC-6.3/6.4. Do not
/// delete the disjointness test as redundant. Disjointness is the invariant that <i>licences</i> writing with no latch; the byte equality is a consequence of
/// what the descent happens to write TODAY. The moment a leaf-level counter, a rotation, or a shared header field joins the write set — or the moment an
/// <c>AllowMultiple</c> batch puts two workers in one element buffer — overlap stops being benign, and the byte comparison would still not notice.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeParallelApplyTests
{
    private const int TreeSize = 20_000;

    /// <summary>One leaf's every non-value byte plus its values, in the order the chain walks them.</summary>
    private readonly struct LeafImage
    {
        public readonly int ChunkId;
        public readonly int[] Keys;
        public readonly int[] Values;
        public readonly int Control;
        public readonly int HighKey;
        public readonly int Prev;
        public readonly int Next;
        public readonly int Left;
        public readonly int Version;

        public LeafImage(int chunkId, int[] keys, int[] values, int control, int highKey, int prev, int next, int left, int version)
        {
            ChunkId = chunkId;
            Keys = keys;
            Values = values;
            Control = control;
            HighKey = highKey;
            Prev = prev;
            Next = next;
            Left = left;
            Version = version;
        }
    }

    private static unsafe List<LeafImage> SnapshotLeaves(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor)
    {
        var images = new List<LeafImage>();
        var node = tree.DiagnosticLeafChainHead;
        while (node.IsValid)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            var keys = new int[Index32Chunk.Capacity];
            var values = new int[Index32Chunk.Capacity];
            for (var slot = 0; slot < Index32Chunk.Capacity; slot++)
            {
                keys[slot] = chunk.Keys[slot];
                values[slot] = chunk.Values[slot];
            }

            images.Add(new LeafImage(node.ChunkId, keys, values, chunk.Control, chunk.HighKey, chunk.PrevChunk, chunk.NextChunk, chunk.LeftValue,
                chunk.OlcVersion));
            node = node.GetNext(ref accessor);
        }

        return images;
    }

    private static void AssertLeavesIdentical(List<LeafImage> expected, List<LeafImage> actual, string what)
    {
        Assert.That(actual, Has.Count.EqualTo(expected.Count), $"{what}: the leaf chain length changed");
        for (var c = 0; c < expected.Count; c++)
        {
            var e = expected[c];
            var a = actual[c];
            Assert.That(a.ChunkId, Is.EqualTo(e.ChunkId), $"{what}: leaf {c} of the chain is a different chunk");
            for (var slot = 0; slot < e.Keys.Length; slot++)
            {
                if (a.Keys[slot] != e.Keys[slot])
                {
                    Assert.Fail($"{what}: chunk {e.ChunkId} slot {slot} key {a.Keys[slot]} against {e.Keys[slot]}");
                }

                if (a.Values[slot] != e.Values[slot])
                {
                    Assert.Fail($"{what}: chunk {e.ChunkId} slot {slot} value {a.Values[slot]} against {e.Values[slot]}");
                }
            }

            Assert.That(a.Control, Is.EqualTo(e.Control), $"{what}: chunk {e.ChunkId} control word");
            Assert.That(a.HighKey, Is.EqualTo(e.HighKey), $"{what}: chunk {e.ChunkId} HighKey");
            Assert.That(a.Prev, Is.EqualTo(e.Prev), $"{what}: chunk {e.ChunkId} chain prev");
            Assert.That(a.Next, Is.EqualTo(e.Next), $"{what}: chunk {e.ChunkId} chain next");
            Assert.That(a.Left, Is.EqualTo(e.Left), $"{what}: chunk {e.ChunkId} left-child slot");
            Assert.That(a.Version, Is.EqualTo(e.Version), $"{what}: chunk {e.ChunkId} OlcVersion — this path takes no latch, so it must not move");
        }
    }

    private static BTreeValueUpdate<int>[] ScatteredBatch(int count, int seed)
    {
        var rng = new Random(seed);
        var keys = new SortedSet<int>();
        while (keys.Count < count)
        {
            keys.Add(rng.Next(1, TreeSize + 1) * 10);
        }

        var batch = new BTreeValueUpdate<int>[count];
        var i = 0;
        foreach (var key in keys)
        {
            batch[i] = new BTreeValueUpdate<int>(key, 7_000_000 + i);
            i++;
        }

        return batch;
    }

    /// <summary>
    /// Partitions <paramref name="batch"/> into <paramref name="workers"/> parts and applies each on its own thread, returning the total applied.
    /// </summary>
    /// <remarks>
    /// Real threads rather than the thread pool, and deliberately: <c>RentWarmAccessor</c> caches its accessor in a <c>[ThreadStatic]</c>, so the whole
    /// question of whether two workers can share a tree is a question about distinct threads. Worker exceptions are captured and rethrown after the join —
    /// an unobserved worker failure would leave the byte comparison passing on a tree nobody wrote to.
    /// </remarks>
    private static int ApplyInParallel(IntSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, EpochManager epochManager,
        BTreeValueUpdate<int>[] batch, int workers, ref ChunkAccessor<PersistentStore> planAccessor, out int parts)
    {
        var boundaries = new int[workers + 1];
        parts = tree.PartitionByLeafBoundaries(batch, workers, boundaries, ref planAccessor);

        var applied = new int[parts];
        var failures = new Exception[parts];
        var threads = new Thread[parts];
        var start = new ManualResetEventSlim(false);

        for (var p = 0; p < parts; p++)
        {
            var index = p;
            var from = boundaries[p];
            var to = boundaries[p + 1];
            threads[p] = new Thread(() =>
            {
                try
                {
                    // Released together so the workers actually overlap; starting them one at a time would test a sequential run wearing threads.
                    start.Wait();
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var accessor = segment.CreateChunkAccessor();
                        try
                        {
                            applied[index] = tree.UpdateValues(new ReadOnlySpan<BTreeValueUpdate<int>>(batch, from, to - from), ref accessor, out _);
                        }
                        finally
                        {
                            accessor.Dispose();
                        }
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"bulk-worker-{p}",
            };

            threads[p].Start();
        }

        start.Set();

        var total = 0;
        for (var p = 0; p < parts; p++)
        {
            threads[p].Join();
            total += applied[p];
        }

        start.Dispose();

        for (var p = 0; p < parts; p++)
        {
            if (failures[p] != null)
            {
                Assert.Fail($"worker {p} threw: {failures[p]}");
            }
        }

        return total;
    }

    /// <summary>
    /// Builds and fills one tree in a service scope of its own, then hands the body the tree, its segment and the epoch manager — the three things a worker
    /// thread needs to open its own accessor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own provider per call, not the fixture's.</b> The scoped <c>ManagedPagedMMF</c> is disposed when the helper returns, so a test that needs two
    /// trees — a reference and a subject, or three repeats — cannot get them from one fixture-level provider: the second call would build over a disposed
    /// file. Owning the provider here is what makes the helper safe to call more than once in a test.
    /// </para>
    /// <para>
    /// The tree is constructed exactly ONCE per scope. A second <c>new IntSingleBTree(segment)</c> over the same segment does not reopen the existing tree, it
    /// registers a new one in the segment directory and throws.
    /// </para>
    /// </remarks>
    private static unsafe void OnFreshTree(string tag, Action<IntSingleBTree<PersistentStore>, ChunkBasedSegment<PersistentStore>, EpochManager> body)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"par_{tag}_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        using var provider = serviceCollection.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var mpmmf = provider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = provider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var tree = new IntSingleBTree<PersistentStore>(segment);
            var accessor = segment.CreateChunkAccessor();
            try
            {
                for (var i = 1; i <= TreeSize; i++)
                {
                    tree.Add(i * 10, i, ref accessor);
                }
            }
            finally
            {
                accessor.Dispose();
            }

            body(tree, segment, epochManager);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-6.3 / AC-6.4 — identical to a single-threaded run, at every W
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    public void ParallelApply_ProducesTheSameLeafBytesAsOneThread(int workers)
    {
        var batch = ScatteredBatch(4_000, 606 + workers);
        List<LeafImage> parallelImage = null;
        var parts = 0;
        var appliedParallel = 0;

        OnFreshTree("subject", (tree, segment, epochManager) =>
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                appliedParallel = ApplyInParallel(tree, segment, epochManager, batch, workers, ref accessor, out parts);
                parallelImage = SnapshotLeaves(tree, ref accessor);

                // The validators see structural damage the leaf-byte comparison cannot: an internal node written by an overlapping worker.
                tree.CheckConsistency(ref accessor);
                Assert.That(tree.ValidateNodeKeyOrder(ref accessor), Is.Null);
                Assert.That(tree.ValidateLeafChain(ref accessor), Is.Null);
            }
            finally
            {
                accessor.Dispose();
            }
        });

        Assert.That(appliedParallel, Is.EqualTo(batch.Length), "every key exists, so every entry must be applied exactly once across the workers");
        if (workers > 1)
        {
            Assert.That(parts, Is.GreaterThan(1), $"W = {workers} collapsed to one part, so this case never exercised concurrency");
        }

        // Outside the subject's scope, never inside it. Each OnFreshTree opens an epoch scope, and nesting two of them fails the guard's depth check
        // ("EpochGuard depth mismatch: expected 1, got 0") intermittently rather than always — which is how it survived a fixture-only run and reddened as
        // soon as another fixture ran first.
        BuildReferenceAndCompare(batch, parallelImage, $"W = {workers}");
    }

    /// <summary>Applies the batch single-threaded to a second identical tree and compares the two leaf images.</summary>
    private static void BuildReferenceAndCompare(BTreeValueUpdate<int>[] batch, List<LeafImage> actual, string what)
        => OnFreshTree("ref", (tree, segment, epochManager) =>
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                Assert.That(tree.UpdateValues(batch, ref accessor, out _), Is.EqualTo(batch.Length));
                AssertLeavesIdentical(SnapshotLeaves(tree, ref accessor), actual, what);
            }
            finally
            {
                accessor.Dispose();
            }
        });

    [Test]
    [CancelAfter(15_000)]
    public void ParallelApply_RepeatedRunsAreByteIdentical()
    {
        // AC-6.4 states determinism against SCHEDULING, which the W sweep above cannot see: it varies the partition as well as the interleaving. Here the
        // partition is fixed and only the interleaving differs between runs.
        var batch = ScatteredBatch(4_000, 4_812);
        List<LeafImage> first = null;

        for (var run = 0; run < 3; run++)
        {
            var runIndex = run;
            OnFreshTree($"run{runIndex}", (tree, segment, epochManager) =>
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    ApplyInParallel(tree, segment, epochManager, batch, 8, ref accessor, out _);
                    var image = SnapshotLeaves(tree, ref accessor);
                    if (first == null)
                    {
                        first = image;
                    }
                    else
                    {
                        AssertLeavesIdentical(first, image, $"run {runIndex} against run 0");
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-6.2 — the workers' leaf sets, observed rather than argued
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void ParallelApply_EachWorkersLeafSetIsDisjointFromEveryOthers()
    {
        // The partition fixture proves the boundaries fall on leaf edges. This asserts the consequence on the exact spans the workers were handed, which is
        // the form the fence phase depends on: for every pair of parts, no leaf in common.
        var batch = ScatteredBatch(4_000, 1_357);

        OnFreshTree("disjoint", (tree, segment, epochManager) =>
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var boundaries = new int[9];
                var parts = tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref accessor);
                Assert.That(parts, Is.GreaterThan(1));

                var sets = new HashSet<int>[parts];
                for (var p = 0; p < parts; p++)
                {
                    sets[p] = [];
                    for (var i = boundaries[p]; i < boundaries[p + 1]; i++)
                    {
                        sets[p].Add(tree.GetLeafChunkIdFor(batch[i].Key, ref accessor));
                    }

                    Assert.That(sets[p], Is.Not.Empty, $"part {p} reaches no leaf at all");
                }

                for (var a = 0; a < parts; a++)
                {
                    for (var b = a + 1; b < parts; b++)
                    {
                        var overlap = new HashSet<int>(sets[a]);
                        overlap.IntersectWith(sets[b]);
                        Assert.That(overlap, Is.Empty, $"parts {a} and {b} share leaf chunk(s) {string.Join(", ", overlap)}");
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        });
    }
}
