using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>Parent of <see cref="CcItem"/>, a SingleVersion child whose FK writes stage for commit.</summary>
[Component("Typhon.Test.ECS.CcBagData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CcBagData
{
    public int Capacity;
    public int _pad;
}

[Component("Typhon.Test.ECS.CcItemData", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct CcItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<CcBag> Owner;
    public int Weight;
    public int _pad;
}

[Archetype]
class CcBag : Archetype<CcBag>
{
    public static readonly Comp<CcBagData> Bag = Register<CcBagData>();
}

[Archetype]
class CcItem : Archetype<CcItem>
{
    public static readonly Comp<CcItemData> Item = Register<CcItemData>();
}

/// <summary>
/// #1247, CASC-01: a destroy cascades to the children its parent has at HEAD — when <c>Destroy()</c> runs and again at commit, alive at HEAD, with the
/// destroying transaction's own FK writes and spawns counted. A child committed after the destroyer's snapshot was found by the HEAD lookup and then dropped by
/// a liveness test at the snapshot; a child committed or re-keyed onto the parent after <c>Destroy()</c> was never looked for; the transaction's own re-keys
/// were not seen — each left a live child under a dead parent, or deleted one moved away first.
/// </summary>
[TestFixture]
class CascadeHeadTests : TestBase<CascadeHeadTests>
{
    private DatabaseEngine Open()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<BagData>();
        dbe.RegisterComponentFromAccessor<ItemData>();
        dbe.RegisterComponentFromAccessor<ClusterBagData>();
        dbe.RegisterComponentFromAccessor<SvItemData>();
        dbe.RegisterComponentFromAccessor<MixedItemData>();
        dbe.RegisterComponentFromAccessor<ItemTagData>();
        dbe.RegisterComponentFromAccessor<ItemAuditData>();
        dbe.RegisterComponentFromAccessor<PartData>();
        dbe.RegisterComponentFromAccessor<ProfileData>();
        dbe.RegisterComponentFromAccessor<CcBagData>();
        dbe.RegisterComponentFromAccessor<CcItemData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId Bag(DatabaseEngine dbe)
    {
        using var t = dbe.CreateQuickTransaction();
        var b = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(new BagData { Capacity = 1 }));
        Assert.That(t.Commit(), Is.True);
        return b;
    }

    private static EntityId Item(DatabaseEngine dbe, EntityId owner)
    {
        using var t = dbe.CreateQuickTransaction();
        var i = t.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = owner, Weight = 1 }));
        Assert.That(t.Commit(), Is.True);
        return i;
    }

    private static void Rekey(DatabaseEngine dbe, EntityId item, EntityId owner)
    {
        using var t = dbe.CreateQuickTransaction();
        t.OpenMut(item).Set(CascadeItem.Item, new ItemData { Owner = owner, Weight = 2 });
        Assert.That(t.Commit(), Is.True);
    }

    private static bool Alive(DatabaseEngine dbe, EntityId id)
    {
        using var t = dbe.CreateQuickTransaction();
        return t.IsAlive(id);
    }

    private static void Kill(DatabaseEngine dbe, EntityId id)
    {
        using var t = dbe.CreateQuickTransaction();
        t.Destroy(id);
        Assert.That(t.Commit(), Is.True);
    }

    // ── Another transaction's children ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private const string Marker = "#1247 cascade";

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildCommittedAfterTheDestroyersSnapshot_IsDestroyed() => ChildCommittedAfterTheDestroyersSnapshot(atSnapshot: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: a cascade child judged alive at the destroyer's snapshot.</summary>
    [Test]
    [RuleMutant("CASC-01")]
    public void AChildJudgedAtTheSnapshot_IsOrphaned() =>
        RuleMutants.AssertDetects("CASC-01", Marker, () => ChildCommittedAfterTheDestroyersSnapshot(atSnapshot: true));

    private void ChildCommittedAfterTheDestroyersSnapshot(bool atSnapshot)
    {
        using var dbe = Open();
        dbe.CascadeJudgesChildAtSnapshotForTest = atSnapshot;
        var bag = Bag(dbe);
        using var d = dbe.CreateQuickTransaction();
        var child = Item(dbe, bag);
        d.Destroy(bag);
        Assert.That(d.PendingDestroys.Contains(child), Is.True, $"{Marker}: found at Destroy(), alive at HEAD");
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, child), Is.False, $"{Marker}: orphaned");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildAndGrandchildCommittedAfterTheDestroyersSnapshot_AreDestroyed_SingleVersionClusters()
    {
        using var dbe = Open();
        EntityId bag, item, part;
        using (var t = dbe.CreateQuickTransaction())
        {
            bag = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using var d = dbe.CreateQuickTransaction();
        using (var t = dbe.CreateQuickTransaction())
        {
            item = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bag, Weight = 1 }));
            part = t.Spawn<ClusterPart>(ClusterPart.Part.Set(new PartData { Item = item, Serial = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        d.Destroy(bag);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, item), Is.False);
        Assert.That(Alive(dbe, part), Is.False);
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildCommittedAfterDestroy_BeforeTheCommit_IsDestroyed()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(bag);
        var child = Item(dbe, bag);
        Assert.That(d.PendingDestroys.Contains(child), Is.False, "not there at Destroy()");
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, child), Is.False, "resolved again at commit");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildReKeyedOntoTheParentAfterDestroy_BeforeTheCommit_IsDestroyed()
    {
        using var dbe = Open();
        var b1 = Bag(dbe);
        var b2 = Bag(dbe);
        var child = Item(dbe, b2);
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(b1);
        Rekey(dbe, child, b1);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, child), Is.False);
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildReKeyedOntoTheParentAfterTheSnapshot_IsDestroyed_OneReKeyedAway_Survives()
    {
        using var dbe = Open();
        var b1 = Bag(dbe);
        var b2 = Bag(dbe);
        var onto = Item(dbe, b2);
        var away = Item(dbe, b1);
        using var d = dbe.CreateQuickTransaction();   // at its snapshot: onto -> b2, away -> b1
        Rekey(dbe, onto, b1);
        Rekey(dbe, away, b2);
        d.Destroy(b1);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, onto), Is.False, "b1's child at HEAD");
        Assert.That(Alive(dbe, away), Is.True, "b2's child at HEAD: the snapshot's answer would delete it");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildDestroyedMeanwhile_OrConcurrently_CommitsBoth()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        var c1 = Item(dbe, bag);
        var c2 = Item(dbe, bag);
        using var d = dbe.CreateQuickTransaction();
        Kill(dbe, c1);
        using var t2 = dbe.CreateQuickTransaction();
        t2.Destroy(c2);
        d.Destroy(bag);
        Assert.That(d.PendingDestroys.Contains(c1), Is.False, "dead at HEAD: not taken");
        Assert.That(d.PendingDestroys.Contains(c2), Is.True, "its destroy only pending: alive at HEAD, taken too");
        Assert.That(t2.Commit(), Is.True);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, c1) || Alive(dbe, c2), Is.False);
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AChildAndGrandchildCommittedAfterDestroy_AreDestroyedAtCommit()
    {
        using var dbe = Open();
        EntityId bag, item, part;
        using (var t = dbe.CreateQuickTransaction())
        {
            bag = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using var d = dbe.CreateQuickTransaction();
        d.Destroy(bag);
        using (var t = dbe.CreateQuickTransaction())
        {
            item = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bag, Weight = 1 }));
            part = t.Spawn<ClusterPart>(ClusterPart.Part.Set(new PartData { Item = item, Serial = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, item), Is.False, "the child, found at commit");
        Assert.That(Alive(dbe, part), Is.False, "its child, found by the commit's walk reaching the child in turn");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void AUniqueCascadeChildCommittedAfterDestroy_IsDestroyed()
    {
        using var dbe = Open();
        EntityId bag, profile;
        using (var t = dbe.CreateQuickTransaction())
        {
            bag = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using var d = dbe.CreateQuickTransaction();
        d.Destroy(bag);
        using (var t = dbe.CreateQuickTransaction())
        {
            profile = t.Spawn<ClusterProfile>(ClusterProfile.Profile.Set(new ProfileData { Owner = bag, Setting = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, profile), Is.False);
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void ADestroyBatch_ResolvesItsCascadesAgainAtCommit()
    {
        using var dbe = Open();
        var b1 = Bag(dbe);
        var b2 = Bag(dbe);
        using var d = dbe.CreateQuickTransaction();
        d.DestroyBatch([b1, b2]);
        var c1 = Item(dbe, b1);
        var c2 = Item(dbe, b2);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, c1) || Alive(dbe, c2), Is.False);
    }

    // ── A SingleVersion child re-keyed in place, before the tick fence moves its index entry ────────────────────────────────────────────────────────────

    private static bool MayHaveStaleKeys<TArch>(DatabaseEngine dbe) where TArch : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        return dbe._archetypeStates[meta.ArchetypeId].ClusterState.MayHaveFenceStaleKeys(0);
    }

    private static (EntityId b1, EntityId b2, EntityId onto, EntityId away) SvChildrenReKeyedInTick(DatabaseEngine dbe)
    {
        EntityId b1, b2, onto, away;
        using (var t = dbe.CreateQuickTransaction())
        {
            b1 = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 1 }));
            b2 = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 2 }));
            onto = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = b2, Weight = 1 }));
            away = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = b1, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);   // the trees hold onto -> b2, away -> b1
        Assert.That(MayHaveStaleKeys<ClusterItem>(dbe), Is.False);
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(onto).Set(ClusterItem.Item, new SvItemData { Owner = b1, Weight = 2 });
            t.OpenMut(away).Set(ClusterItem.Item, new SvItemData { Owner = b2, Weight = 2 });
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(MayHaveStaleKeys<ClusterItem>(dbe), Is.True, "precondition: in-place re-keys the trees have not caught up with");
        return (b1, b2, onto, away);
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void ASingleVersionChildReKeyedOntoTheParentInPlace_BeforeTheFence_IsDestroyed_OneReKeyedAway_Survives()
    {
        using var dbe = Open();
        var (b1, _, onto, away) = SvChildrenReKeyedInTick(dbe);
        using (var d = dbe.CreateQuickTransaction())
        {
            d.Destroy(b1);
            Assert.That(d.Commit(), Is.True);
        }

        dbe.WriteTickFence(2);
        Assert.That(Alive(dbe, onto), Is.False, "found where it was written, its tree entry still under b2");
        Assert.That(Alive(dbe, away), Is.True, "its tree entry still under b1, its value under b2");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void ASingleVersionChildReKeyedInPlace_AfterDestroy_BeforeTheCommit_IsDestroyed()
    {
        using var dbe = Open();
        EntityId b1, b2, child;
        using (var t = dbe.CreateQuickTransaction())
        {
            b1 = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 1 }));
            b2 = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 2 }));
            child = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = b2, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(b1);
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(child).Set(ClusterItem.Item, new SvItemData { Owner = b1, Weight = 2 });
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(MayHaveStaleKeys<ClusterItem>(dbe), Is.True, "precondition: the tree still holds the child under b2");
        Assert.That(d.Commit(), Is.True);
        dbe.WriteTickFence(2);
        Assert.That(Alive(dbe, child), Is.False);
        Assert.That(Alive(dbe, b2), Is.True);
    }


    // ── The destroyer's own writes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("CASC-01")]
    public void ItsOwnReKeyOntoTheParent_IsDestroyed_ItsOwnReKeyAway_Survives_Versioned()
    {
        using var dbe = Open();
        var b1 = Bag(dbe);
        var b2 = Bag(dbe);
        var onto = Item(dbe, b2);
        var away = Item(dbe, b1);
        using var d = dbe.CreateQuickTransaction();
        d.OpenMut(onto).Set(CascadeItem.Item, new ItemData { Owner = b1, Weight = 3 });
        d.OpenMut(away).Set(CascadeItem.Item, new ItemData { Owner = b2, Weight = 3 });
        d.Destroy(b1);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, onto), Is.False, "its own write points it at b1");
        Assert.That(Alive(dbe, away), Is.True, "its own write moved it to b2 first");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void ItsOwnReKeyOntoTheParent_IsDestroyed_ItsOwnReKeyAway_Survives_StagedSingleVersion()
    {
        using var dbe = Open();
        EntityId b1, b2, onto, away;
        using (var t = dbe.CreateQuickTransaction())
        {
            b1 = t.Spawn<CcBag>(CcBag.Bag.Set(new CcBagData { Capacity = 1 }));
            b2 = t.Spawn<CcBag>(CcBag.Bag.Set(new CcBagData { Capacity = 2 }));
            onto = t.Spawn<CcItem>(CcItem.Item.Set(new CcItemData { Owner = b2, Weight = 1 }));
            away = t.Spawn<CcItem>(CcItem.Item.Set(new CcItemData { Owner = b1, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using var d = dbe.CreateQuickTransaction();
        d.OpenMut(onto).Set(CcItem.Item, new CcItemData { Owner = b1, Weight = 3 });
        d.OpenMut(away).Set(CcItem.Item, new CcItemData { Owner = b2, Weight = 3 });
        d.Destroy(b1);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, onto), Is.False, "its staged write points it at b1");
        Assert.That(Alive(dbe, away), Is.True, "its staged write moved it to b2 first");
    }

    [Test]
    [VerifiesRule("CASC-01")]
    public void ItsOwnSpawnAndReKeyOntoTheParent_AfterDestroy_AreDestroyed()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        var other = Item(dbe, Bag(dbe));
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(bag);
        var spawned = d.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = bag, Weight = 1 }));
        d.OpenMut(other).Set(CascadeItem.Item, new ItemData { Owner = bag, Weight = 1 });
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, spawned), Is.False);
        Assert.That(Alive(dbe, other), Is.False);
    }

    // ── The referential check (CASC-02) ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A transaction on a thread of its own (a transaction is thread-affine); background, so a test that fails never keeps the host alive.</summary>
    private sealed class Worker
    {
        private readonly Thread _thread;

        public Worker(Action body)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    Failure = e;
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public Exception Failure { get; private set; }

        public void Finish()
        {
            Assert.That(_thread.Join(TimeSpan.FromSeconds(30)), Is.True, "worker did not finish");
            Assert.That(Failure, Is.Null);
        }
    }

    /// <summary>A probe that parks the thread it runs on, once, until <see cref="Release"/>.</summary>
    private sealed class Hold : IDisposable
    {
        private readonly ManualResetEventSlim _reached = new();
        private readonly ManualResetEventSlim _release = new();
        private int _fired;

        public void Park()
        {
            if (Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            _reached.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
        }

        public void WaitReached() => Assert.That(_reached.Wait(TimeSpan.FromSeconds(10)), Is.True, "the held thread never reached its probe");

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _reached.Dispose();
            _release.Dispose();
        }
    }

    private static Worker DestroyOnAThread(DatabaseEngine dbe, EntityId parent) => new(() =>
    {
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(parent);
        Assert.That(d.Commit(), Is.True);
    });

    [Test]
    [VerifiesRule("CASC-02")]
    public void ASpawnUnderADestroyedParent_IsRefused_AndTheTransactionCanBeFixedAndCommitted()
    {
        using var dbe = Open();
        var dead = Bag(dbe);
        var live = Bag(dbe);
        Kill(dbe, dead);
        using var t = dbe.CreateQuickTransaction();
        var child = t.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = dead, Weight = 1 }));
        var ex = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
        Assert.That(ex.Child, Is.EqualTo(child));
        Assert.That(ex.Parent, Is.EqualTo(dead));
        Assert.That(ex.ParentDying, Is.False);
        Assert.That(ex.IsTransient, Is.False);
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.ForeignKeyViolation));
        Assert.That(dbe.CascadeGate.ReferrersOnStripeOf((long)dead.RawValue), Is.Zero, "the announcement given back");

        t.OpenMut(child).Set(CascadeItem.Item, new ItemData { Owner = live, Weight = 1 });
        Assert.That(t.Commit(), Is.True, "nothing was staged: the transaction commits once fixed");
        Assert.That(Alive(dbe, child), Is.True);
    }

    [Test]
    [VerifiesRule("CASC-02")]
    public void AReKeyOntoADestroyedParent_IsRefused_VersionedAndStagedSingleVersion()
    {
        using var dbe = Open();
        var dead = Bag(dbe);
        var home = Bag(dbe);
        var child = Item(dbe, home);
        Kill(dbe, dead);
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(child).Set(CascadeItem.Item, new ItemData { Owner = dead, Weight = 2 });
            var ex = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
            Assert.That((ex.Child, ex.Parent, ex.ParentDying), Is.EqualTo((child, dead, false)));
            t.Rollback();
        }

        EntityId svDead, svHome, svChild;
        using (var t = dbe.CreateQuickTransaction())
        {
            svDead = t.Spawn<CcBag>(CcBag.Bag.Set(new CcBagData { Capacity = 1 }));
            svHome = t.Spawn<CcBag>(CcBag.Bag.Set(new CcBagData { Capacity = 2 }));
            svChild = t.Spawn<CcItem>(CcItem.Item.Set(new CcItemData { Owner = svHome, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        Kill(dbe, svDead);
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(svChild).Set(CcItem.Item, new CcItemData { Owner = svDead, Weight = 2 });
            var ex = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
            Assert.That((ex.Child, ex.Parent, ex.ParentDying), Is.EqualTo((svChild, svDead, false)));
            t.Rollback();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            Assert.That(r.Open(child).Read(CascadeItem.Item).Owner.Id, Is.EqualTo(home), "the refused write left it where it was");
            Assert.That(r.Open(svChild).Read(CcItem.Item).Owner.Id, Is.EqualTo(svHome));
        }
    }

    /// <summary>
    /// A write that keeps the foreign key still publishes it: refused while the parent is dying — the destroyer's lookup found the child under the key
    /// HEAD holds, and the write would publish the one this transaction read over it.
    /// </summary>
    [Test]
    [VerifiesRule("CASC-02")]
    public void AWriteKeepingTheForeignKey_IsRefusedWhileTheParentIsDying()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        var child = Item(dbe, bag);
        using var hold = new Hold();
        dbe.CascadeAfterDyingMarkedProbe = hold.Park;
        var destroyer = DestroyOnAThread(dbe, bag);
        try
        {
            hold.WaitReached();
            Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.True, "precondition: the parent is being destroyed");
            using var t = dbe.CreateQuickTransaction();
            t.OpenMut(child).Set(CascadeItem.Item, new ItemData { Owner = bag, Weight = 9 });   // the key unchanged
            var ex = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
            Assert.That(ex.ParentDying, Is.True);
            t.Rollback();
        }
        finally
        {
            hold.Release();
        }

        destroyer.Finish();
        Assert.That(Alive(dbe, child), Is.False, "the destroyer's, found in the trees");
    }

    /// <summary>
    /// A transaction read the child under P1; another moved it to P2; a destroy of P1 committed, its lookup finding nothing under P1. A write of another
    /// field publishes the whole component as read — the key P1, unchanged for the writer and dead: refused.
    /// </summary>
    [Test]
    [VerifiesRule("CASC-02")]
    public void AWriteOfAnotherField_CarryingAKeyItsSnapshotHeld_IsRefusedOnceThatParentIsDestroyed()
    {
        using var dbe = Open();
        var p1 = Bag(dbe);
        var p2 = Bag(dbe);
        var child = Item(dbe, p1);
        using var t = dbe.CreateQuickTransaction();
        var read = t.Open(child).Read(CascadeItem.Item);
        Rekey(dbe, child, p2);
        Kill(dbe, p1);
        Assert.That(Alive(dbe, child), Is.True, "precondition: the destroy of p1 did not take it — it was p2's");
        t.OpenMut(child).Set(CascadeItem.Item, new ItemData { Owner = read.Owner, Weight = 7 });
        var ex = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
        Assert.That((ex.Parent, ex.ParentDying), Is.EqualTo((p1, false)));
        t.Rollback();
    }

    /// <summary>A Versioned FK component supplied for the first time to a committed entity is a write of the key, like a spawn's.</summary>
    [Test]
    [VerifiesRule("CASC-01")]
    [VerifiesRule("CASC-02")]
    public void AFirstSupplyOfTheForeignKey_IsCascadedAndChecked()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        var dead = Bag(dbe);
        EntityId bare1, bare2;
        using (var t = dbe.CreateQuickTransaction())
        {
            bare1 = t.Spawn<CascadeItem>();
            bare2 = t.Spawn<CascadeItem>();
            Assert.That(t.Commit(), Is.True);
        }

        Kill(dbe, dead);
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(bare1).Enable(CascadeItem.Item, new ItemData { Owner = dead, Weight = 1 });
            Assert.That(Assert.Throws<ForeignKeyViolationException>(() => t.Commit()).Parent, Is.EqualTo(dead), "a supply onto a destroyed parent");
            t.Rollback();
        }

        using (var d = dbe.CreateQuickTransaction())
        {
            d.Destroy(bag);
            d.OpenMut(bare2).Enable(CascadeItem.Item, new ItemData { Owner = bag, Weight = 1 });
            Assert.That(d.Commit(), Is.True);
        }

        Assert.That(Alive(dbe, bare2), Is.False, "its own supply onto the parent it destroys: cascaded");
        Assert.That(Alive(dbe, bare1), Is.True);
    }

    /// <summary>A child's commit passed its check and is held before it publishes; a destroy of its parent commits meanwhile: it waits, and takes it.</summary>
    [Test]
    [VerifiesRule("CASC-02")]
    public void ADestroyCommittingWhileAChildIsBetweenItsCheckAndItsPublish_WaitsForIt_AndDestroysIt() =>
        DestroyWhileAChildIsBetweenItsCheckAndItsPublish(skipWait: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: a destroyer that does not wait for the referrers counted before its mark.</summary>
    [Test]
    [RuleMutant("CASC-02")]
    public void ADestroyThatDoesNotWait_OrphansTheChild() =>
        RuleMutants.AssertDetects("CASC-02", Marker, () => DestroyWhileAChildIsBetweenItsCheckAndItsPublish(skipWait: true));

    private void DestroyWhileAChildIsBetweenItsCheckAndItsPublish(bool skipWait)
    {
        using var dbe = Open();
        dbe.CascadeSkipsReferrerWaitForTest = skipWait;
        var bag = Bag(dbe);
        using var childHold = new Hold();
        using var waiting = new ManualResetEventSlim();
        dbe.CascadeAfterReferrerCheckProbe = childHold.Park;
        EntityId child = default;
        var spawner = new Worker(() =>
        {
            using var t = dbe.CreateQuickTransaction();
            child = t.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = bag, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        });
        Worker destroyer = null;
        try
        {
            childHold.WaitReached();
            dbe.CascadeBeforeReferrerWaitProbe = () => waiting.Set();
            destroyer = DestroyOnAThread(dbe, bag);
            Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.True);
            Assert.That(dbe.CascadeGate.ReferrersOnStripeOf((long)bag.RawValue), Is.EqualTo(1), "the destroyer's wait has a counted referrer to wait for");
            Assert.That(Alive(dbe, bag), Is.True, $"{Marker}: the destroy committed while a counted child had not published");
        }
        finally
        {
            dbe.CascadeBeforeReferrerWaitProbe = null;
            childHold.Release();
        }

        spawner.Finish();
        destroyer.Finish();
        Assert.That(Alive(dbe, bag), Is.False);
        Assert.That(Alive(dbe, child), Is.False, $"{Marker}: the child was not found by the destroyer's lookup");
        Assert.That(dbe.CascadeGate.ReferrersOnStripeOf((long)bag.RawValue), Is.Zero);
        Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.False);
    }

    /// <summary>
    /// A destroy of the parent holds its dying mark: a spawn and a re-key pointing at it are refused as dying, and the same transaction committed again
    /// after the destroy published is refused as destroyed.
    /// </summary>
    [Test]
    [VerifiesRule("CASC-02")]
    public void AChildCommittingWhileItsParentIsBeingDestroyed_IsRefused_ThenRefusedAsDestroyed()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        var home = Bag(dbe);
        var other = Item(dbe, home);
        using var hold = new Hold();
        dbe.CascadeAfterDyingMarkedProbe = hold.Park;
        var destroyer = DestroyOnAThread(dbe, bag);
        using var t = dbe.CreateQuickTransaction();
        EntityId spawned;
        try
        {
            hold.WaitReached();
            spawned = t.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = bag, Weight = 1 }));
            var ex = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
            Assert.That(ex.ParentDying, Is.True);
            Assert.That(ex.IsTransient, Is.True);

            using var r = dbe.CreateQuickTransaction();
            r.OpenMut(other).Set(CascadeItem.Item, new ItemData { Owner = bag, Weight = 1 });
            Assert.That(Assert.Throws<ForeignKeyViolationException>(() => r.Commit()).ParentDying, Is.True);
            r.Rollback();
        }
        finally
        {
            hold.Release();
        }

        destroyer.Finish();
        var again = Assert.Throws<ForeignKeyViolationException>(() => t.Commit());
        Assert.That(again.ParentDying, Is.False, "the destroy published: a retry is refused for good");
        Assert.That(again.IsTransient, Is.False);
        t.Rollback();
        Assert.That(Alive(dbe, spawned), Is.False);
        using (var r = dbe.CreateQuickTransaction())
        {
            Assert.That(r.Open(other).Read(CascadeItem.Item).Owner.Id, Is.EqualTo(home), "its re-key was refused");
        }

        Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.False, "the mark given back once the destroy published");
    }

    [Test]
    [VerifiesRule("CASC-02")]
    public void AGrandchildUnderAChildBeingCascaded_IsRefused()
    {
        using var dbe = Open();
        EntityId bag, item;
        using (var t = dbe.CreateQuickTransaction())
        {
            bag = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 1 }));
            item = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bag, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using var hold = new Hold();
        dbe.CascadeAfterDyingMarkedProbe = hold.Park;
        var destroyer = DestroyOnAThread(dbe, bag);
        try
        {
            hold.WaitReached();
            Assert.That(dbe.CascadeGate.IsDying((long)item.RawValue), Is.True, "the cascaded child is marked dying in its turn");
            using var t = dbe.CreateQuickTransaction();
            t.Spawn<ClusterPart>(ClusterPart.Part.Set(new PartData { Item = item, Serial = 1 }));
            Assert.That(Assert.Throws<ForeignKeyViolationException>(() => t.Commit()).Parent, Is.EqualTo(item));
            t.Rollback();
        }
        finally
        {
            hold.Release();
        }

        destroyer.Finish();
        Assert.That(Alive(dbe, item), Is.False);
    }

    [Test]
    [VerifiesRule("CASC-02")]
    public void ADestroyThatFailsBeforeStaging_GivesItsMarksBack_AndCommitsWhenRetried()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(bag);
        dbe.CascadeAfterDyingMarkedProbe = () =>
        {
            dbe.CascadeAfterDyingMarkedProbe = null;
            throw new InvalidOperationException("staged failure");
        };
        Assert.Throws<InvalidOperationException>(() => d.Commit());
        Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.False, "the mark given back");

        var child = Item(dbe, bag);   // the parent is alive: a child commits
        Assert.That(d.Commit(), Is.True, "nothing was staged: the destroy commits when retried");
        Assert.That(Alive(dbe, bag) || Alive(dbe, child), Is.False, "and takes the child committed meanwhile");
    }

    [Test]
    [VerifiesRule("CASC-02")]
    public void TwoDestroysOfOneParent_KeepItDyingUntilBothEnd()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        using var hold = new Hold();
        dbe.CascadeAfterDyingMarkedProbe = hold.Park;
        var first = DestroyOnAThread(dbe, bag);
        try
        {
            hold.WaitReached();
            using (var second = dbe.CreateQuickTransaction())
            {
                second.Destroy(bag);
                Assert.That(second.Commit(), Is.True);
            }

            Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.True, "the first destroy still holds its mark");
        }
        finally
        {
            hold.Release();
        }

        first.Finish();
        Assert.That(dbe.CascadeGate.IsDying((long)bag.RawValue), Is.False);
    }

    [Test]
    [VerifiesRule("CASC-02")]
    public void AParentSharingADyingParentsStripe_IsNotRefused()
    {
        using var dbe = Open();
        var (p, q) = TwoParentsOnOneStripe(dbe);
        using var hold = new Hold();
        dbe.CascadeAfterDyingMarkedProbe = hold.Park;
        var destroyer = DestroyOnAThread(dbe, p);
        try
        {
            hold.WaitReached();
            Assert.That(dbe.CascadeGate.IsDying((long)q.RawValue), Is.False, "the dying test is exact");
            using var t = dbe.CreateQuickTransaction();
            t.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = q, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        }
        finally
        {
            hold.Release();
        }

        destroyer.Finish();
    }

    /// <summary>
    /// P and Q share a stripe. A child of Q counted before P's destroy marks P holds that destroy; one counted after does not — a stream of referrers to
    /// the stripe's other parents cannot starve a destroyer.
    /// </summary>
    [Test]
    [VerifiesRule("CASC-02")]
    public void ADestroyWaitsOnlyForTheReferrersCountedBeforeIt()
    {
        using var dbe = Open();
        var (p, q) = TwoParentsOnOneStripe(dbe);
        var holds = new System.Collections.Concurrent.ConcurrentDictionary<int, Hold>();
        dbe.CascadeAfterReferrerCheckProbe = () =>
        {
            if (holds.TryGetValue(Environment.CurrentManagedThreadId, out var h))
            {
                h.Park();
            }
        };

        Worker ChildOf(EntityId parent, Hold hold) => new(() =>
        {
            holds[Environment.CurrentManagedThreadId] = hold;
            using var t = dbe.CreateQuickTransaction();
            t.Spawn<CascadeItem>(CascadeItem.Item.Set(new ItemData { Owner = parent, Weight = 1 }));
            Assert.That(t.Commit(), Is.True);
        });

        using var before = new Hold();
        using var after = new Hold();
        using var waiting = new ManualResetEventSlim();
        var r1 = ChildOf(q, before);
        Worker destroyer = null, r2 = null;
        try
        {
            before.WaitReached();
            dbe.CascadeBeforeReferrerWaitProbe = () => waiting.Set();
            destroyer = DestroyOnAThread(dbe, p);
            Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)), Is.True);
            r2 = ChildOf(q, after);
            after.WaitReached();
            Assert.That(Alive(dbe, p), Is.True, "precondition: the destroy waits for the child counted before it");
            before.Release();
            r1.Finish();
            destroyer.Finish();   // with the second child still counted
            Assert.That(Alive(dbe, p), Is.False);
        }
        finally
        {
            dbe.CascadeBeforeReferrerWaitProbe = null;
            before.Release();
            after.Release();
        }

        r2.Finish();
    }

    private static (EntityId P, EntityId Q) TwoParentsOnOneStripe(DatabaseEngine dbe)
    {
        var byStripe = new System.Collections.Generic.Dictionary<int, EntityId>();
        for (var i = 0; i < 300; i++)
        {
            var b = Bag(dbe);
            if (byStripe.TryGetValue(CascadeReferrerGate.SlotOf((long)b.RawValue), out var first))
            {
                return (first, b);
            }

            byStripe[CascadeReferrerGate.SlotOf((long)b.RawValue)] = b;
        }

        Assert.Fail("precondition: two parents on one stripe");
        return default;
    }

    [Test]
    public void ADestroyOfNoCascadeParent_ResolvesNothingAtCommit()
    {
        using var dbe = Open();
        var bag = Bag(dbe);
        var child = Item(dbe, bag);
        using var d = dbe.CreateQuickTransaction();
        d.Destroy(child);
        Item(dbe, bag);
        Assert.That(d.Commit(), Is.True);
        Assert.That(Alive(dbe, bag), Is.True, "a child's destroy cascades nowhere");
        Assert.That(Alive(dbe, child), Is.False);
    }
}
