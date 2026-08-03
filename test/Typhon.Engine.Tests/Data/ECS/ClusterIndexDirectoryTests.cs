using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Two SingleVersion components, each indexing its OWN first field. Field ids are per-component, so both indexed fields are FieldId 0 — the collision this
// fixture is about. SingleVersion makes the archetype cluster-eligible, which is what puts both components' indexes on ONE shared segment.
[Component("Typhon.Test.ECS.DirKey.Alpha", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DirKeyAlpha
{
    [Index] public int Code;
    public int Payload;

    public DirKeyAlpha(int code, int payload)
    {
        Code = code;
        Payload = payload;
    }
}

[Component("Typhon.Test.ECS.DirKey.Beta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DirKeyBeta
{
    [Index] public int Code;
    public int Payload;

    public DirKeyBeta(int code, int payload)
    {
        Code = code;
        Payload = payload;
    }
}

[Archetype]
class DirKeyArch : Archetype<DirKeyArch>
{
    public static readonly Comp<DirKeyAlpha> Alpha = Register<DirKeyAlpha>();
    public static readonly Comp<DirKeyBeta> Beta = Register<DirKeyBeta>();
}

/// <summary>
/// Per-archetype index segments are shared by every component slot, so a tree's directory key must include the slot — issue #657.
/// </summary>
/// <remarks>
/// <para>
/// Each B+Tree on a shared segment owns one chunk-0 directory entry recording its root chunk and count, and finds that entry again on reopen by key. The key
/// was the field id alone. Field ids restart at 0 for every component, so <c>DirKeyAlpha.Code</c> and <c>DirKeyBeta.Code</c> both registered under 0.
/// </para>
/// <para>
/// Creation still worked — each tree instance caches the offset of the entry it appended — so the damage is invisible until the database is REOPENED. Then
/// <c>FindInDirectory</c> hands both trees the first matching entry, they share one root, and one component's index silently answers with the other's
/// entities. The same-session test below passes before and after the fix; only the reopen test moves.
/// </para>
/// </remarks>
[TestFixture]
class ClusterIndexDirectoryTests : TestBase<ClusterIndexDirectoryTests>
{
    private const int Count = 30;

    private const int AlphaBase = 1000;
    private const int BetaBase = 2000;

    private static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DirKeyAlpha>();
        dbe.RegisterComponentFromAccessor<DirKeyBeta>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Resolves the entity indexed at <paramref name="code"/> in the index slot at <paramref name="indexSlotIndex"/>, or null when the key is absent.
    /// </summary>
    /// <remarks>
    /// Reads the B+Tree directly rather than issuing a <c>WhereField</c> query: at these entity counts the planner takes Path B (zone-map prune + direct SoA
    /// evaluation) and never touches the tree, so a query would report the component DATA and pass over a cross-wired index.
    /// </remarks>
    private static unsafe EntityId? IndexedEntity(DatabaseEngine dbe, int indexSlotIndex, int code)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var clusterState = dbe._archetypeStates[Archetype<DirKeyArch>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref clusterState.IndexSlots[indexSlotIndex].Fields[0];

        var accessor = field.Index.Segment.CreateChunkAccessor();
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            var result = field.Index.TryGet(&code, ref accessor);
            if (!result.IsSuccess)
            {
                return null;
            }

            var clusterLocation = result.Value;
            var clusterBase = clusterAccessor.GetChunkAddress(clusterLocation >> 6);
            var raw = *(long*)(clusterBase + clusterState.Layout.EntityIdsOffset + (clusterLocation & 0x3F) * 8);
            return EntityId.FromRaw(raw);
        }
        finally
        {
            clusterAccessor.Dispose();
            accessor.Dispose();
        }
    }

    /// <summary>Spawns <see cref="Count"/> entities with disjoint Alpha / Beta key ranges and closes the engine cleanly.</summary>
    private EntityId[] WriteSession()
    {
        var ids = new EntityId[Count];

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Count; i++)
            {
                ids[i] = tx.Spawn<DirKeyArch>(
                    DirKeyArch.Alpha.Set(new DirKeyAlpha(AlphaBase + i, i)),
                    DirKeyArch.Beta.Set(new DirKeyBeta(BetaBase + i, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        return ids;
    }

    /// <summary>Both components' indexes must land on the shared per-archetype segment — otherwise nothing below is testing #657.</summary>
    [Test]
    public void BothComponentSlots_ShareOneIndexSegment()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        Assert.That(Archetype<DirKeyArch>.Metadata.HasClusterIndexes, Is.True, "fixture must be cluster-backed");

        var clusterState = dbe._archetypeStates[Archetype<DirKeyArch>.Metadata.ArchetypeId].ClusterState;
        Assert.That(clusterState.IndexSlots.Length, Is.EqualTo(2), "both components contribute an index slot");
        Assert.That(clusterState.IndexSlots[0].Slot, Is.Not.EqualTo(clusterState.IndexSlots[1].Slot), "the two index slots must be distinct component slots");
        Assert.That(ReferenceEquals(clusterState.IndexSlots[0].Fields[0].Index.Segment, clusterState.IndexSlots[1].Fields[0].Index.Segment), Is.True,
            "both trees must share one segment — that shared chunk-0 directory is what the slot in the key disambiguates");
    }

    /// <summary>
    /// The in-session case. Each tree caches the offset of the entry it appended, so this passes with or without the fix — it is here to pin that the defect
    /// is specifically in directory LOOKUP, not registration.
    /// </summary>
    [Test]
    public void SameSession_EachSlotResolvesItsOwnKeys()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        var ids = new EntityId[Count];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Count; i++)
            {
                ids[i] = tx.Spawn<DirKeyArch>(
                    DirKeyArch.Alpha.Set(new DirKeyAlpha(AlphaBase + i, i)),
                    DirKeyArch.Beta.Set(new DirKeyBeta(BetaBase + i, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        for (var i = 0; i < Count; i++)
        {
            Assert.That(IndexedEntity(dbe, 0, AlphaBase + i), Is.EqualTo(ids[i]), $"Alpha key {AlphaBase + i}");
            Assert.That(IndexedEntity(dbe, 1, BetaBase + i), Is.EqualTo(ids[i]), $"Beta key {BetaBase + i}");
        }
    }

    /// <summary>
    /// The #657 repro. After a reopen each slot's tree must reload ITS OWN root. Before the fix both slots resolve through whichever tree registered first,
    /// so slot 1 answers Alpha's keys and knows nothing of Beta's.
    /// </summary>
    [Test]
    public void Reopen_EachSlotResolvesItsOwnKeys()
    {
        var ids = WriteSession();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        // Guards the test's premise: if the indexes were rebuilt from a cluster scan instead of loaded, FindInDirectory never ran and this proves nothing.
        Assert.That(dbe.LastOpenClusterIndexRebuildCount, Is.EqualTo(0), "the reopen must LOAD the persisted index directory, not rebuild the trees");

        for (var i = 0; i < Count; i++)
        {
            Assert.That(IndexedEntity(dbe, 0, AlphaBase + i), Is.EqualTo(ids[i]), $"Alpha key {AlphaBase + i} must resolve after reopen");
            Assert.That(IndexedEntity(dbe, 1, BetaBase + i), Is.EqualTo(ids[i]), $"Beta key {BetaBase + i} must resolve after reopen");
        }

        // The sharp edge: each tree must be blind to the other's key range. A shared root makes both lookups succeed on Alpha's keys and fail on Beta's.
        Assert.That(IndexedEntity(dbe, 1, AlphaBase), Is.Null, "slot 1's tree must not resolve slot 0's key");
        Assert.That(IndexedEntity(dbe, 0, BetaBase), Is.Null, "slot 0's tree must not resolve slot 1's key");
    }
}
