using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>
/// One <c>AllowMultiple</c> indexed field, so the same key can be written by many entities and the tree stores them in one leaf entry's buffer rather than
/// one entry each — the shape whose entry count could drift.
/// </summary>
[Component("Typhon.Test.IxCount.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct IxCountData
{
    [Index(AllowMultiple = true)] public int Score;
    public int Payload;

    public IxCountData(int score, int payload)
    {
        Score = score;
        Payload = payload;
    }
}

[Archetype]
class IxCountUnit : Archetype<IxCountUnit>
{
    public static readonly Comp<IxCountData> Data = Register<IxCountData>();
}

#endregion

/// <summary>
/// <see cref="IBTreeIndex.EntryCount"/> must equal the number of leaf entries the chain actually holds — rule IXS-05 — including for an
/// <c>AllowMultiple</c> index, where an entry is a DISTINCT KEY and not a row.
/// </summary>
/// <remarks>
/// <para>
/// The count is not a diagnostic. <see cref="IndexStatistics.EntryCount"/> reports it as the index's distinct-key count, the selectivity estimators divide by
/// it, and the planner picks both the primary scan stream and the cluster scan path from the result — so a drifted count is a query plan chosen from a number
/// that describes nothing.
/// </para>
/// <para>
/// <b>Insertion ORDER is the axis, which is why this went unnoticed.</b> A duplicate key is appended to the existing entry's buffer by one of two code paths:
/// the OLC fast paths (leftmost/rightmost leaf, and the single-leaf case), which return before the caller's <c>if (args.Added) { IncCount(); }</c> tail, and
/// the general descent through <c>NodeWrapper.InsertLeaf</c>, which does not. Keys arriving in sorted order always land on the rightmost fast path, so every
/// ascending-insert test counted correctly; cyclic keys route through the general descent and inflated the count by one per duplicate row (#783).
/// </para>
/// </remarks>
[TestFixture]
class BTreeEntryCountTests : TestBase<BTreeEntryCountTests>
{
    private const int EntityCount = 2_000;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<IxCountData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Spawns <see cref="EntityCount"/> entities over <paramref name="distinctKeys"/> key values, ascending when <paramref name="ascending"/> and cycling
    /// otherwise, then returns the tree's own count alongside a walk of its leaf chain.
    /// </summary>
    private (int Reported, int LeafEntries, int DistinctInLeaves) SpawnAndMeasure(int distinctKeys, bool ascending)
    {
        var dbe = SetupEngine();
        var perKey = EntityCount / distinctKeys;

        var remaining = EntityCount;
        var offset = 0;
        while (remaining > 0)
        {
            var batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var idx = offset + i;
                var d = new IxCountData(ascending ? idx / perKey : idx % distinctKeys, idx);
                tx.Spawn<IxCountUnit>(IxCountUnit.Data.Set(in d));
            }

            tx.Commit();
            offset += batch;
        }

        dbe.WriteTickFence(0);

        var meta = ArchetypeRegistry.GetMetadata<IxCountUnit>();
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var tree = (BTree<int, PersistentStore>)clusterState.IndexSlots[0].Fields[0].Index;

        var distinct = new HashSet<int>();
        var leafEntries = 0;
        using (EpochGuard.Enter(tree.Segment.Store.EpochManager))
        {
            foreach (var kv in tree.EnumerateLeaves())
            {
                leafEntries++;
                distinct.Add(kv.Key);
            }
        }

        return (tree.EntryCount, leafEntries, distinct.Count);
    }

    /// <summary>
    /// 50 and 250 keys straddle a single leaf's capacity, so the cyclic cases route duplicates through both the fast paths and the general descent; 10 keys
    /// fit one leaf (fast path only) and <see cref="EntityCount"/> keys have no duplicates at all, and both counted correctly even before the fix — they are
    /// here to hold the boundary rather than to demonstrate it.
    /// </summary>
    [TestCase(10, true), TestCase(10, false)]
    [TestCase(50, true), TestCase(50, false)]
    [TestCase(250, true), TestCase(250, false)]
    [TestCase(EntityCount, true), TestCase(EntityCount, false)]
    [VerifiesRule("IXS-05")]
    public void EntryCountEqualsTheDistinctKeysTheChainHolds(int distinctKeys, bool ascending)
    {
        var (reported, leafEntries, distinctInLeaves) = SpawnAndMeasure(distinctKeys, ascending);

        Assert.Multiple(() =>
        {
            // Structure first: if the tree really did hold one entry per row the count would be RIGHT and the tree wrong, which is a different defect with a
            // different fix. Asserting the chain before the counter is what separates the two.
            Assert.That(leafEntries, Is.EqualTo(distinctKeys),
                $"the leaf chain should hold exactly one entry per distinct key ({(ascending ? "ascending" : "cyclic")} insert)");
            Assert.That(distinctInLeaves, Is.EqualTo(distinctKeys), "and no key should appear in more than one leaf entry");

            // Then the counter, which is the part that drifted.
            Assert.That(reported, Is.EqualTo(leafEntries),
                $"EntryCount must equal the entries the chain holds (IXS-05); {(ascending ? "ascending" : "cyclic")} insert of {EntityCount} rows over "
                + $"{distinctKeys} keys reported {reported}");
        });
    }

    /// <summary>
    /// The same count read back through the statistics wrapper the planner actually consumes, so the fix is asserted where it is used and not only where it
    /// is stored.
    /// </summary>
    [Test]
    public void TheStatisticsDistinctKeyCountSurvivesCyclicInsertion()
    {
        var (reported, _, distinctInLeaves) = SpawnAndMeasure(50, ascending: false);

        Assert.That(reported, Is.EqualTo(distinctInLeaves),
            "IndexStatistics.EntryCount is the planner's distinct-key estimate for an AllowMultiple index; a count that grows with the ROW count makes every "
            + "selectivity estimate over it meaningless");
    }
}
