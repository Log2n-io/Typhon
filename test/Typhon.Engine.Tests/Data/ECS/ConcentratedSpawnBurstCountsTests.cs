using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>
/// A mob as the demo indexes one: by the cell it stands in — many rows per key, so the index stores one leaf entry per CELL — and by a per-spawn unique key,
/// which is the shape that actually makes leaves split.
/// </summary>
/// <remarks>
/// Both fields live on one component on purpose: a single burst then produces BOTH numbers from the SAME inserts, so the two shapes cannot be compared across
/// runs that differed in anything else. <c>CellId</c> is the realistic SWG index; <c>SpawnKey</c> is the pessimistic one.
/// </remarks>
[Component("Typhon.Test.Burst.Mob", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct BurstMobData
{
    /// <summary>The cell the mob spawned in. Few distinct values in a concentrated burst, hence <c>AllowMultiple</c>.</summary>
    [Index(AllowMultiple = true)] public int CellId;

    /// <summary>One value per spawn, inside a narrow range — the key shape that fills a leaf and keeps filling the same one.</summary>
    [Index] public int SpawnKey;

    public int Payload;

    public BurstMobData(int cellId, int spawnKey, int payload)
    {
        CellId = cellId;
        SpawnKey = spawnKey;
        Payload = payload;
    }
}

[Archetype]
class BurstMob : Archetype<BurstMob>
{
    public static readonly Comp<BurstMobData> Data = Register<BurstMobData>();
}

#endregion

/// <summary>
/// #1098 — what a CONCENTRATED spawn burst actually costs the two structures a spawn inserts into: the secondary-index B+Tree and the EntityMap. Counted, not
/// timed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> #1097 designs a parallel apply for deferred entity commands, partitioned by destination cell, on the premise that disjoint
/// partitions touch disjoint structure. An insert breaks that premise two ways, and only one of them was ever counted: a node SPLIT propagates to a shared
/// parent (<c>BTree.SplitCount</c> saw it), and a SPILL bulk-moves up to half a leaf into its chain NEIGHBOUR and rewrites that neighbour's ancestor — across
/// a partition boundary, and nothing counted it at all. The spill counters added with this fixture are what make the second one visible.
/// </para>
/// <para>
/// <b>Why concentrated and not scattered.</b> The realistic worst case is one area-of-effect kill taking ~3 000 mobs in a small area, so ~3 000 spawns into a
/// handful of cells. A uniform scatter spreads the splits over many leaves and would give a number that is true and useless.
/// </para>
/// <para>
/// <b>What a reader should take from the output.</b> <c>SpillEntriesMoved / batch</c> is the ratio that decides whether a bulk-load-shaped applier is required:
/// a cost proportional to the number of structural events is something a pre-split pass can hoist out, a cost proportional to the batch is not. Parent writes
/// are <c>SplitCount + SpillLeftCount + SpillRightCount</c> — every one of the three outcomes of a full leaf rewrites a separator in an ancestor.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ConcentratedSpawnBurstCountsTests : TestBase<ConcentratedSpawnBurstCountsTests>
{
    /// <summary>How many cells the burst lands in. Four is a small area at the demo's 64 m cell size, not a single degenerate cell.</summary>
    private const int CellCount = 4;

    private readonly struct Counts
    {
        public readonly int Batch;
        public readonly bool Ascending;

        // Per indexed field: [0] = CellId (AllowMultiple, few keys), [1] = SpawnKey (unique).
        public readonly long[] LeafFull;
        public readonly long[] Splits;
        public readonly long[] SpillLeft;
        public readonly long[] SpillRight;
        public readonly long[] SpillMoved;
        public readonly long[] Entries;
        public readonly int[] Height;

        public readonly long MapSplits;
        public readonly long MapOverflowChained;
        public readonly long MapEntriesRehashed;
        public readonly int MapBuckets;
        public readonly int MapDistinctBucketsTouched;

        public Counts(int batch, bool ascending, long[] leafFull, long[] splits, long[] spillLeft, long[] spillRight, long[] spillMoved, long[] entries,
            int[] height, long mapSplits, long mapOverflowChained, long mapEntriesRehashed, int mapBuckets, int mapDistinctBucketsTouched)
        {
            Batch = batch;
            Ascending = ascending;
            LeafFull = leafFull;
            Splits = splits;
            SpillLeft = spillLeft;
            SpillRight = spillRight;
            SpillMoved = spillMoved;
            Entries = entries;
            Height = height;
            MapSplits = mapSplits;
            MapOverflowChained = mapOverflowChained;
            MapEntriesRehashed = mapEntriesRehashed;
            MapBuckets = mapBuckets;
            MapDistinctBucketsTouched = mapDistinctBucketsTouched;
        }
    }

    /// <summary>
    /// Spawns <paramref name="batch"/> mobs concentrated into <see cref="CellCount"/> cells and returns what the insert paths did. Counters are cleared AFTER
    /// archetype initialisation, so nothing the engine inserted while opening is attributed to the burst.
    /// </summary>
    private Counts Measure(int batch, bool ascending)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<BurstMobData>();
        dbe.InitializeArchetypes();

        var meta = ArchetypeRegistry.GetMetadata<BurstMob>();
        var state = dbe._archetypeStates[meta.ArchetypeId];
        var cluster = state.ClusterState;
        var map = state.EntityMap;

        var cellTree = (BTree<int, PersistentStore>)cluster.IndexSlots[0].Fields[0].Index;
        var keyTree = (BTree<int, PersistentStore>)cluster.IndexSlots[0].Fields[1].Index;

        cellTree.ResetDiagnostics();
        keyTree.ResetDiagnostics();
        map.ResetDiagnostics();

        // The spawn keys. Ascending is the benign order — every insert lands on the rightmost-leaf fast path. Shuffled keys inside the SAME narrow range route
        // through the general descent and keep re-entering leaves that are already full, which is the order a real spawn burst has no reason to avoid.
        var keys = new int[batch];
        for (var i = 0; i < batch; i++)
        {
            keys[i] = i;
        }

        if (!ascending)
        {
            // Fixed seed: the point of the fixture is a number that can be compared across runs and quoted in a design doc.
            var rng = new Random(1098);
            for (var i = batch - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }
        }

        var spawned = new long[batch];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < batch; i++)
            {
                var d = new BurstMobData(keys[i] % CellCount, keys[i], i);
                spawned[i] = tx.Spawn<BurstMob>(BurstMob.Data.Set(in d)).EntityKey;
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);

        // Distinct EntityMap buckets the burst's keys resolve to — against LiveBucketCount, this is what says whether a bulk insert has runs to partition by.
        // Read AFTER the burst, because a split moves the bucket a key resolves to; this is the partitioning a bulk applier would face on the NEXT burst.
        var touched = new HashSet<int>();
        for (var i = 0; i < batch; i++)
        {
            touched.Add(map.BucketIndexOf(spawned[i]));
        }

        return new Counts(
            batch, ascending,
            [cellTree.LeafFullCount, keyTree.LeafFullCount],
            [cellTree.SplitCount, keyTree.SplitCount],
            [cellTree.SpillLeftCount, keyTree.SpillLeftCount],
            [cellTree.SpillRightCount, keyTree.SpillRightCount],
            [cellTree.SpillEntriesMoved, keyTree.SpillEntriesMoved],
            [cellTree.EntryCount, keyTree.EntryCount],
            [cellTree.Height, keyTree.Height],
            map._splitCount, map._overflowChunksChained, map._splitEntriesRehashed,
            map.LiveBucketCount, touched.Count);
    }

    private static void Report(Counts c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#1098 concentrated burst: {c.Batch} spawns into {CellCount} cells, spawn keys {(c.Ascending ? "ascending" : "shuffled")}");
        sb.AppendLine();
        sb.AppendLine("  index field        entries  height  leafFull  splits  spillL  spillR  entriesMoved  parentWrites  moved/batch");
        string[] names = ["CellId (multi)", "SpawnKey (uniq)"];
        for (var f = 0; f < 2; f++)
        {
            var parentWrites = c.Splits[f] + c.SpillLeft[f] + c.SpillRight[f];
            sb.AppendLine($"  {names[f],-17}  {c.Entries[f],7}  {c.Height[f],6}  {c.LeafFull[f],8}  {c.Splits[f],6}  {c.SpillLeft[f],6}  "
                + $"{c.SpillRight[f],6}  {c.SpillMoved[f],12}  {parentWrites,12}  {(double)c.SpillMoved[f] / c.Batch,11:F2}");
        }

        sb.AppendLine();
        sb.AppendLine($"  EntityMap: buckets={c.MapBuckets} distinctBucketsTouched={c.MapDistinctBucketsTouched} splits={c.MapSplits} "
            + $"overflowChunksChained={c.MapOverflowChained} entriesRehashed={c.MapEntriesRehashed} "
            + $"(rehashed/batch={(double)c.MapEntriesRehashed / c.Batch:F2})");
        TestContext.Out.WriteLine(sb.ToString());
    }

    /// <summary>
    /// The measurement. 100 / 1 000 / 3 000 are the sizes #1098 asks for: a pull of a few mobs, a large pull, and one AoE taking a spawn camp.
    /// </summary>
    /// <remarks>
    /// The assertions are deliberately structural rather than numeric — a threshold on a count would make this a regression test for the B+Tree's split policy,
    /// which is not what it is for. What they DO pin is that the measurement still measures something: a burst this concentrated must fill leaves, and the
    /// spill counters must stay consistent with the leaf-full count that gates them. If a future change makes <c>LeafFull</c> zero here, the numbers in the
    /// design doc describe a workload that no longer happens and the fixture says so instead of quietly reporting zeroes.
    /// </remarks>
    [TestCase(100, true), TestCase(100, false)]
    [TestCase(1_000, true), TestCase(1_000, false)]
    [TestCase(3_000, true), TestCase(3_000, false)]
    public void ConcentratedBurstStructuralCounts(int batch, bool ascending)
    {
        var c = Measure(batch, ascending);
        Report(c);

        Assert.Multiple(() =>
        {
            // The unique-key index must have filled leaves — otherwise the burst was not concentrated enough to measure anything.
            Assert.That(c.LeafFull[1], Is.GreaterThan(0),
                $"a {batch}-spawn burst over {batch} unique keys should have found a leaf full at least once; if it did not, this fixture is measuring a "
                + "workload that no longer stresses the insert path");

            // Spills are a SUBSET of full-leaf events: a full leaf spills left, spills right, or falls through to a split.
            Assert.That(c.SpillLeft[1] + c.SpillRight[1], Is.LessThanOrEqualTo(c.LeafFull[1]),
                "every spill is preceded by a full leaf, so spills can never outnumber full-leaf events");

            // A spill is never a no-op: if one was taken, entries moved.
            if (c.SpillLeft[1] + c.SpillRight[1] > 0)
            {
                Assert.That(c.SpillMoved[1], Is.GreaterThanOrEqualTo(c.SpillLeft[1] + c.SpillRight[1]),
                    "a spill moves at least the one entry that triggered it, and normally a bulk tail on top");
            }

            // The multi-value index holds one entry per CELL regardless of batch — the finding that makes the realistic demo index cheap.
            Assert.That(c.Entries[0], Is.EqualTo(CellCount),
                $"an AllowMultiple index over {CellCount} distinct cells holds {CellCount} leaf entries however many rows share them");

            // Every spawned entity got an EntityMap entry, and the burst's keys are spread over buckets rather than piled into one — monotonic keys hashed,
            // not used as an array index.
            Assert.That(c.MapDistinctBucketsTouched, Is.GreaterThan(0));
            Assert.That(c.MapDistinctBucketsTouched, Is.LessThanOrEqualTo(c.MapBuckets));
        });
    }
}
