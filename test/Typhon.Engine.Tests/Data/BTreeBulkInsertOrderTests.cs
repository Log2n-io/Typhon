using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1101 — inserting a batch in key order builds the SAME tree as inserting it shuffled, for about a seventh of the structural work.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the measurement that replaced the item it was gating.</b> #1101 was filed to build a bulk-load-shaped pre-split — per-leaf overflow computed
/// up front, new leaves created with separators taken from the batch's own keys, parent inserts batched. #1098 measured the workload first: presenting the
/// same concentrated 3 000-key burst in order rather than shuffled took it from 3.59 entry-moves per key to 0.48 and from 1 417 ancestor writes to 208. The
/// sort gets most of what the pre-split was for, in a few lines rather than new code in the engine's most expensive file.
/// </para>
/// <para>
/// <b>Both halves are asserted, and the second is the one that makes the first mean anything.</b> That the sorted arm is cheaper is a performance claim; that
/// the two arms produce the same content is what says the cheaper path is not cutting a corner. A test asserting only the counters would pass against an
/// insert that quietly dropped entries.
/// </para>
/// <para>
/// <b>Counted, not timed.</b> The counters are exact and reproducible on any machine; a wall-clock ratio on this workload would be neither.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class BTreeBulkInsertOrderTests : TestBase<BTreeBulkInsertOrderTests>
{
    private const int BatchSize = 3000;

    private readonly struct Counts
    {
        public readonly long LeafFull;
        public readonly long Splits;
        public readonly long Spills;
        public readonly long EntriesMoved;
        public readonly int Entries;
        public readonly int Height;
        /// <summary>
        /// The KEYS the leaf chain holds, in leaf order. Keys and not key-value pairs: the index under test is <c>AllowMultiple</c>, so a leaf entry's
        /// value is a VSBS buffer root id rather than the inserted value, and a buffer id depends on allocation order — which the two arms have no reason
        /// to share. Comparing them failed with key 0 holding 934 in one arm and 5 in the other, both correct.
        /// </summary>
        public readonly List<int> Content;

        public Counts(BTree<int, PersistentStore> tree, List<int> content)
        {
            LeafFull = tree.LeafFullCount;
            Splits = tree.SplitCount;
            Spills = tree.SpillLeftCount + tree.SpillRightCount;
            EntriesMoved = tree.SpillEntriesMoved;
            Entries = tree.EntryCount;
            Height = tree.Height;
            Content = content;
        }

        public long ParentWrites => Splits + Spills;
    }

    /// <summary>Keys dense in a narrow range, which is what "concentrated" means for an index: every one of them lands in the same few leaves.</summary>
    private static BTreeInsert<int>[] Batch(bool shuffled)
    {
        var batch = new BTreeInsert<int>[BatchSize];
        for (var i = 0; i < BatchSize; i++)
        {
            batch[i] = new BTreeInsert<int>(i, i + 1);
        }

        if (shuffled)
        {
            // Fixed seed: the numbers are quoted in a design note and have to be reproducible.
            var rng = new Random(1101);
            for (var i = BatchSize - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (batch[i], batch[j]) = (batch[j], batch[i]);
            }
        }

        return batch;
    }

    /// <summary>
    /// Builds one tree from the batch and returns what it cost and what it holds. Each arm gets its own engine, so neither sees the other's tree.
    /// </summary>
    private Counts Build(bool shuffled, bool sortFirst)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<IxCountData>();
        dbe.InitializeArchetypes();

        var meta = ArchetypeRegistry.GetMetadata<IxCountUnit>();
        var tree = (BTree<int, PersistentStore>)dbe._archetypeStates[meta.ArchetypeId].ClusterState.IndexSlots[0].Fields[0].Index;
        tree.ResetDiagnostics();

        var batch = Batch(shuffled);

        using (EpochGuard.Enter(tree.Segment.Store.EpochManager))
        {
            var accessor = tree.Segment.CreateChunkAccessor(null);
            try
            {
                if (sortFirst)
                {
                    tree.AddBulkUnsorted(batch, ref accessor);
                }
                else
                {
                    // Deliberately NOT AddBulkSorted: in DEBUG that asserts, and the point of this arm is the cost of the unsorted order through the same
                    // per-entry path the sorted arm uses.
                    for (var i = 0; i < batch.Length; i++)
                    {
                        tree.Add(batch[i].Key, batch[i].Value, ref accessor, out _);
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var content = new List<int>(BatchSize);
        using (EpochGuard.Enter(tree.Segment.Store.EpochManager))
        {
            foreach (var kv in tree.EnumerateLeaves())
            {
                content.Add(kv.Key);
            }
        }

        return new Counts(tree, content);
    }

    /// <summary>
    /// The headline: same tree, far less structural work. Ratios are asserted loosely — the exact figures belong in the design note, and pinning them here
    /// would make this a regression test for the split policy rather than for the ordering effect.
    /// </summary>
    [Test]
    public void SortingTheBatchFirstBuildsTheSameTreeForAFractionOfTheWork()
    {
        var shuffled = Build(shuffled: true, sortFirst: false);
        var sorted = Build(shuffled: true, sortFirst: true);

        TestContext.Out.WriteLine($"shuffled: leafFull={shuffled.LeafFull} splits={shuffled.Splits} spills={shuffled.Spills} "
            + $"moved={shuffled.EntriesMoved} parentWrites={shuffled.ParentWrites} height={shuffled.Height}");
        TestContext.Out.WriteLine($"sorted:   leafFull={sorted.LeafFull} splits={sorted.Splits} spills={sorted.Spills} "
            + $"moved={sorted.EntriesMoved} parentWrites={sorted.ParentWrites} height={sorted.Height}");
        TestContext.Out.WriteLine($"ratio: moved {(double)shuffled.EntriesMoved / Math.Max(1, sorted.EntriesMoved):F2}x, "
            + $"parentWrites {(double)shuffled.ParentWrites / Math.Max(1, sorted.ParentWrites):F2}x");

        Assert.Multiple(() =>
        {
            // Same tree. This is the assertion that stops the cost claim being satisfied by an insert that lost entries.
            Assert.That(sorted.Entries, Is.EqualTo(shuffled.Entries), "both arms must hold the same number of entries");
            Assert.That(sorted.Content, Is.EqualTo(shuffled.Content), "and exactly the same keys, in the same leaf order");
            Assert.That(sorted.Height, Is.EqualTo(shuffled.Height), "and reach the same height");

            // Less work. A factor of two is well inside the measured ~7x and leaves room for the policy to be retuned without this going red for the
            // wrong reason.
            Assert.That(sorted.EntriesMoved * 2, Is.LessThan(shuffled.EntriesMoved),
                $"a sorted insert must move far fewer entries between leaves ({sorted.EntriesMoved} against {shuffled.EntriesMoved})");
            Assert.That(sorted.ParentWrites * 2, Is.LessThan(shuffled.ParentWrites),
                $"and write far fewer ancestors ({sorted.ParentWrites} against {shuffled.ParentWrites})");
        });
    }

    /// <summary>
    /// A sorted insert never spills RIGHT, and spills LEFT freely — which is the mechanism, and it is the opposite of what this case first asserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guess was that ascending keys land on the rightmost leaf whose left neighbour is full, so a left spill should be impossible. Measured, it is the
    /// right spill that is impossible and the left spill that does all the work: 102 left, 0 right, over 205 full leaves.
    /// </para>
    /// <para>
    /// <b>Both halves follow from the same fact and are worth stating.</b> The rightmost leaf has no right neighbour, so a right spill has nowhere to go —
    /// that is the zero. And its left neighbour is NOT full: the bulk-spill tail moves entries toward half capacity, so the leaf behind the cursor always
    /// has room, and pushing the lowest keys left is cheaper than splitting. That is why an ascending insert splits 106 times against the shuffled arm's
    /// 126 while moving a seventh of the entries — it is not avoiding structural change, it is always making the cheap version of it.
    /// </para>
    /// </remarks>
    [Test]
    public void AnAscendingInsertNeverSpillsLeft()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<IxCountData>();
        dbe.InitializeArchetypes();

        var meta = ArchetypeRegistry.GetMetadata<IxCountUnit>();
        var tree = (BTree<int, PersistentStore>)dbe._archetypeStates[meta.ArchetypeId].ClusterState.IndexSlots[0].Fields[0].Index;
        tree.ResetDiagnostics();

        using (EpochGuard.Enter(tree.Segment.Store.EpochManager))
        {
            var accessor = tree.Segment.CreateChunkAccessor(null);
            try
            {
                tree.AddBulkSorted(Batch(shuffled: false), ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        TestContext.Out.WriteLine($"ascending: leafFull={tree.LeafFullCount} splits={tree.SplitCount} "
            + $"spillL={tree.SpillLeftCount} spillR={tree.SpillRightCount} moved={tree.SpillEntriesMoved}");

        Assert.Multiple(() =>
        {
            Assert.That(tree.EntryCount, Is.EqualTo(BatchSize), "precondition: the batch went in");
            Assert.That(tree.LeafFullCount, Is.GreaterThan(0), "and filled leaves, or there is no structural work to characterise");
            Assert.That(tree.SpillRightCount, Is.Zero,
                "the rightmost leaf has no right neighbour, so an ascending insert can never spill right — a non-zero here means the fast path was missed");
            Assert.That(tree.SpillLeftCount, Is.GreaterThan(0),
                "and it spills LEFT instead, into the half-filled leaf the bulk-spill tail left behind the cursor — that is the cheap path, not a defect");
        });
    }
}
