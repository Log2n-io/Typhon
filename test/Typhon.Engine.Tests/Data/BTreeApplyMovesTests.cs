using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>ApplyMoves</c> — the tick fence's batched index moves (#1346): sorted once, applied per key and per leaf. Each test states the final state a loop of
/// single-entry moves would reach, and checks the tree against it and against its own consistency validator.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BTreeApplyMovesTests
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
                var raw = $"moves_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private delegate void UniqueBody(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    private delegate void MultiBody(IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    private delegate void TreeBody<TTree>(TTree tree, ref ChunkAccessor<PersistentStore> accessor);

    private unsafe void WithTree<TTree, TChunk>(Func<ChunkBasedSegment<PersistentStore>, TTree> create, TreeBody<TTree> body) where TChunk : unmanaged
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 1_000, sizeof(TChunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                body(create(segment), ref accessor);
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

    private unsafe void WithUnique(UniqueBody body)
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
                body(new IntSingleBTree<PersistentStore>(segment), ref accessor);
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

    private unsafe void WithMulti(MultiBody body)
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

    private delegate void SegmentBody<TTree>(TTree tree, ChunkBasedSegment<PersistentStore> segment, EpochManager epochManager,
        ref ChunkAccessor<PersistentStore> accessor);

    private unsafe void WithSegment<TTree>(Func<ChunkBasedSegment<PersistentStore>, TTree> create, SegmentBody<TTree> body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 1_000, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        var minEvents = IndexMoveBatch.MinEventsPerRange;
        IndexMoveBatch.MinEventsPerRange = 64;   // a batch of a few thousand moves cut into several ranges
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                body(create(segment), segment, epochManager, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            IndexMoveBatch.MinEventsPerRange = minEvents;
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Applies a batch the way the parallel fence does (#1346): prepared, cut into leaf-disjoint ranges, every range on its own thread at once, then finished
    /// on this one. The range <paramref name="skip"/> names is not applied, as when its work item never ran: the finish must apply it.
    /// </summary>
    private static IndexMoveOutcome ApplyPartitioned(BTreeBase<PersistentStore> tree, IndexMove[] moves, ChunkBasedSegment<PersistentStore> segment,
        EpochManager epochManager, ref ChunkAccessor<PersistentStore> accessor, out int rangeCount, int skip = -1)
    {
        var ranges = new IndexMoveRange[8];
        var count = rangeCount = tree.PrepareMoveBatch(moves, ranges.Length, ranges, ref accessor);

        using var start = new Barrier(count);
        var threads = new Thread[count];
        Exception failure = null;
        for (var r = 0; r < count; r++)
        {
            var range = r;
            threads[r] = new Thread(() =>
            {
                var depth = epochManager.EnterScope();
                var rangeAccessor = segment.CreateChunkAccessor();
                try
                {
                    start.SignalAndWait();
                    if (range != skip)
                    {
                        tree.ApplyMoveRange(moves, ref ranges[range], ref rangeAccessor);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref failure, e, null);
                }
                finally
                {
                    rangeAccessor.Dispose();
                    epochManager.ExitScope(depth);
                }
            });
            threads[r].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        if (failure != null)
        {
            throw failure;
        }

        var finishedBefore = Interlocked.Read(ref IndexMoveBatch.RangesAppliedAtFinish);
        var outcome = tree.FinishMoveBatch(moves, ranges.AsSpan(0, count), ref accessor);
        Assert.That(Interlocked.Read(ref IndexMoveBatch.RangesAppliedAtFinish) - finishedBefore, Is.EqualTo(skip >= 0 ? 1 : 0),
            "the finish applies exactly the ranges no thread did");
        var gathered = 0;
        for (var r = 0; r < count; r++)
        {
            gathered += ranges[r].Events;
        }

        Assert.That(gathered, Is.EqualTo(2 * moves.Length), "the ranges' key intervals tile the batch: every event gathered once");
        return outcome;
    }

    private static IndexMove Move(int oldKey, int newKey, int value, int elementId = 0)
        => new() { OldKey = KeyBytes8.FromInt(oldKey), NewKey = KeyBytes8.FromInt(newKey), Value = value, ElementId = elementId };

    /// <summary>Every key of a unique tree with its value, by enumerating each expected key and the count.</summary>
    private static void AssertUnique(IntSingleBTree<PersistentStore> tree, Dictionary<int, int> expected, ref ChunkAccessor<PersistentStore> accessor)
    {
        tree.CheckConsistency(ref accessor);
        Assert.That(tree.EntryCount, Is.EqualTo(expected.Count), "key count");
        foreach (var (key, value) in expected)
        {
            var got = tree.TryGet(key, ref accessor);
            Assert.That(got.IsSuccess, Is.True, $"key {key} present");
            Assert.That(got.Value, Is.EqualTo(value), $"key {key}'s value");
        }
    }

    private static Dictionary<int, List<int>> MultiContents(IntMultipleBTree<PersistentStore> tree, int lo, int hi)
    {
        var contents = new Dictionary<int, List<int>>();
        using var e = tree.EnumerateRangeMultiple(lo, hi);
        while (e.MoveNext())
        {
            var values = new List<int>();
            do
            {
                foreach (var v in e.CurrentValues)
                {
                    values.Add(v);
                }
            }
            while (e.NextChunk());   // a key's values span its buffer's chunks

            contents[e.CurrentKey] = values;
        }

        return contents;
    }

    /// <summary>
    /// 3 000 keys, every one moved by +1 (2i → 2i + 1): each key is vacated and a new one taken in the same leaf, the fence's common unique shape.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_EveryKeyShifted_MovesThemAll()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var expected = new Dictionary<int, int>();
            var moves = new IndexMove[3_000];
            for (var i = 0; i < moves.Length; i++)
            {
                tree.Add(2 * i, i, ref accessor);
                moves[i] = Move(2 * i, 2 * i + 1, i);
                expected[2 * i + 1] = i;
            }

            var outcome = tree.ApplyMoves(moves, ref accessor);

            Assert.That(outcome.Misses + outcome.Collisions, Is.Zero);
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// #1342: a swap, a cycle and a chain of unique keys inside one batch are legal — the final state holds each key once — in whatever order the moves come.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_SwapsCyclesAndChains_AreDecidedOnTheFinalState()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            for (var k = 1; k <= 200; k++)
            {
                tree.Add(k * 10, k, ref accessor);
            }

            var moves = new[]
            {
                Move(20, 10, 2),   // swap 10 <-> 20, listed second-first
                Move(10, 20, 1),
                Move(30, 40, 3),   // cycle 30 -> 40 -> 50 -> 30
                Move(40, 50, 4),
                Move(50, 30, 5),
                Move(70, 80, 7),   // chain 70 -> 80 -> 2005 (a key no one holds)
                Move(80, 2_005, 8),
            };

            var outcome = tree.ApplyMoves(moves, ref accessor);

            Assert.That(outcome.Misses + outcome.Collisions, Is.Zero, "every move is legal on the final state");
            var expected = Enumerable.Range(1, 200).ToDictionary(k => k * 10, k => k);
            expected[10] = 2;
            expected[20] = 1;
            expected[40] = 3;
            expected[50] = 4;
            expected[30] = 5;
            expected[80] = 7;
            expected.Remove(70);
            expected[2_005] = 8;
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// A move to a key another entity holds and keeps collides, and so does the second of two moves to one free key: each is counted, the first reported,
    /// the holder untouched, and the colliding entity leaves its old key without taking the new one — its data no longer holds the old key.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_ACollision_IsCountedAndLeavesTheHolder()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            for (var k = 1; k <= 100; k++)
            {
                tree.Add(k * 10, k, ref accessor);
            }

            var moves = new[]
            {
                Move(10, 500, 1),    // 500 is held by entity 50, which does not move
                Move(20, 2_001, 2),  // two moves to one free key: the second in key order collides
                Move(30, 2_001, 3),
            };

            var outcome = tree.ApplyMoves(moves, ref accessor);

            Assert.That(outcome.Collisions, Is.EqualTo(2));
            Assert.That(outcome.FirstCollision, Is.EqualTo(0), "the first collision in key order: the move to 500");
            var expected = Enumerable.Range(1, 100).ToDictionary(k => k * 10, k => k);
            expected.Remove(10);   // collided: leaves 10, does not take 500
            expected.Remove(20);   // took 2001
            expected.Remove(30);   // collided: leaves 30, 2001 is taken
            expected[2_001] = 2;
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// A collided move still vacates its old key, so nothing cascades: B (20 → 30) collides with C, who keeps 30, and leaves 20, which A (10 → 20) takes.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_ACollidedMove_StillVacatesItsKey()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            for (var k = 1; k <= 100; k++)
            {
                tree.Add(k * 10, k, ref accessor);
            }

            var outcome = tree.ApplyMoves(new[] { Move(10, 20, 1), Move(20, 30, 2) }, ref accessor);

            Assert.That(outcome.Collisions, Is.EqualTo(1), "B on C's key; A takes the key B left");
            var expected = Enumerable.Range(1, 100).ToDictionary(k => k * 10, k => k);
            expected.Remove(10);
            expected[20] = 1;
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// Moves that fill one leaf past its capacity and empty another below half take the single-entry path, which splits and merges; the result is the
    /// same.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_MovesThatSplitOrMergeLeaves_TakeTheSingleEntryPath()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var expected = new Dictionary<int, int>();
            for (var k = 0; k < 2_000; k++)
            {
                tree.Add(k * 100, k, ref accessor);
                expected[k * 100] = k;
            }

            // 300 keys from the top of the range all move into the gaps of the first few hundreds (never onto a multiple of 100, which an unmoved
            // entity holds): the leaves owning those ranges overflow, and the top leaves empty.
            var moves = new IndexMove[300];
            for (var i = 0; i < moves.Length; i++)
            {
                var k = 1_999 - i;
                var to = i / 99 * 100 + 1 + i % 99;
                moves[i] = Move(k * 100, to, k);
                expected.Remove(k * 100);
                expected[to] = k;
            }

            var outcome = tree.ApplyMoves(moves, ref accessor);

            Assert.That(outcome.Misses + outcome.Collisions, Is.Zero);
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// Review 2, B1: two departures from one key — reachable, since a collision keeps the loser's entry on its old key while its data moves on. Only the
    /// key's holder vacates it; the other departure is a miss, and its arrival still applies. Treating both as vacating re-inserted one of them in the
    /// leaf merge.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_TwoDeparturesFromOneKey_OnlyTheHolderVacatesIt()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            for (var k = 1; k <= 100; k++)
            {
                tree.Add(k * 10, k, ref accessor);   // key 20 is held by entity 2
            }

            var outcome = tree.ApplyMoves(new[] { Move(20, 2_005, 99), Move(20, 2_006, 2) }, ref accessor);

            Assert.That(outcome.Misses, Is.EqualTo(1), "entity 99 does not hold 20");
            Assert.That(outcome.Collisions, Is.Zero);
            var expected = Enumerable.Range(1, 100).ToDictionary(k => k * 10, k => k);
            expected.Remove(20);
            expected[2_005] = 99;
            expected[2_006] = 2;
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// Every key shifted +1, the last onto a key an entity keeps: only the last move collides — each earlier one takes the key its neighbour left. (With
    /// collided moves keeping their old keys this cascaded through all of them, review 2.)
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_AChainOntoAHeldKey_CollidesOnlyItsLastMove()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int N = 20_000;
            var expected = new Dictionary<int, int>();
            var moves = new IndexMove[N];
            for (var k = 0; k <= N; k++)
            {
                tree.Add(k, k, ref accessor);
                expected[k] = k;
            }

            for (var k = 0; k < N; k++)
            {
                moves[k] = Move(k, k + 1, k);   // N is held by entity N, which stays
            }

            var outcome = tree.ApplyMoves(moves, ref accessor);

            Assert.That(outcome.Collisions, Is.EqualTo(1), "only entity N - 1, onto N");
            expected.Remove(0);
            for (var k = 1; k < N; k++)
            {
                expected[k] = k - 1;
            }

            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>An empty tree: the departure is a miss, the arrival inserts its key.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_OnAnEmptyTree_InsertsTheArrivals()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var outcome = tree.ApplyMoves(new[] { Move(5, 6, 1), Move(7, 8, 2) }, ref accessor);

            Assert.That(outcome.Misses, Is.EqualTo(2));
            AssertUnique(tree, new Dictionary<int, int> { [6] = 1, [8] = 2 }, ref accessor);
        });
    }

    /// <summary>A 64-bit key (the L64 node layout), negative values included: every key shifted.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public unsafe void LongKeys_EveryKeyShifted_MovesThemAll()
    {
        WithTree<LongSingleBTree<PersistentStore>, Index64Chunk>(static s => new LongSingleBTree<PersistentStore>(s),
            static (LongSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
            {
                var moves = new IndexMove[2_000];
                for (var i = 0; i < moves.Length; i++)
                {
                    tree.Add(-5_000_000_000L + 2L * i, i, ref accessor);
                    moves[i] = new IndexMove
                    {
                        OldKey = KeyBytes8.FromLong(-5_000_000_000L + 2L * i), NewKey = KeyBytes8.FromLong(-5_000_000_000L + 2L * i + 1), Value = i,
                    };
                }

                var outcome = tree.ApplyMoves(moves, ref accessor);
                Assert.That(outcome.Misses + outcome.Collisions, Is.Zero);
                tree.CheckConsistency(ref accessor);
                for (var i = 0; i < moves.Length; i++)
                {
                    Assert.That(tree.TryGet(-5_000_000_000L + 2L * i + 1, ref accessor).Value, Is.EqualTo(i), $"long key {i}");
                }
            });
    }

    /// <summary>A char key (L16 node layout, radix on its UTF-16 code unit): every key shifted. Its own test: the harness's page file is scoped to
    /// one.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public unsafe void CharKeys_EveryKeyShifted_MovesThemAll()
    {
        WithTree<CharSingleBTree<PersistentStore>, Index16Chunk>(static s => new CharSingleBTree<PersistentStore>(s),
            static (CharSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
            {
                var moves = new IndexMove[300];
                for (var i = 0; i < moves.Length; i++)
                {
                    tree.Add((char)(1_000 + 2 * i), i, ref accessor);
                    moves[i] = new IndexMove
                    {
                        OldKey = KeyBytes8.FromUShort((ushort)(1_000 + 2 * i)), NewKey = KeyBytes8.FromUShort((ushort)(1_001 + 2 * i)), Value = i,
                    };
                }

                var outcome = tree.ApplyMoves(moves, ref accessor);
                Assert.That(outcome.Misses + outcome.Collisions, Is.Zero);
                tree.CheckConsistency(ref accessor);
                for (var i = 0; i < moves.Length; i++)
                {
                    Assert.That(tree.TryGet((char)(1_001 + 2 * i), ref accessor).Value, Is.EqualTo(i), $"char key {i}");
                }
            });
    }

    /// <summary>A move whose old key is absent is a miss; the entity is indexed under its new key anyway.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Unique_AMoveFromAnAbsentKey_IsAMiss()
    {
        WithUnique(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            for (var k = 1; k <= 100; k++)
            {
                tree.Add(k * 10, k, ref accessor);
            }

            var outcome = tree.ApplyMoves(new[] { Move(15, 16, 99) }, ref accessor);

            Assert.That(outcome.Misses, Is.EqualTo(1));
            var expected = Enumerable.Range(1, 100).ToDictionary(k => k * 10, k => k);
            expected[16] = 99;
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// AllowMultiple, the fence's ±1 shape: 50 keys of 40 values each, every value moving to the next key. Every departure pairs with an arrival under its
    /// key, so the buffers are rewritten in place; the returned element ids then remove each value exactly.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Multi_EveryValueShifted_MovesThemAll_AndReturnsUsableElementIds()
    {
        WithMulti(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Keys = 50;
            const int PerKey = 40;
            var moves = new IndexMove[Keys * PerKey];
            for (var k = 0; k < Keys; k++)
            {
                for (var j = 0; j < PerKey; j++)
                {
                    var value = k * PerKey + j;
                    var id = tree.Add(k, value, ref accessor);
                    moves[value] = Move(k, k + 1, value, id);
                }
            }

            var outcome = tree.ApplyMoves(moves, ref accessor);

            Assert.That(outcome.Misses, Is.Zero);
            tree.CheckConsistency(ref accessor);
            var contents = MultiContents(tree, -10, Keys + 10);
            Assert.That(contents.ContainsKey(0), Is.False, "key 0 emptied and left the tree");
            for (var k = 1; k <= Keys; k++)
            {
                Assert.That(contents[k], Is.EquivalentTo(Enumerable.Range((k - 1) * PerKey, PerKey)), $"key {k}");
            }

            // The element ids name the right chunks: removing every value by its id empties the tree.
            for (var m = 0; m < moves.Length; m++)
            {
                Assert.That(tree.RemoveValue(m / PerKey + 1, moves[m].ElementId, m, ref accessor), Is.True, $"value {m} removed by its new element id");
            }

            Assert.That(MultiContents(tree, -10, Keys + 10), Is.Empty);
        });
    }

    /// <summary>
    /// #1343: values leaving a key empty chunks of its buffer, and the batch gives them back, so moving the same values from one key to another and back
    /// does not grow the segment. Before, an emptied chunk stayed linked until a reader walked the buffer: every round trip appended into new chunks.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Multi_ValuesLeavingAKey_GiveItsEmptiedChunksBack()
    {
        WithSegment(static s => new IntMultipleBTree<PersistentStore>(s),
            static (IntMultipleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, EpochManager _,
                ref ChunkAccessor<PersistentStore> accessor) =>
            {
                const int Values = 3_000;
                const int Stay = 100;   // values that never leave key 1: its buffer, and its first chunks, outlive every trip
                var ids = new int[Values];
                for (var v = 0; v < Values; v++)
                {
                    ids[v] = tree.Add(1, v, ref accessor);
                }

                tree.Add(2, -1, ref accessor);   // key 2 exists: its buffer receives, and gives back on the return
                var chunksPerTrip = new List<int>();
                for (var trip = 0; trip < 4; trip++)
                {
                    var (from, to) = trip % 2 == 0 ? (1, 2) : (2, 1);
                    var moves = new IndexMove[Values - Stay];
                    for (var m = 0; m < moves.Length; m++)
                    {
                        var v = Stay + m;
                        moves[m] = Move(from, to, v, ids[v]);
                    }

                    var outcome = tree.ApplyMoves(moves, ref accessor);
                    Assert.That(outcome.Misses, Is.Zero, $"trip {trip}");
                    for (var m = 0; m < moves.Length; m++)
                    {
                        ids[Stay + m] = moves[m].ElementId;
                    }

                    chunksPerTrip.Add(segment.AllocatedChunkCount);
                }

                tree.CheckConsistency(ref accessor);
                var contents = MultiContents(tree, 0, 3);
                Assert.That(contents[1], Is.EquivalentTo(Enumerable.Range(0, Values)), "key 1 holds every value again");
                Assert.That(contents[2], Is.EquivalentTo(new[] { -1 }), "key 2 holds its own");
                Assert.That(chunksPerTrip[3], Is.LessThanOrEqualTo(chunksPerTrip[1]),
                    $"a round trip grew the segment: allocated chunks after each trip {string.Join(", ", chunksPerTrip)}");
            });
    }

    /// <summary>
    /// AllowMultiple, unbalanced: values leave a key with no arrivals (its last one empties it), others arrive at keys that do not exist yet, and some join
    /// keys that receive more than leave. The structural part takes the single-entry path; the contents are what a loop of moves would leave.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Multi_UnbalancedMoves_CreateAndEmptyKeys()
    {
        WithMulti(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var expected = new Dictionary<int, List<int>>();
            var ids = new Dictionary<int, int>();
            for (var v = 0; v < 300; v++)
            {
                var key = v % 10;   // keys 0..9, 30 values each
                ids[v] = tree.Add(key, v, ref accessor);
                (expected.TryGetValue(key, out var l) ? l : expected[key] = []).Add(v);
            }

            var moves = new List<IndexMove>();
            void MoveValue(int v, int from, int to)
            {
                moves.Add(Move(from, to, v, ids[v]));
                expected[from].Remove(v);
                (expected.TryGetValue(to, out var l) ? l : expected[to] = []).Add(v);
            }

            for (var v = 0; v < 300; v += 10)
            {
                MoveValue(v, 0, 1_000 + v);   // key 0 empties; each value creates a key
            }

            for (var v = 1; v < 300; v += 10)
            {
                MoveValue(v, 1, 2);           // key 1 empties into key 2
            }

            for (var v = 3; v < 60; v += 10)
            {
                MoveValue(v, 3, 7);           // some of key 3 into key 7
            }

            var batch = moves.ToArray();
            var outcome = tree.ApplyMoves(batch, ref accessor);

            Assert.That(outcome.Misses, Is.Zero);
            tree.CheckConsistency(ref accessor);
            var contents = MultiContents(tree, -1, 2_000);
            foreach (var key in expected.Keys.Where(k => expected[k].Count == 0).ToList())
            {
                expected.Remove(key);
            }

            Assert.That(contents.Keys, Is.EquivalentTo(expected.Keys));
            foreach (var (key, values) in expected)
            {
                Assert.That(contents[key], Is.EquivalentTo(values),
                    $"key {key}: missing [{string.Join(",", values.Except(contents[key]))}], extra [{string.Join(",", contents[key].Except(values))}], "
                    + $"{contents[key].Count} held vs {values.Count}");
            }
        });
    }

    /// <summary>
    /// The parallel fence's form (#1346): a unique batch cut into ranges applied on several threads at once, then finished, reaches what the whole batch
    /// reaches — shifts in place, swaps across one key, a collision decided inside its range. With one range left unapplied, the finish applies it.
    /// </summary>
    [TestCase(-1)]
    [TestCase(1)]
    [CancelAfter(30_000)]
    [VerifiesRule("IX-08")]
    public void Unique_APartitionedBatch_AppliedConcurrently_ReachesTheWholeBatchState(int skip)
    {
        WithSegment(static s => new IntSingleBTree<PersistentStore>(s), (IntSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochManager, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int N = 20_000;
            var expected = new Dictionary<int, int>();
            for (var k = 0; k < N; k++)
            {
                tree.Add(2 * k, k, ref accessor);
                expected[2 * k] = k;
            }

            // Swaps make every key a run of two events, a departure and an arrival: a cut that is not snapped to a leaf edge splits some run, and the
            // range holding the arrival sees the key held — a collision the whole batch does not have.
            var moves = new List<IndexMove>();
            for (var k = 0; k < 19_000; k += 2)
            {
                moves.Add(Move(2 * k, 2 * k + 2, k));   // swap two neighbours' keys
                moves.Add(Move(2 * k + 2, 2 * k, k + 1));
                expected[2 * k + 2] = k;
                expected[2 * k] = k + 1;
            }

            for (var k = 19_000; k < 19_400; k++)
            {
                moves.Add(Move(2 * k, 2 * k + 1, k));   // vacate and take a key in the same leaf
                expected.Remove(2 * k);
                expected[2 * k + 1] = k;
            }

            moves.Add(Move(2 * 19_500, 2 * 19_501, 19_500));   // onto a key its holder keeps: collides, leaves its old key
            expected.Remove(2 * 19_500);

            var outcome = ApplyPartitioned(tree, moves.ToArray(), segment, epochManager, ref accessor, out var ranges, skip);

            Assert.That(ranges, Is.GreaterThanOrEqualTo(4), "the batch was cut into several ranges");
            Assert.That(outcome.Misses, Is.Zero);
            Assert.That(outcome.Collisions, Is.EqualTo(1));
            AssertUnique(tree, expected, ref accessor);
        });
    }

    /// <summary>
    /// Review 6 (#1346): on random batches — swaps, shifts onto free keys, moves onto held keys, two moves onto one key, departures from keys their entity
    /// does not hold — the partitioned form reaches exactly what the whole batch reaches on an identical tree: the same outcome, the same first collision,
    /// the same tree. Each range sorts its own events, so this is what pins their order to the whole batch's.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [CancelAfter(30_000)]
    [VerifiesRule("IX-08")]
    public void Unique_APartitionedBatch_MatchesTheWholeBatch_OnRandomMoves(int seed)
    {
        WithSegment(static s => new IntSingleBTree<PersistentStore>(s), (IntSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochManager, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int N = 12_000;
            var whole = new IntSingleBTree<PersistentStore>(segment, key: new BTreeStableKey(1, 0));   // its own directory entry
            for (var k = 0; k < N; k++)
            {
                tree.Add(3 * k, k, ref accessor);
                whole.Add(3 * k, k, ref accessor);
            }

            var random = new Random(seed);
            var moves = new List<IndexMove>();
            var moved = new HashSet<int>();
            for (var n = 0; n < 6_000; n++)
            {
                var k = random.Next(N);
                if (!moved.Add(k))
                {
                    continue;   // at most one move per entity, as the drain stages
                }

                var to = random.Next(4) switch
                {
                    0 => 3 * random.Next(N),               // a key someone holds, maybe leaving it too
                    1 => 3 * random.Next(N) + 1,           // a free key, maybe taken twice
                    2 => 3 * k + 2,                        // a free key of its own
                    _ => 3 * ((k + 1) % N),                // its neighbour's key
                };
                var from = random.Next(50) == 0 ? 3 * random.Next(N) : 3 * k;   // now and then a departure the entity does not hold
                moves.Add(Move(from, to, k));
            }

            var batch = moves.ToArray();
            var expected = whole.ApplyMoves((IndexMove[])batch.Clone(), ref accessor);
            var outcome = ApplyPartitioned(tree, batch, segment, epochManager, ref accessor, out var ranges);

            Assert.That(ranges, Is.GreaterThanOrEqualTo(4), "the batch was cut into several ranges");
            Assert.That(outcome.Misses, Is.EqualTo(expected.Misses), "misses");
            Assert.That(outcome.Collisions, Is.EqualTo(expected.Collisions), "collisions");
            Assert.That(outcome.FirstCollision, Is.EqualTo(expected.FirstCollision), "the first collision");
            tree.CheckConsistency(ref accessor);
            Assert.That(tree.EntryCount, Is.EqualTo(whole.EntryCount), "key count");
            for (var key = 0; key < 3 * N; key++)
            {
                var got = tree.TryGet(key, ref accessor);
                var want = whole.TryGet(key, ref accessor);
                Assert.That(got.IsSuccess, Is.EqualTo(want.IsSuccess), $"key {key} present");
                if (want.IsSuccess)
                {
                    Assert.That(got.Value, Is.EqualTo(want.Value), $"key {key}'s value");
                }
            }
        });
    }

    /// <summary>
    /// The AllowMultiple counterpart: a batch that pairs in place, overflows into new keys and empties one, cut into ranges applied concurrently, reaches the
    /// expected buffers, and every element id it hands back names the entity's element — moving every value back by those ids misses none.
    /// </summary>
    [TestCase(-1)]
    [TestCase(2)]
    [CancelAfter(30_000)]
    [VerifiesRule("IX-08")]
    public void Multi_APartitionedBatch_AppliedConcurrently_ReachesTheWholeBatchState(int skip)
    {
        WithSegment(static s => new IntMultipleBTree<PersistentStore>(s), (IntMultipleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochManager, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int N = 20_000;
            var keyOf = new int[N];
            var ids = new int[N];
            for (var v = 0; v < N; v++)
            {
                keyOf[v] = v % 500;   // 40 values per key
                ids[v] = tree.Add(keyOf[v], v, ref accessor);
            }

            var original = MultiContents(tree, -1, 100_000);
            var moves = new List<IndexMove>();
            var newKeyOf = (int[])keyOf.Clone();
            for (var v = 0; v < N; v++)
            {
                var k = keyOf[v];
                var to = k == 250 ? 251                              // key 250 empties
                    : v % 7 == 0 ? 10_000 + v                         // a key that does not exist yet
                    : v % 3 == 0 ? ((k + 1) % 500 == 250 ? 251 : (k + 1) % 500)
                    : k;
                if (to != k)
                {
                    moves.Add(Move(k, to, v, ids[v]));
                    newKeyOf[v] = to;
                }
            }

            var batch = moves.ToArray();
            var outcome = ApplyPartitioned(tree, batch, segment, epochManager, ref accessor, out var ranges, skip);

            Assert.That(ranges, Is.GreaterThanOrEqualTo(4), "the batch was cut into several ranges");
            Assert.That(outcome.Misses, Is.Zero);
            tree.CheckConsistency(ref accessor);
            var expected = new Dictionary<int, List<int>>();
            for (var v = 0; v < N; v++)
            {
                (expected.TryGetValue(newKeyOf[v], out var l) ? l : expected[newKeyOf[v]] = []).Add(v);
            }

            var contents = MultiContents(tree, -1, 100_000);
            Assert.That(contents.Keys, Is.EquivalentTo(expected.Keys), "key 250 left, the new keys created");
            foreach (var (key, values) in expected)
            {
                Assert.That(contents[key], Is.EquivalentTo(values), $"key {key}");
            }

            var back = new IndexMove[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                back[i] = new IndexMove { OldKey = batch[i].NewKey, NewKey = batch[i].OldKey, Value = batch[i].Value, ElementId = batch[i].ElementId };
            }

            Assert.That(tree.ApplyMoves(back, ref accessor).Misses, Is.Zero, "every element id handed back names its entity's element");
            var restored = MultiContents(tree, -1, 100_000);
            Assert.That(restored.Keys, Is.EquivalentTo(original.Keys));
            foreach (var (key, values) in original)
            {
                Assert.That(restored[key], Is.EquivalentTo(values), $"key {key} restored");
            }
        });
    }

    /// <summary>An AllowMultiple move naming an element that is not where its id says is a miss; the value is indexed under its new key anyway.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-08")]
    public void Multi_AMoveWithAWrongElementId_IsAMiss()
    {
        WithMulti(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var a = tree.Add(1, 100, ref accessor);
            tree.Add(2, 200, ref accessor);
            tree.Add(3, 300, ref accessor);
            var idOfThree = tree.Add(3, 301, ref accessor);

            // Value 100 leaves key 1 naming key 3's chunk, and value 200 arrives at key 1, pairing with it.
            var outcome = tree.ApplyMoves(new[] { Move(1, 5, 100, idOfThree), Move(2, 1, 200, a) }, ref accessor);

            Assert.That(outcome.Misses, Is.GreaterThanOrEqualTo(1));
            tree.CheckConsistency(ref accessor);
            var contents = MultiContents(tree, 0, 10);
            Assert.That(contents[5], Does.Contain(100), "the missed value still reaches its new key");
            Assert.That(contents[1], Does.Contain(200), "the arrival is indexed");
        });
    }
}
