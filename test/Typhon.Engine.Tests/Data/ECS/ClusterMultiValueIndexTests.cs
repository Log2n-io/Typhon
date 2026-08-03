using System.Collections.Generic;
using Typhon.Engine.Internals;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tick-fence index maintenance for <c>AllowMultiple</c> fields on CLUSTER-BACKED (SingleVersion) archetypes — issue #659.
/// </summary>
/// <remarks>
/// <para>
/// A multi-value B+Tree leaf holds a VSBS <b>buffer id</b>, not an entity location. <c>ProcessClusterShadowEntries</c> called plain
/// <c>Move</c> / <c>Remove</c> unconditionally, which overwrites that buffer id with a raw ClusterLocation — so every entity sharing
/// the mutated key disappeared from the index at once. Its flat twin <c>ProcessShadowFieldEntries</c> always branched correctly.
/// </para>
/// <para>
/// The pre-existing coverage missed this because it asserted only that a <b>key</b> was present in the tree. The corruption is in the
/// key's <b>value</b>, so a key-presence check passes while every entity behind it is unreachable. These tests enumerate the key's
/// values and resolve each one to an entity, which is the only assertion level that observes the defect — see
/// <see cref="IndexedEntitiesAt"/> for why a <c>WhereField(...).Execute()</c> query does not.
/// </para>
/// <para>Reuses <c>TbSvData</c> / <c>TbSvArch</c> from <c>TickBoundaryIndexTests.cs</c> — SingleVersion with <c>[Index(AllowMultiple = true)] Category</c>.</para>
/// </remarks>
[TestFixture]
class ClusterMultiValueIndexTests : TestBase<ClusterMultiValueIndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TbSvData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Entities reachable THROUGH THE INDEX at <paramref name="category"/>, resolved the way a real index scan resolves them:
    /// enumerate the multi-value buffer at the key, then decode each ClusterLocation to an EntityId via the cluster's EntityIds array.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <c>WhereField(...).Execute()</c> query. At these entity counts the planner picks Path B (zone-map prune +
    /// direct SoA evaluation), which never touches the B+Tree — so a query-based assertion reports the component DATA and passes
    /// happily over a corrupted index. Only a direct enumeration proves the index itself is intact.
    /// </remarks>
    private static unsafe EntityId[] IndexedEntitiesAt(DatabaseEngine dbe, int category)
    {
        var meta = Archetype<TbSvArch>.Metadata;
        Assert.That(meta.HasClusterIndexes, Is.True, "fixture must be cluster-backed");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        ref var field = ref clusterState.IndexSlots[0].Fields[0];
        Assert.That(field.AllowMultiple, Is.True, "this fixture's indexed field must be AllowMultiple");

        var tree = (BTree<int, PersistentStore>)field.Index;
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        var layout = clusterState.Layout;
        var found = new List<EntityId>();

        try
        {
            var e = tree.EnumerateRangeMultiple(category, category);
            try
            {
                while (e.MoveNextKey())
                {
                    do
                    {
                        var values = e.CurrentValues;
                        for (var i = 0; i < values.Length; i++)
                        {
                            var clusterLocation = values[i];
                            var clusterBase = clusterAccessor.GetChunkAddress(clusterLocation >> 6);
                            var raw = *(long*)(clusterBase + layout.EntityIdsOffset + (clusterLocation & 0x3F) * 8);
                            found.Add(EntityId.FromRaw(raw));
                        }
                    }
                    while (e.NextChunk());
                }
            }
            finally
            {
                e.Dispose();
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return found.ToArray();
    }

    [Test]
    public void SvMultiValueMutation_AtFence_SiblingAtSameKeySurvives()
    {
        using var dbe = SetupEngine();
        Assert.That(Archetype<TbSvArch>.Metadata.HasClusterIndexes, Is.True, "fixture must be cluster-backed");

        EntityId moved, sibling;
        using (var tx = dbe.CreateQuickTransaction())
        {
            moved = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 1)));
            sibling = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 2)));   // same Category — shares the multi-value buffer
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(IndexedEntitiesAt(dbe, 10), Is.EquivalentTo(new[] { moved, sibling }), "precondition: both entities are indexed at Category 10");

        // In-place SV mutation — index maintenance is deferred to the fence.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(moved).Write(TbSvArch.Data) = new TbSvData(20, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexedEntitiesAt(dbe, 10), Is.EquivalentTo(new[] { sibling }),
            "the sibling must still be reachable at Category 10 — a plain Move clobbers the shared multi-value buffer and loses it");
        Assert.That(IndexedEntitiesAt(dbe, 20), Is.EquivalentTo(new[] { moved }), "the mutated entity must be reachable at its new key");
    }

    [Test]
    public void SvMultiValueShadowedDestroy_AtFence_SiblingAtSameKeySurvives()
    {
        using var dbe = SetupEngine();

        EntityId doomed, sibling;
        using (var tx = dbe.CreateQuickTransaction())
        {
            doomed = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 1)));
            sibling = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 2)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate THEN destroy in the same tick: the entity is shadowed, so FlushPendingDestroys defers its index removal to the
        // fence drain — which is the branch that used Remove(key) and took the sibling with it.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(doomed).Write(TbSvArch.Data) = new TbSvData(10, 99);
            tx.Destroy(doomed);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexedEntitiesAt(dbe, 10), Is.EquivalentTo(new[] { sibling }),
            "destroying one entity must remove only its own element from the shared multi-value buffer");
    }

    [Test]
    public void SvMultiValueMutation_AtFence_RepeatedMovesKeepBufferIntact()
    {
        using var dbe = SetupEngine();

        EntityId a, b, c;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 1)));
            b = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 2)));
            c = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 3)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Move a, then b, across separate ticks — the second move must find a still-valid buffer and a correct element id,
        // which only holds if the first move wrote its new element id back to the cluster tail.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(a).Write(TbSvArch.Data) = new TbSvData(20, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(b).Write(TbSvArch.Data) = new TbSvData(20, 2);
            tx.Commit();
        }
        dbe.WriteTickFence(3);

        Assert.That(IndexedEntitiesAt(dbe, 10), Is.EquivalentTo(new[] { c }));
        Assert.That(IndexedEntitiesAt(dbe, 20), Is.EquivalentTo(new[] { a, b }));
    }
}
