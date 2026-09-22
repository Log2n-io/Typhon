using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// A component that mixes a MOVING field with a stable INDEXED one — the ordinary shape, and the one that makes the shadow drain expensive (#882).
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ShDrain.Unit", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ShDrainComp
{
    /// <summary>Indexed and non-unique. Written by only some of the mutations below, which is the point: the drain must reach the same answer whether an
    /// entry's key changed or not, and in this archetype most of them will not have.</summary>
    [Index(AllowMultiple = true)]
    public int Tag;

    /// <summary>Not indexed. Writing this alone still captures a shadow entry for <see cref="Tag"/>, because capture is per indexed FIELD of the written
    /// component's slot, not per changed field.</summary>
    public int Payload;
}

[Archetype]
partial class ShDrainUnit : Archetype<ShDrainUnit>
{
    public static readonly Comp<ShDrainComp> Comp = Register<ShDrainComp>();
}

/// <summary>
/// The tick fence replays parked index key-changes in ascending CLUSTER order rather than in the order user code wrote the entities (#882).
/// </summary>
/// <remarks>
/// <para><b>Why the order changed.</b> The drain resolves a chunk address per shadow entry, and a <c>ChunkAccessor</c>'s page window holds 32 pages against
/// an archetype that places one or two clusters per page. In append order — which is user-write order, arbitrary with respect to cluster id — a few thousand
/// dirty clusters miss that window on nearly every entry, and a miss is a dictionary lookup plus three interlocked read-modify-writes on shared
/// <c>PageInfo</c> cache lines. The drain measured <b>43 %</b> of the fence's Prep phase at the 25 % reference point of the #872 matrix, and almost none of
/// it was B+Tree work.</para>
/// <para><b>What this fixture has to prove.</b> Reordering a drain is only safe if nothing downstream depends on the sequence. No rule constrains it, view
/// deltas from the fence all carry <c>tsn = 0</c> and there is at most one entry per (entity, field) per tick — but "no rule says otherwise" is an argument,
/// not a test. These assert the OBSERVABLE result: after a tick, every entity is findable under its current key and under no other, across enough clusters
/// that the permutation is real.</para>
/// <para><b>Ablations</b>: making <c>BuildShadowDrainOrder</c> return the identity permutation still passes (the point is that behaviour is unchanged), but
/// returning a REVERSED permutation, or one that drops or duplicates an index, reddens every test here. The scatter's off-by-one — writing
/// <c>order[counts[bucket]]</c> without the post-increment — reddens <see cref="ShadowDrainOrderPermutationTests"/> immediately.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterShadowDrainOrderTests : TestBase<ClusterShadowDrainOrderTests>
{
    /// <summary>Enough entities to span many clusters, so the append order and the cluster order genuinely differ. Asserted, not assumed.</summary>
    private const int EntityCount = 1_500;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ShDrainComp>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ShDrainUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Spawn <see cref="EntityCount"/> entities, each with a distinct tag, and settle them with one fence.</summary>
    private static List<EntityId> SpawnAcrossManyClusters(DatabaseEngine dbe, long tick)
    {
        var ids = new List<EntityId>(EntityCount);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                ids.Add(tx.Spawn<ShDrainUnit>(ShDrainUnit.Comp.Set(new ShDrainComp { Tag = i, Payload = 0 })));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(tick);
        return ids;
    }

    /// <summary>A fixed-seed shuffle. The scramble is the experiment — writing in id order would leave append order and cluster order nearly identical and
    /// the fixture would pass without exercising anything.</summary>
    private static int[] ScrambledOrder(int count, int seed)
    {
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        var rng = new Random(seed);
        for (var i = count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    private static int CountWithTag(DatabaseEngine dbe, int tag)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<ShDrainUnit>().WhereField<ShDrainComp>(c => c.Tag == tag).Count();
    }

    /// <summary>
    /// Assert directly against the per-archetype B+Tree that <paramref name="present"/> are keys and <paramref name="absent"/> are not.
    /// </summary>
    /// <remarks>
    /// <b>Querying is not enough, and an ablation proved it.</b> The first version of this fixture asserted only through
    /// <c>Query(...).WhereField(...)</c>, and it stayed green under a deliberately broken drain permutation — one whose scatter dropped 31 of every 32
    /// entries. The planner had chosen a scan for this shape, so the assertions were reading the component column and the index could have said anything.
    /// A test of the drain has to read what the drain writes.
    /// </remarks>
    private static unsafe void AssertIndexKeys(DatabaseEngine dbe, IReadOnlyList<int> present, IReadOnlyList<int> absent)
    {
        var cs = ClusterStateOf(dbe);
        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < present.Count; i++)
            {
                var key = present[i];
                Assert.That(field.Index.TryGet(&key, ref accessor).IsSuccess, Is.True, $"key {key} must be IN the per-archetype B+Tree after the drain");
            }

            for (var i = 0; i < absent.Count; i++)
            {
                var key = absent[i];
                Assert.That(field.Index.TryGet(&key, ref accessor).IsSuccess, Is.False, $"key {key} must have been REMOVED from the B+Tree by the drain");
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public void TheWorkloadActuallySpansManyClusters()
    {
        // Guards every other test in this fixture against passing vacuously. A single-cluster world has exactly one drain order and proves nothing.
        using var dbe = SetupEngine();
        SpawnAcrossManyClusters(dbe, 1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(8),
            $"{EntityCount} entities must occupy enough clusters for append order and cluster order to differ");
    }

    [Test]
    public void EveryEntityIsFindableUnderItsCurrentKeyAcrossManyClusters()
    {
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);
        var order = ScrambledOrder(EntityCount, seed: 20250904);

        // Half the mutations change the indexed field; half write only the non-indexed one. Both capture a shadow entry — the second kind is the no-op the
        // drain spends its time on — and the tick has to end with the index describing exactly the first kind's new keys.
        var expected = new int[EntityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < EntityCount; k++)
            {
                var i = order[k];
                if ((i & 1) == 0)
                {
                    expected[i] = 1_000_000 + i;
                    tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = expected[i], Payload = i };
                }
                else
                {
                    expected[i] = i;
                    tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = i, Payload = i };
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // The index itself: every new key present, every vacated key gone. This is the assertion the drain can actually fail.
        var present = new List<int>(EntityCount);
        var absent = new List<int>(EntityCount / 2);
        for (var i = 0; i < EntityCount; i++)
        {
            present.Add(expected[i]);
            if ((i & 1) == 0)
            {
                absent.Add(i);   // moved off its original tag, which no other entity holds
            }
        }

        AssertIndexKeys(dbe, present, absent);

        // And the same conclusion through the query planner, whichever path it picks.
        using var tx2 = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var found = tx2.Query<ShDrainUnit>().WhereField<ShDrainComp>(c => c.Tag == expected[i]).Count();
            Assert.That(found, Is.EqualTo(1), $"entity {i} must be findable under its post-tick key {expected[i]}");
        }
    }

    /// <summary>
    /// The <c>AllowMultiple</c> path writes BACK into the cluster — <c>MoveValue</c> returns a new element id that the drain stores in the entity's elementId
    /// tail — so a mistake under reordering loses entities from the index rather than merely mis-sorting them. That is the shape of #659.
    /// </summary>
    /// <remarks>Stops one short of the whole population deliberately: collapsing EVERY entity onto one key leaves the tree with a single distinct key and
    /// crashes the query path, which is #884 and has nothing to do with the drain. <see cref="TheWholePopulationOnOneMultiValueKeyCrashesTheQueryPath"/>
    /// carries that arm.</remarks>
    [TestCase(64)]
    [TestCase(256)]
    [TestCase(512)]
    [TestCase(1024)]
    [TestCase(EntityCount - 1)]
    public void EntitiesSharingOneMultiValueKeySurviveTheReorder(int shareCount)
    {
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);
        var order = ScrambledOrder(EntityCount, seed: 7);

        const int SharedTag = 424_242;
        var moved = new HashSet<int>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < shareCount; k++)
            {
                var i = order[k];
                moved.Add(i);
                tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = SharedTag, Payload = i };
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.That(CountWithTag(dbe, SharedTag), Is.EqualTo(shareCount), "every entity collapsed onto one key must still be in the index under it");

        // A vacated key must be empty and a retained one must not — the two halves of "the drain moved exactly the entities it was asked to".
        Assert.That(CountWithTag(dbe, order[0]), Is.EqualTo(0), $"entity {order[0]} moved, so its original key must have been removed");

        var retained = -1;
        for (var i = 0; i < EntityCount; i++)
        {
            if (!moved.Contains(i))
            {
                retained = i;
                break;
            }
        }

        Assert.That(retained, Is.GreaterThanOrEqualTo(0), "the arm must leave at least one entity on its own key, or it is testing #884 instead");
        Assert.That(CountWithTag(dbe, retained), Is.EqualTo(1), $"entity {retained} did not move, so its key must be untouched");

        AssertIndexKeys(dbe, [SharedTag, retained], [order[0]]);
    }

    /// <summary>
    /// The arm the fixture could not carry: EVERY entity on one multi-value key, then a query for a key nobody holds. #884 — the process died, no
    /// exception, and the bisect said 1 499 was fine and 1 500 was not, so the trigger was a tree left holding exactly one distinct key.
    /// </summary>
    /// <remarks>
    /// Fixed by #887, whose defect (2) is the same shape: the pessimistic path believed a not-found answer from a descent that can land one leaf past the
    /// key. With one separator left, every vacated key routes to that single leaf, which is why the last entity to move is the one that turns a correct
    /// empty answer into a walk off the end of the buffer chain. Kept here, un-quarantined, because a test that once killed the shard is exactly the one
    /// worth running every time.
    /// </remarks>
    [Test]
    public void TheWholePopulationOnOneMultiValueKeyStillAnswersAVacatedKey()
    {
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);
        var order = ScrambledOrder(EntityCount, seed: 7);

        const int SharedTag = 424_242;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < EntityCount; k++)
            {
                tx.OpenMut(ids[order[k]]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = SharedTag, Payload = order[k] };
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.That(CountWithTag(dbe, SharedTag), Is.EqualTo(EntityCount), "every entity is on the shared key, so all of them must be findable under it");

        // The query that used to kill the process: a key every entity has left, against a tree holding one separator.
        Assert.That(CountWithTag(dbe, order[0]), Is.EqualTo(0), "a vacated key must answer empty, not crash");
        Assert.That(CountWithTag(dbe, order[EntityCount - 1]), Is.EqualTo(0), "the last key vacated is the one the bisect blamed");
    }

    [Test]
    public void AnEntityDestroyedAfterMutationLosesItsOldKey()
    {
        // The occupancy == 0 branch of the drain: the slot is gone but the shadow entry survives, and the old key must be removed with it. Reordering moves
        // that branch relative to the surviving entries, so it is asserted alongside them rather than alone.
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);
        var order = ScrambledOrder(EntityCount, seed: 99);

        var destroyed = new HashSet<int>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < EntityCount; k++)
            {
                var i = order[k];
                tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = 500_000 + i, Payload = i };

                // Mutate THEN destroy, in the same transaction — the case PrepareEcsDestroys does not cover and the drain has to.
                if (i % 5 == 0)
                {
                    destroyed.Add(i);
                    tx.Destroy(ids[i]);
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        using var tx2 = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var expectedCount = destroyed.Contains(i) ? 0 : 1;
            Assert.That(tx2.Query<ShDrainUnit>().WhereField<ShDrainComp>(c => c.Tag == 500_000 + i).Count(), Is.EqualTo(expectedCount),
                destroyed.Contains(i) ? $"destroyed entity {i} must be gone from the index" : $"surviving entity {i} must be findable");

            Assert.That(tx2.Query<ShDrainUnit>().WhereField<ShDrainComp>(c => c.Tag == i).Count(), Is.EqualTo(0), $"the pre-mutation key {i} must be gone");
        }

        var survivorKeys = new List<int>();
        var goneKeys = new List<int>();
        for (var i = 0; i < EntityCount; i++)
        {
            (destroyed.Contains(i) ? goneKeys : survivorKeys).Add(500_000 + i);
            goneKeys.Add(i);
        }

        AssertIndexKeys(dbe, survivorKeys, goneKeys);
    }

    [TestCase(1500)]
    [TestCase(750)]
    [TestCase(64)]
    [TestCase(3)]
    [TestCase(1)]
    public void ASmallMutationSetStillReachesTheIndex(int mutateCount)
    {
        // Isolating the RepeatedTicks failure: does a SUBSET mutation reach the B+Tree at all, on a single tick, with no reuse involved?
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);
        var order = ScrambledOrder(EntityCount, seed: 555);

        var newKeys = new List<int>(mutateCount);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < mutateCount; k++)
            {
                var i = order[k];
                newKeys.Add(900_000 + i);
                tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = 900_000 + i, Payload = i };
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);
        AssertIndexKeys(dbe, newKeys, []);
    }

    [Test]
    public void RepeatedTicksLeaveNoResidueInTheReusedOrderingBuffers()
    {
        // The permutation and the histogram are per-archetype and reused across ticks. A histogram not cleared after use, or an order array read past the
        // current tick's count, would corrupt a LATER tick — so one tick proves nothing and this drives several with different populations.
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);

        var counts = new[] { EntityCount, 3, EntityCount / 2, 1, EntityCount };
        var tick = 2L;
        var generation = 0;
        foreach (var mutateCount in counts)
        {
            generation++;
            var order = ScrambledOrder(EntityCount, seed: 1000 + generation);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < mutateCount; k++)
                {
                    var i = order[k];
                    tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = (generation * 100_000) + i, Payload = generation };
                }

                tx.Commit();
            }

            dbe.WriteTickFence(tick++);

            using (var tx2 = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < mutateCount; k++)
                {
                    var i = order[k];
                    Assert.That(tx2.Query<ShDrainUnit>().WhereField<ShDrainComp>(c => c.Tag == (generation * 100_000) + i).Count(), Is.EqualTo(1),
                        $"generation {generation}: entity {i} must be findable under its new key");
                }
            }

        }
    }

    /// <summary>
    /// <b>#885 (fixed):</b> two consecutive ticks that each mutate hundreds of indexed values must leave the B+Tree agreeing with the component data.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a #882 regression, and the drain order is irrelevant.</b> Forcing the drain back to append order — an identity permutation from
    /// <c>ArchetypeClusterState.BuildDrainOrder</c>, exactly the pre-#882 behaviour — fails identically, on a different key.</para>
    /// <para><b>Bisected on the generation sequence</b>, 1 500 entities: <c>[1500, 750]</c>, <c>[1500, 1500]</c> and <c>[750, 750]</c> all fail on the SECOND
    /// generation; <c>[1500, 3]</c> and <c>[3, 750]</c> pass. A single wave of any size passes — see
    /// <see cref="ASmallMutationSetStillReachesTheIndex"/>, which covers 1 through 1 500. So the trigger is two large waves in succession, not size and not
    /// repetition.</para>
    /// <para>Query assertions do NOT catch it: the planner serves those from a scan, and the data is correct. Only a direct B+Tree probe sees it, which is
    /// why <see cref="AssertIndexKeys"/> exists.</para>
    /// </remarks>
    [Test]
    public void TwoLargeMutationWavesInSuccessionLeaveKeysOutOfTheIndex()
    {
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);

        var tick = 2L;
        for (var generation = 1; generation <= 2; generation++)
        {
            var order = ScrambledOrder(EntityCount, seed: 1000 + generation);
            var mutateCount = generation == 1 ? EntityCount : EntityCount / 2;

            var newKeys = new List<int>(mutateCount);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < mutateCount; k++)
                {
                    var i = order[k];
                    newKeys.Add((generation * 100_000) + i);
                    tx.OpenMut(ids[i]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = (generation * 100_000) + i, Payload = generation };
                }

                tx.Commit();
            }

            dbe.WriteTickFence(tick++);
            AssertIndexKeys(dbe, newKeys, []);

            // The stronger statement, and the one that actually caught the defect: every leaf entry names a slot that holds its key, and every entity is
            // reachable under the key it holds. AssertIndexKeys only asks whether a key is present.
            IndexDataOracle.AssertIndexAgreesWithData<ShDrainUnit>(dbe, $"after generation {generation}");
        }
    }

    /// <summary>
    /// The whole population collapsed onto ONE <c>AllowMultiple</c> key — the shape that grows a single VSBS buffer past its root chunk.
    /// </summary>
    /// <remarks>
    /// Capped below the population at which <b>#884</b> still kills the process. The buffer's root chunk holds 56 elements, so anything above that walks the
    /// chunk chain — which <c>IndexDataOracle</c> only does correctly since the <c>NextChunk()</c> fix that came out of this work. Before it, the oracle
    /// stopped at 56 and reported every entity past that as missing, which is what made #884 look like index loss rather than a chain-walk crash.
    /// </remarks>
    [TestCase(64)]
    [TestCase(256)]
    [TestCase(512)]
    [TestCase(768)]
    [TestCase(1024)]
    [TestCase(EntityCount)]
    public void TheWholePopulationOnOneMultiValueKeyKeepsTheIndexAgreeingWithTheData(int shareCount)
    {
        using var dbe = SetupEngine();
        var ids = SpawnAcrossManyClusters(dbe, 1);
        var order = ScrambledOrder(EntityCount, seed: 7);

        const int SharedTag = 424_242;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < shareCount; k++)
            {
                tx.OpenMut(ids[order[k]]).Write(ShDrainUnit.Comp) = new ShDrainComp { Tag = SharedTag, Payload = order[k] };
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.That(CountWithTag(dbe, SharedTag), Is.EqualTo(shareCount), "every entity collapsed onto the shared key is findable under it");

        // NOT asserted here: querying a key every entity has LEFT. That is #884's exact trigger and it still KILLS THE PROCESS — no exception, no
        // assertion, the host simply dies — so it cannot be carried even under Quarantine, because a crash takes its whole nightly shard's results rather
        // than reporting one failure. #885's fix did not touch it; verified by adding the call back and watching it crash on every run.

        IndexDataOracle.AssertIndexAgreesWithData<ShDrainUnit>(dbe, "after collapsing onto one key");
    }
}
