using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Segment LIFETIME across a schema migration (review M9). A migration replaces an archetype's cluster, its EntityMap, and — when a component gains an
// index — its index segment. Every replacement used to abandon the old one: occupancy bits still set, nothing claiming them, so the pages never came
// back and the next open reported PopcountOrphan. Measured before the fix: 52 leaked pages for a one-field migration of two archetypes.
//
// The assertion that matters is RunStorageIntegrityCheck().IsHealthy at TWO points, and the second one is the whole reason this fixture exists:
//
//   * the MIGRATING open  — catches the EntityMap and (for a non-SingleVersion archetype) the cluster, both abandoned without ever being loaded;
//   * the open AFTER it   — catches the SingleVersion pre-migration cluster, which is LOADED during the migrating open to copy bytes out of, and loading
//                           registers it. A registered segment claims its pages, so the leak is invisible in the session that creates it and only surfaces
//                           one open later, when nothing loads it any more. A fixture that stopped at the migrating open would report health for the exact
//                           case M9 is about.
//
// Every test also reads its seeded entity back. "Healthy" is trivially satisfiable by an engine that dropped everything on the floor.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region SingleVersion — the cluster is loaded for the byte copy, so the leak hides until the next open

[Component("Typhon.Schema.UnitTest.MsrSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MsrSvV1
{
    public int A;
    public float B;
    public MsrSvV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.MsrSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MsrSvV2
{
    public int A;
    public float B;
    public long C;
    public MsrSvV2(int a, float b, long c) { A = a; B = b; C = c; }
}

[Archetype]
class MsrSvArch : Archetype<MsrSvArch>
{
    public static readonly Comp<MsrSvV1> Comp = Register<MsrSvV1>();
}

[Archetype]
class MsrSvV2Arch : Archetype<MsrSvV2Arch>
{
    public static readonly Comp<MsrSvV2> Comp = Register<MsrSvV2>();
}

#endregion

#region Pure-Versioned — nothing needs the old cluster, so nothing ever loaded it, so nothing could free it

[Component("Typhon.Schema.UnitTest.MsrVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MsrVerV1
{
    public int A;
    public int Pad;
    public MsrVerV1(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MsrVer", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct MsrVerV2
{
    public int A;
    public long B;
    public MsrVerV2(int a, long b) { A = a; B = b; }
}

[Archetype]
class MsrVerArch : Archetype<MsrVerArch>
{
    public static readonly Comp<MsrVerV1> Comp = Register<MsrVerV1>();
}

[Archetype]
class MsrVerV2Arch : Archetype<MsrVerV2Arch>
{
    public static readonly Comp<MsrVerV2> Comp = Register<MsrVerV2>();
}

#endregion

#region A component that gains a SECOND index — V1 must already be indexed, or there is no index segment to replace

[Component("Typhon.Schema.UnitTest.MsrIdx", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MsrIdxV1
{
    [Index]
    public int Key;
    public int Pad;
    public MsrIdxV1(int key) { Key = key; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MsrIdx", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MsrIdxV2
{
    [Index]
    public int Key;
    public int Pad;
    [Index]
    public long Rank;
    public MsrIdxV2(int key, long rank) { Key = key; Pad = 0; Rank = rank; }
}

[Archetype]
class MsrIdxArch : Archetype<MsrIdxArch>
{
    public static readonly Comp<MsrIdxV1> Comp = Register<MsrIdxV1>();
}

[Archetype]
class MsrIdxV2Arch : Archetype<MsrIdxV2Arch>
{
    public static readonly Comp<MsrIdxV2> Comp = Register<MsrIdxV2>();
}

#endregion

#region A component that gains an index and NOTHING else — same fields, same size, so no field migration runs

[Component("Typhon.Schema.UnitTest.MsrIdxOnly", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MsrIdxOnlyV1
{
    [Index]
    public int Key;
    public int Pad;
    public long Rank;
    public MsrIdxOnlyV1(int key, long rank) { Key = key; Pad = 0; Rank = rank; }
}

[Component("Typhon.Schema.UnitTest.MsrIdxOnly", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MsrIdxOnlyV2
{
    [Index]
    public int Key;
    public int Pad;
    [Index]
    public long Rank;
    public MsrIdxOnlyV2(int key, long rank) { Key = key; Pad = 0; Rank = rank; }
}

[Archetype]
class MsrIdxOnlyArch : Archetype<MsrIdxOnlyArch>
{
    public static readonly Comp<MsrIdxOnlyV1> Comp = Register<MsrIdxOnlyV1>();
}

[Archetype]
class MsrIdxOnlyV2Arch : Archetype<MsrIdxOnlyV2Arch>
{
    public static readonly Comp<MsrIdxOnlyV2> Comp = Register<MsrIdxOnlyV2>();
}

#endregion

/// <summary>
/// Every segment a schema migration replaces must have its pages returned to the occupancy map, in the migrating open and in the one after it (review M9).
/// </summary>
/// <remarks>
/// <see cref="NonParallelizableAttribute"/> because each test registers components into the process-global archetype registry, and concurrent registration
/// is a known flake source.
/// </remarks>
[NonParallelizable]
class MigrationSegmentReclaimTests : TestBase<MigrationSegmentReclaimTests>
{
    static void AssertNoLeakedPages(DatabaseEngine dbe, string when)
    {
        var report = dbe.RunStorageIntegrityCheck();
        var detail = new System.Text.StringBuilder();
        foreach (var issue in report.Issues)
        {
            detail.Append(detail.Length == 0 ? "" : " | ").Append(issue.Kind).Append(": ").Append(issue.Detail);
        }

        Assert.That(report.IsHealthy, Is.True,
            $"{when}: storage integrity must be clean — {report.OrphanPageCount} orphan page(s), {report.PhantomPageCount} phantom. {detail}");
    }

    [Test]
    public void SingleVersionMigration_ReclaimsOldClusterAndEntityMap()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrSvV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<MsrSvArch>(MsrSvArch.Comp.Set(new MsrSvV1(0x5EED, 2.5f)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrSvV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(id).Read(MsrSvV2Arch.Comp).A, Is.EqualTo(0x5EED), "the SingleVersion bytes must still cross the re-cluster");
            AssertNoLeakedPages(dbe, "migrating open");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrSvV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(id).Read(MsrSvV2Arch.Comp).A, Is.EqualTo(0x5EED));
            // The one that used to fail. The pre-migration cluster was registered by the open above, so it claimed its pages there; here nothing loads it.
            AssertNoLeakedPages(dbe, "open after the migration");
        }
    }

    [Test]
    public void VersionedMigration_ReclaimsOldClusterAndEntityMap()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrVerV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<MsrVerArch>(MsrVerArch.Comp.Set(new MsrVerV1(77)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrVerV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(id).Read(MsrVerV2Arch.Comp).A, Is.EqualTo(77), "the Versioned value must be refilled from its chain at the new slot");
            // No SingleVersion slot, so CapturePreMigrationCluster has nothing to copy and never loaded the old cluster — it leaked here, not one open later.
            AssertNoLeakedPages(dbe, "migrating open");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrVerV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(id).Read(MsrVerV2Arch.Comp).A, Is.EqualTo(77));
            AssertNoLeakedPages(dbe, "open after the migration");
        }
    }

    /// <summary>
    /// A field change AND an index addition together. The field change forces a fresh EntityMap, and <c>isFreshAllocation</c> then suppresses the read of the
    /// persisted index SPI entirely — so the migrating open could not see the index segment it was walking away from, let alone free it.
    /// </summary>
    [Test]
    public void AddingASecondIndexDuringAFieldMigration_ReclaimsTheReplacedIndexSegment()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrIdxV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<MsrIdxArch>(MsrIdxArch.Comp.Set(new MsrIdxV1(4242)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrIdxV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(id).Read(MsrIdxV2Arch.Comp).Key, Is.EqualTo(4242), "the indexed value itself must survive the migration");

            // Both trees must ANSWER, not merely exist — freeing the old segment must not disturb the fresh one built beside it. Queried through MsrIdxArch,
            // the archetype the entity was spawned into; MsrIdxV2Arch exists only to carry the V2 component accessor and holds no entities.
            Assert.That(t.Query<MsrIdxArch>().WhereField<MsrIdxV2>(c => c.Key == 4242).Execute(), Has.Count.EqualTo(1),
                "the pre-existing index must be repopulated after the re-cluster");
            Assert.That(t.Query<MsrIdxArch>().WhereField<MsrIdxV2>(c => c.Rank == 0).Execute(), Has.Count.EqualTo(1),
                "the index ADDED by the migration must be populated from cluster data");

            AssertNoLeakedPages(dbe, "migrating open");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrIdxV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Query<MsrIdxArch>().WhereField<MsrIdxV2>(c => c.Key == 4242).Execute(), Has.Count.EqualTo(1));
            AssertNoLeakedPages(dbe, "open after the migration");
        }
    }

    /// <summary>
    /// An index addition with no field change at all — same fields, same size, so nothing migrates and the persisted index SPI IS read. This is the
    /// <c>hasNewIndex</c> path proper: it used to skip the load purely because the trees could not be trusted, and dropped the segment with them.
    /// </summary>
    [Test]
    public void AddingAnIndexWithNoFieldChange_RecyclesTheIndexSegment()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrIdxOnlyV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<MsrIdxOnlyArch>(MsrIdxOnlyArch.Comp.Set(new MsrIdxOnlyV1(7, 900)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrIdxOnlyV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(id).Read(MsrIdxOnlyV2Arch.Comp).Rank, Is.EqualTo(900L), "no field changed, so nothing may move");

            // The segment is now RECYCLED rather than replaced: loaded, cleared by ClearSharedSegment, and rebuilt. Both assertions below would still pass over
            // a freshly allocated segment — what they rule out is the clear-and-rebuild leaving a stale directory entry or a double-inserted key behind.
            Assert.That(t.Query<MsrIdxOnlyArch>().WhereField<MsrIdxOnlyV2>(c => c.Key == 7).Execute(), Has.Count.EqualTo(1),
                "the pre-existing index must answer over the recycled segment");
            Assert.That(t.Query<MsrIdxOnlyArch>().WhereField<MsrIdxOnlyV2>(c => c.Rank == 900).Execute(), Has.Count.EqualTo(1),
                "the newly declared index must be filled from cluster data");

            AssertNoLeakedPages(dbe, "index-addition open");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MsrIdxOnlyV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Query<MsrIdxOnlyArch>().WhereField<MsrIdxOnlyV2>(c => c.Rank == 900).Execute(), Has.Count.EqualTo(1),
                "the rebuilt tree must survive the reopen that loads it");
            AssertNoLeakedPages(dbe, "open after the index addition");
        }
    }
}
