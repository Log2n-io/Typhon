using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A sparse B+Tree (IX-09) at its own entry points: key 0 is "no data", so an insert of it adds nothing, a removal of it succeeds, a move to it removes, a
/// move from it inserts, and a lookup of it finds nothing — through the single-entry calls and through the fence's move batch, whole and partitioned.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SparseBTreeTests
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
                var raw = $"sparse_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private delegate void Body<TTree>(TTree tree, ChunkBasedSegment<PersistentStore> segment, EpochManager epochManager,
        ref ChunkAccessor<PersistentStore> accessor);

    private unsafe void With<TTree>(Func<ChunkBasedSegment<PersistentStore>, TTree> create, Body<TTree> body) where TTree : BTreeBase<PersistentStore>
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
                var tree = create(segment);
                tree.Sparse = true;
                body(tree, segment, epochManager, ref accessor);
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

    private static IndexMove Move(int oldKey, int newKey, int value, int elementId = 0)
        => new() { OldKey = KeyBytes8.FromInt(oldKey), NewKey = KeyBytes8.FromInt(newKey), Value = value, ElementId = elementId };

    /// <summary>Every key of a multi-value tree with its values, the whole key range, 0 included.</summary>
    private static Dictionary<int, List<int>> Contents(IntMultipleBTree<PersistentStore> tree)
    {
        var contents = new Dictionary<int, List<int>>();
        using var e = tree.EnumerateRangeMultiple(int.MinValue, int.MaxValue);
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
            while (e.NextChunk());

            contents[e.CurrentKey] = values;
        }

        return contents;
    }

    /// <summary>
    /// Applies a batch as the parallel fence does: prepared into up to 8 leaf-disjoint ranges, every range applied, then finished. Single-threaded — the
    /// concurrency of the ranges is <c>BTreeApplyMovesTests</c>' subject; this one's is which events the gather keeps.
    /// </summary>
    private static IndexMoveOutcome ApplyInRanges(BTreeBase<PersistentStore> tree, IndexMove[] moves, ref ChunkAccessor<PersistentStore> accessor,
        out int rangeCount, out int events)
    {
        var ranges = new IndexMoveRange[8];
        rangeCount = tree.PrepareMoveBatch(moves, ranges.Length, ranges, ref accessor);
        events = 0;
        for (var r = 0; r < rangeCount; r++)
        {
            tree.ApplyMoveRange(moves, ref ranges[r], ref accessor);
            events += ranges[r].Events;
        }

        return tree.FinishMoveBatch(moves, ranges.AsSpan(0, rangeCount), ref accessor);
    }

    /// <summary>Whether a multi-value lookup of <paramref name="key"/> finds a buffer; the buffer's accessor is let go of at once.</summary>
    private static bool Finds(IntMultipleBTree<PersistentStore> tree, int key, ref ChunkAccessor<PersistentStore> accessor)
    {
        using var values = tree.TryGetMultiple(key, ref accessor);
        return values.IsValid;
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Multi_EveryEntryPoint_TreatsKeyZeroAsNoEntry()
    {
        With(s => new IntMultipleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            Assert.That(tree.Add(0, 100, ref accessor), Is.Zero, "an insert of 0 stores no element");
            Assert.That(tree.EntryCount, Is.Zero, "and files no key");
            Assert.That(Finds(tree, 0, ref accessor), Is.False, "a lookup of 0 finds nothing");
            Assert.That(tree.RemoveValue(0, 0, 100, ref accessor), Is.True, "a removal from 0 is already done");
            Assert.That(tree.Remove(0, out _, ref accessor), Is.True);

            // Off 0: an insert, with an element id a removal reads back.
            var id = tree.MoveValue(0, 5, 0, 101, ref accessor, out _, out _);
            Assert.That(Contents(tree), Is.EquivalentTo(new Dictionary<int, List<int>> { [5] = [101] }), "moved off 0, into key 5");

            // Onto 0: a removal; the entity keeps no element.
            Assert.That(tree.MoveValue(5, 0, id, 101, ref accessor, out _, out _), Is.Zero);
            Assert.That(tree.EntryCount, Is.Zero, "key 5 emptied and dropped");

            // 0 to 0 changes nothing; a removal that misses still reports -1, which MoveValueOrReindex counts.
            Assert.That(tree.MoveValue(0, 0, 0, 102, ref accessor, out _, out _), Is.Zero);
            Assert.That(tree.MoveValue(7, 0, 3, 103, ref accessor, out _, out _), Is.EqualTo(-1), "key 7 holds no such element");
            tree.CheckConsistency(ref accessor);
        });
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Unique_EveryEntryPoint_TreatsKeyZeroAsNoEntry()
    {
        With(s => new IntSingleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            for (var v = 0; v < 10; v++)
            {
                tree.Add(0, v, ref accessor);   // any number of entities hold 0
            }

            Assert.That(tree.EntryCount, Is.Zero);
            Assert.That(tree.TryGet(0, ref accessor).IsSuccess, Is.False);

            Assert.That(tree.Move(0, 3, 30, ref accessor), Is.True, "off 0: an insert");
            Assert.That(tree.TryGet(3, ref accessor).Value, Is.EqualTo(30));
            Assert.That(tree.Move(3, 0, 30, ref accessor), Is.True, "onto 0: a removal");
            Assert.That(tree.EntryCount, Is.Zero);
            Assert.That(tree.Move(0, 0, 31, ref accessor), Is.True);
            Assert.That(tree.Move(9, 0, 31, ref accessor), Is.False, "a removal of a key the tree does not hold still misses");
            tree.CheckConsistency(ref accessor);
        });
    }

    /// <summary>
    /// The lookups answer from the guard, not from the tree's contents: a key-0 entry planted under the flag (as a tree built dense would hold) is not
    /// returned once the tree is sparse.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Multi_TheLookupNeverAnswersKeyZero_EvenWhenTheTreeHoldsOne()
    {
        With(s => new IntMultipleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            tree.Sparse = false;
            tree.Add(0, 100, ref accessor);
            Assert.That(Finds(tree, 0, ref accessor), Is.True, "premise: the planted key 0 is there");
            tree.Sparse = true;
            Assert.That(Finds(tree, 0, ref accessor), Is.False, "a planted key 0 is not answered");
        });
    }

    /// <inheritdoc cref="Multi_TheLookupNeverAnswersKeyZero_EvenWhenTheTreeHoldsOne"/>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Unique_TheLookupNeverAnswersKeyZero_EvenWhenTheTreeHoldsOne()
    {
        With(s => new IntSingleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            tree.Sparse = false;
            tree.Add(0, 100, ref accessor);
            Assert.That(tree.TryGet(0, ref accessor).IsSuccess, Is.True, "premise: the planted key 0 is there");
            tree.Sparse = true;
            Assert.That(tree.TryGet(0, ref accessor).IsSuccess, Is.False, "a planted key 0 is not answered");
        });
    }

    /// <summary>On a unique sparse tree a move off 0 onto a key another entity holds changes nothing and answers false, as a move between two keys does.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Unique_AMoveOffZeroOntoAHeldKey_ChangesNothing()
    {
        With(s => new IntSingleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            tree.Add(3, 30, ref accessor);
            Assert.That(tree.Move(0, 3, 31, ref accessor), Is.False, "key 3 is held");
            Assert.That(tree.TryGet(3, ref accessor).Value, Is.EqualTo(30), "and still by its holder");
            Assert.That(tree.EntryCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A batch of moves on and off 0, whole: the gather keeps only the non-zero sides, so a move onto 0 is a departure alone and one off 0 an arrival alone,
    /// and the element ids written back are 0 for an entity left in no group and a real element for one that joined.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Multi_ABatchOnAndOffZero_AppliesTheNonZeroSides_AndWritesBackTheIds()
    {
        With(s => new IntMultipleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            // 300 entities: value v in key v % 3 + 1 for v < 150, in no group (0) otherwise.
            var ids = new int[300];
            for (var v = 0; v < 150; v++)
            {
                ids[v] = tree.Add(v % 3 + 1, v, ref accessor);
            }

            // Each grouped entity leaves for 0 when v is even; each ungrouped one joins key 9 when v % 4 == 1; the rest go 0 to 0.
            var moves = new List<IndexMove>();
            var expected = new Dictionary<int, List<int>>();
            for (var v = 0; v < 300; v++)
            {
                var from = v < 150 ? v % 3 + 1 : 0;
                var to = v < 150 ? (v % 2 == 0 ? 0 : from) : (v % 4 == 1 ? 9 : 0);
                if (from != to || from == 0)
                {
                    moves.Add(Move(from, to, v, ids[v]));
                }

                if (to != 0)
                {
                    (expected.TryGetValue(to, out var list) ? list : expected[to] = []).Add(v);
                }
            }

            var batch = moves.ToArray();
            var outcome = tree.ApplyMoves(batch, ref accessor);
            Assert.That(outcome.Misses, Is.Zero, "every departure found its element");
            tree.CheckConsistency(ref accessor);

            var contents = Contents(tree);
            Assert.That(contents.Keys, Is.EquivalentTo(expected.Keys), "no key 0, and the joined key 9");
            foreach (var (key, values) in expected)
            {
                Assert.That(contents[key], Is.EquivalentTo(values), $"key {key}");
            }

            // The ids handed back move every value out again, exactly; a move onto 0 handed back 0.
            foreach (var m in batch)
            {
                var to = m.NewKey.AsInt();
                if (to == 0)
                {
                    Assert.That(m.ElementId, Is.Zero, $"value {m.Value} left for 0 and holds no element");
                }
                else
                {
                    Assert.That(tree.RemoveValue(to, m.ElementId, m.Value, ref accessor), Is.True, $"value {m.Value}'s element under key {to}");
                }
            }
        });
    }

    /// <summary>
    /// A partitioned batch where nine moves in ten are 0 to 0 — the shape of a group field most entities leave unset: the sample skips the 0s, so the cut
    /// still splits the real keys, and every range gathers only non-zero events. A batch of nothing but 0 to 0 applies as one range and changes nothing.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Multi_APartitionedBatchOfMostlyZeroKeys_GathersOnlyTheRealEvents()
    {
        With(s => new IntMultipleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            // One grouped entity in ten, ten to a key over 400 keys, so the elements a key's arrivals take have ids 0..9, not all 0.
            const int N = 40_000;
            static int KeyOf(int v) => 1 + v / 10 % 400;
            var ids = new int[N];
            for (var v = 0; v < N; v += 10)
            {
                ids[v] = tree.Add(KeyOf(v), v, ref accessor);
            }

            var moves = new IndexMove[N];
            var realEvents = 0;
            for (var v = 0; v < N; v++)
            {
                // The grouped ones rotate to the next key (two events, each arrival taking a departing element in place), every other stays in no group.
                moves[v] = v % 10 == 0 ? Move(KeyOf(v), KeyOf(v) % 400 + 1, v, ids[v]) : Move(0, 0, v);
                realEvents += v % 10 == 0 ? 2 : 0;
            }

            var outcome = ApplyInRanges(tree, moves, ref accessor, out var rangeCount, out var events);
            Assert.That(rangeCount, Is.GreaterThan(1), "the sample, without its 0s, still cuts the batch");
            Assert.That(events, Is.EqualTo(realEvents), "the ranges gathered the non-zero events, each once");
            Assert.That(outcome.Misses, Is.Zero);
            tree.CheckConsistency(ref accessor);
            Assert.That(tree.EntryCount, Is.EqualTo(400));

            // Every grouped entity back to 0 and the rest 0 to 0, in a batch whose per-move id array still holds the first batch's arrivals.
            var idle = new IndexMove[N];
            for (var v = 0; v < N; v++)
            {
                idle[v] = v % 10 == 0 ? Move(KeyOf(v) % 400 + 1, 0, v, moves[v].ElementId) : Move(0, 0, v, 77);
            }

            outcome = ApplyInRanges(tree, idle, ref accessor, out _, out _);
            Assert.That(outcome.Misses, Is.Zero, "the ids the first batch handed back name each element");
            Assert.That(tree.EntryCount, Is.Zero);
            for (var v = 0; v < N; v++)
            {
                Assert.That(idle[v].ElementId, Is.Zero, $"move {v} ended in no group and holds no element");
            }

            // Nothing but 0 to 0, over a tree of many leaves: no key to cut on, whatever the last batch's sample left behind.
            for (var k = 1; k <= 4_000; k++)
            {
                tree.Add(k, k, ref accessor);
            }

            var none = new IndexMove[2_000];
            for (var v = 0; v < none.Length; v++)
            {
                none[v] = Move(0, 0, v);
            }

            ApplyInRanges(tree, none, ref accessor, out var noneRanges, out var noneEvents);
            Assert.That(noneRanges, Is.EqualTo(1), "every sampled key was 0");
            Assert.That(noneEvents, Is.Zero);
            Assert.That(tree.EntryCount, Is.EqualTo(4_000));
        });
    }

    /// <summary>A unique batch on and off 0: any number may leave for 0, and arrivals off 0 still collide on a real key.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IX-09")]
    public void Unique_ABatchOnAndOffZero_DecidesCollisionsOnRealKeysOnly()
    {
        With(s => new IntSingleBTree<PersistentStore>(s), static (tree, _, _, ref accessor) =>
        {
            for (var v = 1; v <= 100; v++)
            {
                tree.Add(v, v, ref accessor);
            }

            var moves = new List<IndexMove>();
            for (var v = 1; v <= 100; v++)
            {
                moves.Add(Move(v, 0, v));            // every holder leaves for 0
            }

            moves.Add(Move(0, 500, 1_000));          // two arrivals off 0 at one key: one collides
            moves.Add(Move(0, 500, 1_001));
            moves.Add(Move(0, 0, 1_002));

            var outcome = tree.ApplyMoves(moves.ToArray(), ref accessor);
            Assert.That(outcome.Collisions, Is.EqualTo(1), "key 500 taken twice; the hundred 0s collide with nothing");
            tree.CheckConsistency(ref accessor);
            Assert.That(tree.EntryCount, Is.EqualTo(1));
            Assert.That(tree.TryGet(500, ref accessor).Value, Is.EqualTo(1_000));
        });
    }
}
