using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Rule <b>AP-06</b> (<c>rules/durability.md</c>): an entity's cluster slot holds its newest committed revision, whatever order the publishes run in.
/// </summary>
/// <remarks>
/// Two transactions may update one entity concurrently: without a conflict handler each prepares its own revision, and the WAL appends do not have to finish
/// in the order the revisions were added. The publish copied its value into the cluster slot after releasing the chain lock and unconditionally, so the
/// older revision publishing last overwrote the newer one: point reads, which walk the chain, saw the newer value, while bulk iteration and Path-B scans,
/// which read the slot, saw the older — and a clean close persisted the slot that way (CS-03). The fixture holds the older publish at the point after its
/// WAL append, lets the newer commit run to completion, then releases it.
/// </remarks>
[TestFixture]
class VersionedPublishOrderTests : TestBase<VersionedPublishOrderTests>
{
    private const string Ap06Marker = "AP-06 violated: the cluster slot holds an older revision than the chain's newest";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static void Update(DatabaseEngine dbe, EntityId id, long key)
    {
        using var tx = dbe.CreateQuickTransaction();
        var target = tx.OpenMut(id);
        var item = target.Read(PdItemArch.Item);
        item.Key = key;
        target.Set(PdItemArch.Item, item);
        tx.Commit();
    }

    [Test]
    [VerifiesRule("AP-06")]
    public void TwoUpdatesPublishedOutOfOrder_LeaveTheNewerValueInTheClusterSlot()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = VersionedPublishDurabilityTests.OpenEngine(scope);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<PdItemArch>(PdItemArch.Item.Set(new PdItem { Key = 1 }));
            tx.Commit();
        }

        var table = dbe.GetComponentTable<PdItem>();
        using var olderAtPublish = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        var publishes = 0;
        dbe.PublishComponentProbe = (t, _) =>
        {
            // The first publish of this table is the older update's: hold it after its append, before it stamps or copies anything.
            if (ReferenceEquals(t, table) && Interlocked.Increment(ref publishes) == 1)
            {
                olderAtPublish.Set();
                releaseOlder.Wait(Patience);
            }
        };

        Exception failure = null;
        var older = new Thread(() => Run(() => Update(dbe, id, 100))) { IsBackground = true };
        var newer = new Thread(() => Run(() => Update(dbe, id, 200))) { IsBackground = true };
        try
        {
            older.Start();
            Assert.That(olderAtPublish.Wait(Patience), Is.True, "premise: the older update reaches its publish");

            // Prepared after the older one, so its revision is the newer; published first, because the older one is held.
            newer.Start();
            Assert.That(newer.Join(Patience), Is.True, "premise: the newer update commits while the older one is held at its publish");
        }
        finally
        {
            releaseOlder.Set();
        }

        Assert.That(older.Join(Patience), Is.True, "the older update finishes its publish once released");
        dbe.PublishComponentProbe = null;
        Assert.That(failure, Is.Null);

        using (var r = dbe.CreateReadOnlyTransaction())
        {
            Assert.That(r.Open(id).Read(PdItemArch.Item).Key, Is.EqualTo(200), "premise: the chain's newest committed revision is the newer update's");
        }

        Assert.That(VersionedPublishDurabilityTests.ClusterKey(dbe, id), Is.EqualTo(200),
            $"{Ap06Marker}: the older update published last and copied its value over the newer one. Bulk iteration and Path-B scans now read 100 while "
            + "point reads read 200, and a clean close persists the slot that way.");

        void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        }
    }
}
