using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ECS.PublishDurability.Item", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PdItem
{
    [Index] public long Key;
    public long Payload;
}

[Archetype]
class PdItemArch : Archetype<PdItemArch>
{
    public static readonly Comp<PdItem> Item = Register<PdItem>();
}

/// <summary>
/// Rule <b>AP-04</b> (<c>rules/durability.md</c>): every page a commit's publish writes is registered with the transaction's ChangeSet (#1159).
/// </summary>
/// <remarks>
/// <para>
/// A Versioned update publishes its committed value twice: into the revision chain, which point reads walk, and into the entity's cluster slot — the HEAD
/// cache that bulk iteration and Path-B scans read. The cluster write went through an accessor created without a ChangeSet, so marking the slot dirty only
/// toggled ActiveChunkWriters and the page's DirtyCounter never moved. Nothing ever wrote that page: not a checkpoint, not the close's dirty-page flush,
/// not the tick fence. After a clean close the cluster slot on disk held the old value, the reopen trusted it (CS-03), and Path B answered with the old
/// value while <c>Read()</c> answered with the new one.
/// </para>
/// <para>
/// The update must land on a page nothing else has dirtied, or another write carries it to disk and hides the defect — hence a spawn in one session and
/// the update in the next.
/// </para>
/// </remarks>
[TestFixture]
unsafe class VersionedPublishDurabilityTests : TestBase<VersionedPublishDurabilityTests>
{
    private const string Ap04Marker = "AP-04 violated: a published cluster page owes no write";

    internal static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PdItem>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static (int ClusterChunkId, int SlotIndex) Locate(DatabaseEngine dbe, EntityId id)
    {
        var meta = Archetype<PdItemArch>.Metadata;
        var es = dbe._archetypeStates[meta.ArchetypeId];
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var mapAccessor = es.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            var record = stackalloc byte[meta._entityRecordSize];
            Assert.That(es.EntityMap.TryGet(id.EntityKey, record, ref mapAccessor), Is.True);
            return (ClusterEntityRecordAccessor.GetClusterChunkId(record), ClusterEntityRecordAccessor.GetSlotIndex(record));
        }
        finally
        {
            mapAccessor.Dispose();
        }
    }

    /// <summary>The <c>Key</c> bytes in the entity's cluster slot — what Path B and bulk iteration read.</summary>
    internal static long ClusterKey(DatabaseEngine dbe, EntityId id)
    {
        var (chunk, slot) = Locate(dbe, id);
        var cs = dbe._archetypeStates[Archetype<PdItemArch>.Metadata.ArchetypeId].ClusterState;
        var itemSlot = cs.IndexSlots[0].Slot;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var b = accessor.GetChunkAddress(chunk);
            return *(long*)(b + cs.Layout.ComponentOffset(itemSlot) + slot * cs.Layout.ComponentSize(itemSlot) + cs.IndexSlots[0].Fields[0].FieldOffset);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>The shared assertion: the page holding the entity's cluster slot owes a write, so a checkpoint or the close will write it.</summary>
    private static void AssertClusterPageDirty(DatabaseEngine dbe, EntityId id)
    {
        var (chunk, _) = Locate(dbe, id);
        var cs = dbe._archetypeStates[Archetype<PdItemArch>.Metadata.ArchetypeId].ClusterState;
        var (pageIndex, _) = cs.ClusterSegment.GetChunkLocation(chunk);
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        dbe.MMF.RequestPageEpoch(cs.ClusterSegment.Pages[pageIndex], guard.Epoch, out var memPageIndex);
        // Writeback debt, not DirtyCounter: a ChangeSet's marks are released when the transaction disposes (#824), and the obligation to write the page is
        // carried by its writeback generation until a durable write discharges it.
        Assert.That(dbe.MMF.HasWritebackDebt(memPageIndex), Is.True,
            $"{Ap04Marker}: the cluster page holding entity {id}'s slot owes no write after its value was published. No checkpoint and no close will "
            + "write it, the cache may evict it and reload the old bytes, and a clean reopen trusts the slot on disk (CS-03, #1159).");
    }

    private EntityId SpawnAndClose(long key)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        var id = tx.Spawn<PdItemArch>(PdItemArch.Item.Set(new PdItem { Key = key, Payload = 7 }));
        tx.Commit();
        return id;
    }

    [Test]
    [VerifiesRule("AP-04")]
    public void AVersionedUpdate_OnACleanPage_IsWrittenAndSurvivesACleanReopen()
    {
        var id = SpawnAndClose(101);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenEngine(scope);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.OpenMut(id).Write(PdItemArch.Item).Key = 1200;
                tx.Commit();
            }

            AssertClusterPageDirty(dbe, id);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenEngine(scope);
            using var r = dbe.CreateReadOnlyTransaction();
            Assert.That(r.Open(id).Read(PdItemArch.Item).Key, Is.EqualTo(1200L), "the point read walks the chain");
            Assert.That(ClusterKey(dbe, id), Is.EqualTo(1200L),
                "the cluster slot on disk must hold the committed value: a clean reopen trusts it instead of rebuilding it from the chain (CS-03)");

            QueryPathProbe.Forced = ClusterScanPath.FullScan;
            try
            {
                var found = r.Query<PdItemArch>().WhereField<PdItem>(x => x.Key == 1200).Execute();
                Assert.That(found.Count == 1 && found.Contains(id), Is.True, "Path B reads the cluster slot and must find the entity by its new key");
            }
            finally
            {
                QueryPathProbe.Forced = ClusterScanPath.Planner;
            }
        }
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: publishes the new value the way the pre-fix commit effectively did — a write that leaves the page
    /// owing nothing — and requires the verifier's own assertion to reject it.
    /// </summary>
    [Test]
    [RuleMutant("AP-04")]
    public void APublishThroughAnAccessorWithoutAChangeSet_LeavesThePageClean()
    {
        var id = SpawnAndClose(101);

        RuleMutants.AssertDetects("AP-04", Ap04Marker, () =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = OpenEngine(scope);
            var (chunk, slot) = Locate(dbe, id);
            var cs = dbe._archetypeStates[Archetype<PdItemArch>.Metadata.ArchetypeId].ClusterState;
            var itemSlot = cs.IndexSlots[0].Slot;
            using (var guard = EpochGuard.Enter(dbe.EpochManager))
            {
                var accessor = cs.ClusterSegment.CreateChunkAccessor();
                try
                {
                    // The pre-fix publish exactly: a dirty write through an accessor that has no ChangeSet, which marks the slot and owes nothing.
                    var b = accessor.GetChunkAddress(chunk, true);
                    var field = b + cs.Layout.ComponentOffset(itemSlot) + slot * cs.Layout.ComponentSize(itemSlot) + cs.IndexSlots[0].Fields[0].FieldOffset;
                    *(long*)field = 1200;
                    accessor.CommitChanges();
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            AssertClusterPageDirty(dbe, id);
        });
    }
}
