using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>SingleVersion — makes the archetype cluster-backed. Carries no index; its only job is eligibility.</summary>
[Component("Typhon.Test.MultiV.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MvPos
{
    public float X;
    public float Y;

    public MvPos(float x, float y) { X = x; Y = y; }
}

/// <summary>Versioned, 8 bytes.</summary>
[Component("Typhon.Test.MultiV.Small", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MvSmall
{
    [Index(AllowMultiple = true)] public int Tag;
    public int Payload;

    public MvSmall(int tag, int payload) { Tag = tag; Payload = payload; }
}

/// <summary>
/// Versioned, <b>16</b> bytes — deliberately a different size from <see cref="MvSmall"/>.
/// </summary>
/// <remarks>
/// The size mismatch is load-bearing. Two same-sized components share a chunk stride, so a lookup sent to the wrong segment lands IN RANGE and silently
/// returns the wrong bytes; differing sizes make the same mistake overrun and throw. A fixture built from uniform components reports success either way.
/// </remarks>
[Component("Typhon.Test.MultiV.Large", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MvLarge
{
    [Index(AllowMultiple = true)] public int Tag;
    public int Payload;
    public long Extra;

    public MvLarge(int tag, int payload, long extra) { Tag = tag; Payload = payload; Extra = extra; }
}

/// <summary>Cluster-backed archetype carrying TWO Versioned components of different sizes.</summary>
[Archetype]
class MvArch : Archetype<MvArch>
{
    public static readonly Comp<MvPos> Pos = Register<MvPos>();
    public static readonly Comp<MvSmall> Small = Register<MvSmall>();
    public static readonly Comp<MvLarge> Large = Register<MvLarge>();
}

/// <summary>
/// The cluster commit path must address each Versioned component's OWN content segment. Regression cover for the defect where it did not (#666).
/// </summary>
/// <remarks>
/// <para>
/// <c>PublishClusterVersionedSlot</c> resolves a component's committed bytes through a lazily-cached content accessor. That cache was keyed on the
/// ARCHETYPE, but content chunks belong to the COMPONENT — so when one archetype carries several Versioned components, the second one's chunk id was looked
/// up in the first one's segment. The engine then copied whatever sat at that address into the cluster slot.
/// </para>
/// <para>
/// <b>Why this fixture is shaped the way it is.</b> Three earlier attempts to catch this failed, each for a different reason, and each is now designed
/// against:
/// </para>
/// <list type="bullet">
/// <item>Reading the entity back with <c>Open().Read()</c> passes even when the bug is live — that path resolves through the revision chain, which is
/// correct. Only the CLUSTER slot is corrupted, and a field query is what reads it. Both Versioned components are therefore indexed, so the corruption is
/// observable whichever of the two the drain processes second.</item>
/// <item>Same-sized components hide it: the mis-addressed lookup stays in range and quietly returns wrong bytes. Hence 8 vs 16.</item>
/// <item>A single-entity or single-component write never exercises it — the accessor is only reused across entries, so both components must be written in
/// ONE transaction.</item>
/// </list>
/// </remarks>
[TestFixture]
class MultiVersionedClusterCommitTests : TestBase<MultiVersionedClusterCommitTests>
{
    // Sized to actually reach the failure, not merely to look thorough: at 64 entities / one update the mis-addressed lookup stays within the other
    // segment's allocated range and the corruption never materialises. The defect needs the two components' content segments to diverge in chunk-id range,
    // which takes real revision churn.
    private const int EntityCount = 2_000;
    private const int TagCount = 4;
    private const int Rounds = 15;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MvPos>();
        dbe.RegisterComponentFromAccessor<MvSmall>();
        dbe.RegisterComponentFromAccessor<MvLarge>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// AC: writing both Versioned components of one entity in a single transaction leaves each component's cluster slot holding ITS OWN committed value.
    /// </summary>
    [Test]
    public void WritingTwoVersionedComponentsInOneTransaction_KeepsEachClusterSlotCorrect()
    {
        using var dbe = SetupEngine();

        Assert.That(ArchetypeRegistry.GetMetadata<MvArch>().IsClusterEligible, Is.True,
            "premise: the SingleVersion slot makes this archetype cluster-backed, so commits take the cluster publish path");

        var ids = new EntityId[EntityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                ids[i] = tx.Spawn<MvArch>(
                    MvArch.Pos.Set(new MvPos(i, i)),
                    MvArch.Small.Set(new MvSmall(i % TagCount, i)),
                    MvArch.Large.Set(new MvLarge(i % TagCount, i, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Both Versioned components written in ONE transaction — the drain then holds entries for two different content segments under one archetype.
        // Repeated, because the two segments have to grow apart before a chunk id valid in one is invalid (or means something else) in the other.
        for (var round = 0; round < Rounds; round++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < EntityCount; i++)
            {
                var e = tx.OpenMut(ids[i]);
                e.Write(MvArch.Small) = new MvSmall((i + 1) % TagCount, i + 1000);
                e.Write(MvArch.Large) = new MvLarge((i + 2) % TagCount, i + 2000, i + 2000);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // Field queries evaluate against the CLUSTER slot, which is the half the revision chain cannot vouch for.
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                for (var tag = 0; tag < TagCount; tag++)
                {
                    var expectedSmall = CountWhere(i => (i + 1) % TagCount == tag);
                    var expectedLarge = CountWhere(i => (i + 2) % TagCount == tag);

                    var t = tag;
                    Assert.That(tx.Query<MvArch>().WhereField<MvSmall>(x => x.Tag == t).Count(), Is.EqualTo(expectedSmall),
                        $"MvSmall cluster slots must hold MvSmall's own committed values (tag {t})");
                    Assert.That(tx.Query<MvArch>().WhereField<MvLarge>(x => x.Tag == t).Count(), Is.EqualTo(expectedLarge),
                        $"MvLarge cluster slots must hold MvLarge's own committed values (tag {t})");
                }
            });
        }
    }

    /// <summary>
    /// AC: the same, through the revision chain — the half that was already correct, kept so a future fix cannot trade one for the other.
    /// </summary>
    [Test]
    public void WritingTwoVersionedComponentsInOneTransaction_KeepsBothChainsCorrect()
    {
        using var dbe = SetupEngine();

        var ids = new EntityId[EntityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                ids[i] = tx.Spawn<MvArch>(
                    MvArch.Pos.Set(new MvPos(i, i)),
                    MvArch.Small.Set(new MvSmall(0, i)),
                    MvArch.Large.Set(new MvLarge(0, i, i)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                var e = tx.OpenMut(ids[i]);
                e.Write(MvArch.Small) = new MvSmall(1, i + 1000);
                e.Write(MvArch.Large) = new MvLarge(2, i + 2000, i + 3000);
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                for (var i = 0; i < EntityCount; i++)
                {
                    var e = tx.Open(ids[i]);
                    var small = e.Read(MvArch.Small);
                    var large = e.Read(MvArch.Large);

                    Assert.That(small.Payload, Is.EqualTo(i + 1000), $"entity {i}: MvSmall payload");
                    Assert.That(large.Payload, Is.EqualTo(i + 2000), $"entity {i}: MvLarge payload");
                    Assert.That(large.Extra, Is.EqualTo(i + 3000), $"entity {i}: MvLarge extra — the 16-byte component's tail must not be truncated");
                }
            });
        }
    }

    private static int CountWhere(System.Func<int, bool> predicate)
    {
        var n = 0;
        for (var i = 0; i < EntityCount; i++)
        {
            if (predicate(i))
            {
                n++;
            }
        }

        return n;
    }
}
