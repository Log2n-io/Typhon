using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>UpdateValues</c> — the bulk partitioning descent (#872 step 5). A sorted batch is split by each node's separators into one contiguous sub-range per
/// child, so every internal node is entered at most once for the whole batch instead of once per entry.
/// </summary>
/// <remarks>
/// Two properties carry the weight, and neither is speed. The first is that the <b>result is indistinguishable</b> from a loop over the single-entry path —
/// checked differentially rather than by re-asserting what the batch was built to do. The second is that <b>node visits match an independently computed
/// set</b>: the union of the root-to-leaf paths of the batch's keys, derived in the test by an obvious algorithm rather than from a fan-out model, so the
/// assertion fails if the descent ever re-enters a node or wanders into one with no work.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeBulkUpdateTests
{
    private const int TreeSize = 20_000;

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
                var raw = $"bulk_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                // 20 000 entries at fan-out 29 is ~715 chunks of 256 B, so ~25 pages per tree; the twin-tree fixture builds two. The default floor is not
                // enough for the segments below and the failure surfaces as a page-cache backpressure TIMEOUT rather than as anything about size.
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private delegate void TreeAction(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    private delegate void TwinTreeAction(
        IntSingleBTree<PersistentStore> bulk,
        ref ChunkAccessor<PersistentStore> bulkAccessor,
        IntSingleBTree<PersistentStore> loop,
        ref ChunkAccessor<PersistentStore> loopAccessor);

    private delegate void MultiTreeAction(IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>
    /// Keys are <c>i * 10</c> for i in 1..<see cref="TreeSize"/>, values <c>i</c>, so a key's initial value is derivable and gaps exist between keys.
    /// </summary>
    private static void Fill(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor)
    {
        for (var i = 1; i <= TreeSize; i++)
        {
            tree.Add(i * 10, i, ref accessor);
        }
    }

    private unsafe void WithTree(TreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var tree = new IntSingleBTree<PersistentStore>(segment);
                Fill(tree, ref accessor);
                body(tree, ref accessor);
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

    /// <summary>Two identically-built trees in separate segments — one gets the batch, the other the single-entry loop.</summary>
    private unsafe void WithTwinTrees(TwinTreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segA = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var segB = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            // Both creates are inside the try: if B's throws, A must still be disposed.
            var accessorA = default(ChunkAccessor<PersistentStore>);
            var accessorB = default(ChunkAccessor<PersistentStore>);
            try
            {
                accessorA = segA.CreateChunkAccessor();
                accessorB = segB.CreateChunkAccessor();
                var bulk = new IntSingleBTree<PersistentStore>(segA);
                var loop = new IntSingleBTree<PersistentStore>(segB);
                Fill(bulk, ref accessorA);
                Fill(loop, ref accessorB);

                // Each tree has its own segment and therefore its own accessor; the body drives them side by side.
                body(bulk, ref accessorA, loop, ref accessorB);
            }
            finally
            {
                accessorA.Dispose();
                accessorB.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private unsafe void WithMultiTree(MultiTreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 1_000, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                body(new IntMultipleBTree<PersistentStore>(segment), ref accessor);
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

    // ══════════════════════════════════════════════════════════════════════
    // AC-5.1 — identical to a TryUpdateValue loop
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(137)]
    [TestCase(1_000)]
    [TestCase(TreeSize)]
    public void UpdateValues_MatchesTryUpdateValueLoop(int batchSize)
    {
        // Differential, not self-referential: the batch path and the single-entry path run over two identically-built trees and every key in the tree is then
        // compared. N = 1 and N = TreeSize are in the list because they are the two ends the partition degenerates at — one child on every level, and every
        // child on every level.
        var rng = new Random(1234 + batchSize);
        var keys = new SortedSet<int>();
        while (keys.Count < batchSize)
        {
            keys.Add(rng.Next(1, TreeSize + 1) * 10);
        }

        var batch = new BTreeValueUpdate<int>[batchSize];
        var i = 0;
        foreach (var key in keys)
        {
            batch[i++] = new BTreeValueUpdate<int>(key, 1_000_000 + key);
        }

        WithTwinTrees((
            IntSingleBTree<PersistentStore> bulk,
            ref ChunkAccessor<PersistentStore> bulkAccessor,
            IntSingleBTree<PersistentStore> loop,
            ref ChunkAccessor<PersistentStore> loopAccessor) =>
        {
            var applied = bulk.UpdateValues(batch, ref bulkAccessor, out var stats);
            Assert.That(applied, Is.EqualTo(batchSize), "every key in the batch exists, so every entry must be applied");
            Assert.That(stats.Applied, Is.EqualTo(applied), "the stats block must agree with the return value");

            foreach (var entry in batch)
            {
                Assert.That(loop.TryUpdateValue(entry.Key, entry.NewValue, ref loopAccessor), Is.True);
            }

            // Compare EVERY key, not just the batch: a partitioning mistake shows up as a key OUTSIDE the batch having been written.
            for (var k = 1; k <= TreeSize; k++)
            {
                var key = k * 10;
                var fromBulk = bulk.TryGet(key, ref bulkAccessor);
                var fromLoop = loop.TryGet(key, ref loopAccessor);
                if (fromBulk.Value != fromLoop.Value)
                {
                    Assert.Fail($"key {key}: batch produced {fromBulk.Value}, the single-entry loop produced {fromLoop.Value}");
                }
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_EmptyBatch_IsANoOpAndVisitsNothing()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var applied = tree.UpdateValues(ReadOnlySpan<BTreeValueUpdate<int>>.Empty, ref accessor, out var stats);
            Assert.That(applied, Is.Zero);
            Assert.That(stats.NodeVisits, Is.Zero, "an empty batch must not even read the root");
            Assert.That(tree.TryGet(10, ref accessor).Value, Is.EqualTo(1), "the tree must be untouched");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_AbsentKeys_AreSkippedNotApplied()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Keys ending in 5 are never inserted (the fill uses multiples of 10), so these route to a real leaf and are simply not there.
            var batch = new[]
            {
                new BTreeValueUpdate<int>(105, 7),
                new BTreeValueUpdate<int>(1_000, 8),
                new BTreeValueUpdate<int>(10_005, 9),
            };

            var applied = tree.UpdateValues(batch, ref accessor, out var stats);
            Assert.That(applied, Is.EqualTo(1), "only the one key that exists may be applied");
            Assert.That(stats.Applied, Is.EqualTo(1));
            Assert.That(tree.TryGet(1_000, ref accessor).Value, Is.EqualTo(8), "the present key must be updated");

            // The half that matters, and that nothing else in this fixture covers: an absent key must write NOWHERE. A miss that stores at ~index instead of
            // skipping — the exact shape of a dropped `index < 0` guard — lands on whichever key happens to sit at the insertion point, and every other batch
            // in this file is built from present keys only, so no other test would ever see it.
            for (var k = 1; k <= TreeSize; k++)
            {
                var key = k * 10;
                var expected = key == 1_000 ? 8 : k;
                var actual = tree.TryGet(key, ref accessor).Value;
                if (actual != expected)
                {
                    Assert.Fail($"key {key} was written by a batch containing only absent keys and {1_000}: expected {expected}, got {actual}");
                }
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_UniqueBatchRepeatingAKey_AppliesTheLastValue()
    {
        // The merge cursor deliberately does NOT advance past a match so that a key repeated in the batch resolves to the same slot twice
        // (L32NodeStorage.ApplyValuesInLeaf). That behaviour is documented and was, until this test, entirely uncovered — every other batch here is built
        // from a SortedSet or from arithmetic, so a repeat was impossible to produce. It is exactly the line a later "the cursor can advance" optimisation
        // breaks, and the sparse SEARCH path must agree with the dense MERGE path about it.
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Dense enough to take the merge path: many entries into one leaf, with the repeat in the middle.
            var batch = new[]
            {
                new BTreeValueUpdate<int>(100, 11),
                new BTreeValueUpdate<int>(110, 12),
                new BTreeValueUpdate<int>(110, 13),
                new BTreeValueUpdate<int>(120, 14),
                new BTreeValueUpdate<int>(130, 15),
                new BTreeValueUpdate<int>(140, 16),
                new BTreeValueUpdate<int>(150, 17),
                new BTreeValueUpdate<int>(160, 18),
            };

            var applied = tree.UpdateValues(batch, ref accessor, out _);
            Assert.That(applied, Is.EqualTo(batch.Length), "a repeated key is applied once per entry, not once per distinct key");

            // Not Assert.Multiple: its lambda cannot capture a ref parameter.
            Assert.That(tree.TryGet(100, ref accessor).Value, Is.EqualTo(11));
            Assert.That(tree.TryGet(110, ref accessor).Value, Is.EqualTo(13), "the later entry for the repeated key wins");
            Assert.That(tree.TryGet(120, ref accessor).Value, Is.EqualTo(14), "the cursor must not have skipped the key after the repeat");
            Assert.That(tree.TryGet(160, ref accessor).Value, Is.EqualTo(18));
            // And the SPARSE path, which reaches its leaf through the per-entry search instead of the cursor. Two entries in a 29-slot leaf is below the
            // density threshold, and a key far from the first batch so it lands in a different leaf. Same tree, because each helper owns the scoped
            // ManagedPagedMMF and disposes it on the way out — calling WithTree twice in one test hands the second a disposed one.
            var sparse = new[]
            {
                new BTreeValueUpdate<int>(90_000, 21),
                new BTreeValueUpdate<int>(90_000, 22),
            };

            Assert.That(tree.UpdateValues(sparse, ref accessor, out _), Is.EqualTo(2));
            Assert.That(tree.TryGet(90_000, ref accessor).Value, Is.EqualTo(22), "the search path must agree with the merge path on a repeated key");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_AllowMultiple_DuplicateKeys_UpdateTheirOwnElements()
    {
        WithMultiTree(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Key = 42;
            var idA = tree.Add(Key, 1001, ref accessor);
            var idB = tree.Add(Key, 1002, ref accessor);
            var idC = tree.Add(Key, 1003, ref accessor);
            tree.Add(Key + 1, 2001, ref accessor);

            // Two entries under ONE key, each naming its own element — the case a partitioning descent must not collapse.
            var batch = new[]
            {
                new BTreeMultiValueUpdate<int>(Key, idA, 1001, 9001),
                new BTreeMultiValueUpdate<int>(Key, idC, 1003, 9003),
            };

            var applied = tree.UpdateValues(batch, ref accessor, out var stats);
            Assert.That(applied, Is.EqualTo(2), "both elements exist under this key");
            Assert.That(stats.LeavesTouched, Is.EqualTo(1), "both entries share a key, so the descent must reach exactly one leaf");

            var seen = new List<int>();
            var neighbour = new List<int>();
            var e = tree.EnumerateRangeMultiple(Key, Key + 1);
            while (e.MoveNext())
            {
                var target = e.CurrentKey == Key ? seen : neighbour;
                foreach (var v in e.CurrentValues)
                {
                    target.Add(v);
                }
            }

            Assert.Multiple(() =>
            {
                // ORDER, for the same reason step 4's AC-4.3 test asserts order: a swap-with-last preserves the SET exactly.
                Assert.That(seen, Is.EqualTo(new[] { 9001, 1002, 9003 }), "each element must be updated where it sits, and idB must be untouched");
                Assert.That(neighbour, Is.EqualTo(new[] { 2001 }), "an adjacent key must not be touched");
            });
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-5.2 — structure bit-identical
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public unsafe void UpdateValues_LeavesEveryNonValueByteIdentical()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Snapshot every leaf on the chain: keys, control word (flags + Start + Count), HighKey, both chain pointers, the left-child slot, and the OLC
            // version. The bulk path takes NO latch, so unlike step 4's single-entry path even OlcVersion must come back unchanged.
            var chunkIds = new List<int>();
            var node = tree.DiagnosticLeafChainHead;
            while (node.IsValid)
            {
                chunkIds.Add(node.ChunkId);
                node = node.GetNext(ref accessor);
            }

            Assert.That(chunkIds, Has.Count.GreaterThan(1), "the fixture must build a multi-leaf tree or this proves nothing");

            var keysBefore = new int[chunkIds.Count][];
            var valuesBefore = new int[chunkIds.Count][];
            var scalarsBefore = new (int Control, int HighKey, int Prev, int Next, int Left, int Version)[chunkIds.Count];

            for (var c = 0; c < chunkIds.Count; c++)
            {
                ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(chunkIds[c]);
                var ks = new int[Index32Chunk.Capacity];
                var vs = new int[Index32Chunk.Capacity];
                for (var slot = 0; slot < Index32Chunk.Capacity; slot++)
                {
                    ks[slot] = chunk.Keys[slot];
                    vs[slot] = chunk.Values[slot];
                }

                keysBefore[c] = ks;
                valuesBefore[c] = vs;
                scalarsBefore[c] = (chunk.Control, chunk.HighKey, chunk.PrevChunk, chunk.NextChunk, chunk.LeftValue, chunk.OlcVersion);
            }

            var batch = new BTreeValueUpdate<int>[500];
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i] = new BTreeValueUpdate<int>((i + 1) * 30, 500_000 + i);
            }

            Assert.That(tree.UpdateValues(batch, ref accessor, out _), Is.EqualTo(batch.Length));

            var changedValueSlots = 0;
            for (var c = 0; c < chunkIds.Count; c++)
            {
                ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(chunkIds[c]);
                var which = chunkIds[c];

                for (var slot = 0; slot < Index32Chunk.Capacity; slot++)
                {
                    if (chunk.Keys[slot] != keysBefore[c][slot])
                    {
                        Assert.Fail($"chunk {which} slot {slot}: the key array must not be written at all");
                    }

                    if (chunk.Values[slot] != valuesBefore[c][slot])
                    {
                        changedValueSlots++;
                    }
                }

                var s0 = scalarsBefore[c];
                Assert.That(chunk.Control, Is.EqualTo(s0.Control), $"chunk {which}: flags, Start and Count must be unchanged");
                Assert.That(chunk.HighKey, Is.EqualTo(s0.HighKey), $"chunk {which}: HighKey must be unchanged");
                Assert.That(chunk.PrevChunk, Is.EqualTo(s0.Prev), $"chunk {which}: the leaf chain must be unchanged");
                Assert.That(chunk.NextChunk, Is.EqualTo(s0.Next), $"chunk {which}: the leaf chain must be unchanged");
                Assert.That(chunk.LeftValue, Is.EqualTo(s0.Left), $"chunk {which}: the left-child slot must be unchanged");
                Assert.That(chunk.OlcVersion, Is.EqualTo(s0.Version), $"chunk {which}: the bulk path takes no latch, so the version must not move");
            }

            Assert.That(changedValueSlots, Is.EqualTo(batch.Length), "exactly one value slot per applied entry may change — no more, and not fewer");

            // The validators are what would catch a structural side effect the byte comparison cannot see, e.g. inside an internal node.
            tree.CheckConsistency(ref accessor);
            Assert.That(tree.ValidateNodeKeyOrder(ref accessor), Is.Null);
            Assert.That(tree.ValidateLeafChain(ref accessor), Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-5.3 — node visits, against an independently computed set
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    [TestCase(10)]
    [TestCase(100)]
    [TestCase(1_000)]
    [TestCase(10_000)]
    public void UpdateValues_VisitsExactlyTheUnionOfTheKeysPaths(int batchSize)
    {
        // The model in the design is a fan-out estimate; this asserts the PROPERTY the model is derived from, computed independently: the set of nodes on the
        // union of the batch keys' root-to-leaf paths. Equality in both directions — a descent that re-enters a node overshoots, one that skips a node with
        // work undershoots — and it needs no assumption about fan-out, which really is 19-38 rather than the model's 29.
        var rng = new Random(99 + batchSize);
        var keys = new SortedSet<int>();
        while (keys.Count < batchSize)
        {
            keys.Add(rng.Next(1, TreeSize + 1) * 10);
        }

        var batch = new BTreeValueUpdate<int>[batchSize];
        var i = 0;
        foreach (var key in keys)
        {
            batch[i++] = new BTreeValueUpdate<int>(key, key + 3);
        }

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var expected = new HashSet<int>();
            foreach (var entry in batch)
            {
                var node = tree.DiagnosticRoot;
                while (node.IsValid)
                {
                    expected.Add(node.ChunkId);
                    if (node.GetIsLeaf(ref accessor))
                    {
                        break;
                    }

                    var index = node.Find(entry.Key, Comparer<int>.Default, ref accessor);
                    if (index < 0)
                    {
                        index = ~index - 1;
                    }

                    node = node.GetChild(index, ref accessor);
                }
            }

            tree.UpdateValues(batch, ref accessor, out var stats);

            TestContext.Out.WriteLine(
                $"N={batchSize}: visits={stats.NodeVisits} expected={expected.Count} leaves={stats.LeavesTouched} "
                + $"visits/update={stats.NodeVisits / (double)batchSize:F2}");

            Assert.That(stats.NodeVisits, Is.EqualTo(expected.Count),
                "the descent must enter exactly the nodes on the union of the batch keys' paths — no node twice, and none with no work in it");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-5.7 / AC-5.8 — allocation, and an unsorted batch
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_AllocatesNothing()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var batch = new BTreeValueUpdate<int>[256];
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i] = new BTreeValueUpdate<int>((i + 1) * 70, i);
            }

            tree.UpdateValues(batch, ref accessor, out _);   // warm the JIT before measuring

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var round = 0; round < 50; round++)
            {
                tree.UpdateValues(batch, ref accessor, out _);
            }

            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero, "the batch span is caller-owned and the descent is recursion over spans");
        });
    }

#if DEBUG
    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_UnsortedBatch_FailsLoudly()
    {
        // An out-of-order entry is not merely misplaced: the partition hands each child ONE contiguous sub-range and never revisits it, so the stray entry is
        // routed to whichever child the walk had reached and is silently dropped — or applied to the wrong region if that leaf happens to hold the key.
        // DEBUG-only by design, hence the guard: Release keeps the contract, Debug enforces it.
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var batch = new[]
            {
                new BTreeValueUpdate<int>(100, 1),
                new BTreeValueUpdate<int>(90, 2),
                new BTreeValueUpdate<int>(300, 3),
            };

            var localAccessor = accessor;
            Assert.Throws<InvalidOperationException>(() => tree.UpdateValues(batch, ref localAccessor, out _));
        });
    }
#endif

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_MultiOverloadOnUniqueTree_Throws()
    {
        // Step 4's review found that TryUpdateValue had no such guard and corrupted an AllowMultiple index. The bulk entry points take a batch AND both index
        // kinds, so there are more ways to be wrong here, not fewer. Split across two tests because each helper owns the scoped ManagedPagedMMF and disposes
        // it on the way out — calling both in one test hands the second a disposed one.
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var multi = new[] { new BTreeMultiValueUpdate<int>(100, 1, 10, 20) };
            var localAccessor = accessor;
            Assert.Throws<InvalidOperationException>(
                () => tree.UpdateValues(multi, ref localAccessor, out _),
                "a unique index has no element buffers, so the AllowMultiple overload must refuse it");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_UniqueOverloadOnAllowMultipleTree_Throws()
    {
        WithMultiTree(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            tree.Add(42, 1001, ref accessor);
            var unique = new[] { new BTreeValueUpdate<int>(42, 7777) };
            var localAccessor = accessor;
            Assert.Throws<InvalidOperationException>(
                () => tree.UpdateValues(unique, ref localAccessor, out _),
                "the leaf slot holds a bufferId on this tree, so the unique overload must refuse it rather than overwrite it");

            // And the refusal must leave the buffer intact, which is the half that would have failed before the guard existed.
            var seen = new List<int>();
            var e = tree.EnumerateRangeMultiple(42, 42);
            while (e.MoveNext())
            {
                foreach (var v in e.CurrentValues)
                {
                    seen.Add(v);
                }
            }

            Assert.That(seen, Is.EqualTo(new[] { 1001 }));
        });
    }

    // ══════════════════════════════════════════════
    // The batched leaf/descent overrides exist per chunk width — exercise more than one
    // ══════════════════════════════════════════════

    /// <summary>
    /// <c>ApplyValuesInLeaf</c>, <c>ReadNodeHeader</c> and <c>FindChildAndBound</c> are overridden separately for each chunk width, and each override restates
    /// the same <c>Adjust(Start + index)</c> rotation arithmetic and child-pointer read. Every test above drives <c>Index32</c> only, which is exactly the gap
    /// step 4's review found in <c>SetValueOnly</c>: three of the four widths had no coverage at all.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public unsafe void UpdateValues_AppliesCorrectly_OnLongKeyedTree()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index64Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var tree = new LongSingleBTree<PersistentStore>(segment);
                const int Count = 5_000;
                for (var i = 1; i <= Count; i++)
                {
                    tree.Add(i * 10L, i, ref accessor);
                }

                // Every third key, so each updated key has untouched neighbours on both sides in the same leaf.
                var batch = new BTreeValueUpdate<long>[Count / 3];
                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i] = new BTreeValueUpdate<long>((i + 1) * 30L, 700_000 + i);
                }

                var applied = tree.UpdateValues(batch, ref accessor, out var stats);
                Assert.That(applied, Is.EqualTo(batch.Length), "every key in the batch exists");
                Assert.That(stats.Applied, Is.EqualTo(applied));

                // Check the whole tree, not just the batch: a rotation or child-pointer mistake in the L64 override shows up as a NEIGHBOUR being written.
                for (var i = 1; i <= Count; i++)
                {
                    var key = i * 10L;
                    var expected = i % 3 == 0 ? 700_000 + (i / 3 - 1) : i;
                    var actual = tree.TryGet(key, ref accessor).Value;
                    if (actual != expected)
                    {
                        Assert.Fail($"key {key}: expected {expected}, got {actual}");
                    }
                }

                tree.CheckConsistency(ref accessor);
                Assert.That(tree.ValidateNodeKeyOrder(ref accessor), Is.Null);
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

    /// <summary>
    /// The <c>AllowMultiple</c> path across a tree deep enough for the partition to matter, and with allocation measured on it.
    /// </summary>
    /// <remarks>
    /// The other multi test builds four entries, so its tree is a single leaf: the root IS the leaf, <c>LeavesTouched</c> is 1 whatever the descent does, and
    /// <c>MultiApplier</c> and the partitioning descent are never exercised together. §5.3's motivating case — one cell's re-clustering of a spatial index —
    /// is precisely the non-unique one, so that combination is the one that most needs covering. Allocation is asserted here too, because the multi path is
    /// the one that rents a second warm accessor and the unique allocation test cannot see it.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void UpdateValues_AllowMultiple_AcrossManyLeaves_UpdatesOnlyTheNamedElements()
    {
        WithMultiTree(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Keys = 1_500;

            // Two elements per key, so every update has a sibling in its own buffer that must not move.
            var first = new int[Keys + 1];
            for (var k = 1; k <= Keys; k++)
            {
                first[k] = tree.Add(k * 10, k * 1_000, ref accessor);
                tree.Add(k * 10, k * 1_000 + 1, ref accessor);
            }

            // Every fifth key, so the batch spans many leaves but does not fill them.
            var batch = new System.Collections.Generic.List<BTreeMultiValueUpdate<int>>();
            for (var k = 5; k <= Keys; k += 5)
            {
                batch.Add(new BTreeMultiValueUpdate<int>(k * 10, first[k], k * 1_000, 500_000 + k));
            }

            var span = batch.ToArray();
            var applied = tree.UpdateValues(span, ref accessor, out var stats);

            Assert.That(applied, Is.EqualTo(span.Length), "every named element exists");
            Assert.That(stats.LeavesTouched, Is.GreaterThan(1), "the fixture must span several leaves or the partition is not being exercised at all");
            Assert.That(stats.LeavesTouched, Is.LessThan(span.Length), "batching must reach fewer leaves than there are entries");

            // Every key in the tree, not just the batch: a partitioning mistake on the multi path shows up as an untouched key having been written.
            var e = tree.EnumerateRangeMultiple(10, Keys * 10);
            var seenKeys = 0;
            while (e.MoveNext())
            {
                var k = e.CurrentKey / 10;
                seenKeys++;
                var expectedFirst = k % 5 == 0 ? 500_000 + k : k * 1_000;

                var values = new System.Collections.Generic.List<int>();
                foreach (var v in e.CurrentValues)
                {
                    values.Add(v);
                }

                // ORDER: the sibling must still sit where it was, and the updated element must be updated in place rather than appended.
                if (values.Count != 2 || values[0] != expectedFirst || values[1] != k * 1_000 + 1)
                {
                    Assert.Fail($"key {k * 10}: expected [{expectedFirst}, {k * 1_000 + 1}], got [{string.Join(", ", values)}]");
                }
            }

            Assert.That(seenKeys, Is.EqualTo(Keys), "every key must still be present and enumerable");

            // AC-5.7 on the path that rents the sibling accessor.
            tree.UpdateValues(span, ref accessor, out _);   // warm
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var round = 0; round < 20; round++)
            {
                tree.UpdateValues(span, ref accessor, out _);
            }

            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero, "the AllowMultiple bulk path must not allocate either");
        });
    }

    /// <summary>
    /// The bulk path over ROTATED leaves — the one case that tells <c>Wrap</c> apart from the identity function.
    /// </summary>
    /// <remarks>
    /// Every other fixture in this file only ever calls <c>Add</c>, so no node's <c>Start</c> ever leaves 0, every physical slot equals its logical index, and
    /// the ring-buffer arithmetic the new leaf merge introduced is never actually exercised. Removing a leaf's first item is what advances <c>Start</c>
    /// (<c>IncrementStart</c>), so this deliberately churns the front of the tree first and then ASSERTS that rotation really happened — a test that silently
    /// stopped producing rotated nodes would be worse than no test, because it would still pass.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public unsafe void UpdateValues_AppliesCorrectly_OnRotatedLeaves()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Remove the low keys, then re-add higher ones, so leaves rotate rather than just shrink.
            var removed = new System.Collections.Generic.HashSet<int>();
            for (var k = 1; k <= 400; k++)
            {
                if (tree.Remove(k * 10, out _, ref accessor))
                {
                    removed.Add(k * 10);
                }
            }

            var rotated = 0;
            var node = tree.DiagnosticLeafChainHead;
            while (node.IsValid)
            {
                ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
                if (chunk.Start != 0)
                {
                    rotated++;
                }

                node = node.GetNext(ref accessor);
            }

            Assert.That(rotated, Is.GreaterThan(0), "the fixture must actually produce rotated leaves or it proves nothing about Wrap");
            TestContext.Out.WriteLine($"rotated leaves: {rotated}");

            // A batch dense enough to take the MERGE path (which is where Wrap is used per slot scanned), over the surviving keys.
            var batch = new System.Collections.Generic.List<BTreeValueUpdate<int>>();
            for (var k = 401; k <= 2_000; k++)
            {
                batch.Add(new BTreeValueUpdate<int>(k * 10, 600_000 + k));
            }

            var span = batch.ToArray();
            Assert.That(tree.UpdateValues(span, ref accessor, out _), Is.EqualTo(span.Length));

            // Every key in the tree, so a Wrap that lands on the wrong physical slot shows up as a neighbour being written.
            for (var k = 1; k <= TreeSize; k++)
            {
                var key = k * 10;
                if (removed.Contains(key))
                {
                    continue;
                }

                var expected = k >= 401 && k <= 2_000 ? 600_000 + k : k;
                var actual = tree.TryGet(key, ref accessor).Value;
                if (actual != expected)
                {
                    Assert.Fail($"key {key} on a rotated tree: expected {expected}, got {actual}");
                }
            }

            tree.CheckConsistency(ref accessor);
            Assert.That(tree.ValidateNodeKeyOrder(ref accessor), Is.Null);
        });
    }

    /// <summary>
    /// The <c>uint</c> key path, whose comparisons are UNSIGNED and which had no coverage at all.
    /// </summary>
    /// <remarks>
    /// <c>L32NodeStorage</c> keeps two separate branches for <c>int</c> and <c>uint</c> in both the leaf merge and the child search, and every other test here
    /// drives <c>int</c>. Keys straddling <c>0x7FFF_FFFF</c> are the discriminating input: under a signed comparison everything at or above <c>0x8000_0000</c>
    /// sorts BELOW everything under it, so a branch that silently used the signed path would mis-route or mis-match here and nowhere else.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public unsafe void UpdateValues_AppliesCorrectly_OnUnsignedKeysAcrossTheSignBoundary()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var tree = new UIntSingleBTree<PersistentStore>(segment);

                // 4 000 keys centred on the sign boundary, so roughly half have the top bit set.
                const uint Origin = 0x7FFF_F000u;
                const int Count = 4_000;
                for (var i = 0; i < Count; i++)
                {
                    tree.Add(Origin + (uint)(i * 4), i, ref accessor);
                }

                // Dense contiguous batch spanning the boundary itself.
                var batch = new BTreeValueUpdate<uint>[1_000];
                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i] = new BTreeValueUpdate<uint>(Origin + (uint)((i + 1_000) * 4), 800_000 + i);
                }

                Assert.That(tree.UpdateValues(batch, ref accessor, out var stats), Is.EqualTo(batch.Length),
                    "every key exists; a signed comparison would mis-route the ones above 0x7FFFFFFF");
                Assert.That(stats.LeavesTouched, Is.GreaterThan(1), "the batch must span leaves for the child search to matter");

                for (var i = 0; i < Count; i++)
                {
                    var key = Origin + (uint)(i * 4);
                    var expected = i >= 1_000 && i < 2_000 ? 800_000 + (i - 1_000) : i;
                    var actual = tree.TryGet(key, ref accessor).Value;
                    if (actual != expected)
                    {
                        Assert.Fail($"key 0x{key:X8}: expected {expected}, got {actual}");
                    }
                }

                tree.CheckConsistency(ref accessor);
                Assert.That(tree.ValidateNodeKeyOrder(ref accessor), Is.Null);
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
}
