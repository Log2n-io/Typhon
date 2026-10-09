using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1102 — a spawn queued from a parallel chunk becomes a real, alive, readable entity, with the id the caller was given.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the acceptance test for the whole feature, so it goes through the real runtime.</b> #1099's cases drive the writer directly, which is right
/// for the write side and proves nothing about the apply; here a system queues through <c>ctx.Commands</c> and a later tick reads the entity back out of an
/// ordinary transaction, which is the only shape that exercises the buffer, the drain, the spawn path and the id contract together.
/// </para>
/// <para>
/// <b>The id is the assertion that matters.</b> Anything can create 3 000 entities; what makes the design worth having is that the id handed to the caller
/// at queue time is the id the entity has afterwards, so a reference stored in another component resolves with no remap pass. The corpse-and-loot case is
/// asserted by reading <c>Owner</c> back off the loot and opening it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class EntityCommandApplyTests : TestBase<EntityCommandApplyTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CmdPosition>();
        dbe.RegisterComponentFromAccessor<CmdOwner>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Queue once from a system, then let the runtime tick on; the entities must exist with the ids the system was handed.
    /// </summary>
    [Test]
    public void QueuedSpawnsBecomeAliveEntitiesWithTheIdsTheCallerWasGiven()
    {
        using var dbe = SetupEngine();

        const int count = 200;
        var handed = new List<EntityId>(count);
        var queued = new ManualResetEventSlim(false);
        var ticksAfter = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Queue", ctx =>
            {
                if (queued.IsSet)
                {
                    Interlocked.Increment(ref ticksAfter);
                    return;
                }

                var cmds = ctx.Commands;
                if (!cmds.IsValid)
                {
                    queued.Set();
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    var id = cmds.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(i, i * 2)));
                    if (!id.IsNull)
                    {
                        handed.Add(id);
                    }
                }

                queued.Set();
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 500 });

        runtime.Start();
        Assert.That(queued.Wait(TimeSpan.FromSeconds(5)), Is.True, "the system never ran");

        // Two ticks past the queueing one, so the drain has certainly run: the drain for tick N happens on tick N, before its fence.
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksAfter) >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(handed, Has.Count.EqualTo(count), "precondition: every spawn was accepted");

        using var tx = dbe.CreateQuickTransaction();
        var alive = 0;
        var readBack = 0;
        foreach (var id in handed)
        {
            if (!tx.IsAlive(id))
            {
                continue;
            }

            alive++;
            if (tx.TryOpen(id, out var e) && Math.Abs(e.Read(CmdCorpse.Position).Y - (e.Read(CmdCorpse.Position).X * 2)) < 0.001f)
            {
                readBack++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(alive, Is.EqualTo(count), "every queued entity must be alive under the id the caller was handed");
            Assert.That(readBack, Is.EqualTo(count), "and must carry the component values the command supplied");
        });
    }

    /// <summary>
    /// The corpse-and-loot case end to end: the id a system copies into another entity's component at queue time resolves to the real corpse afterwards,
    /// with no remap pass anywhere in the engine.
    /// </summary>
    [Test]
    public void AnIdStoredInAComponentAtQueueTimeResolvesAfterTheApply()
    {
        using var dbe = SetupEngine();

        var corpseId = EntityId.Null;
        var lootId = EntityId.Null;
        var queued = new ManualResetEventSlim(false);
        var ticksAfter = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Die", ctx =>
            {
                if (queued.IsSet)
                {
                    Interlocked.Increment(ref ticksAfter);
                    return;
                }

                var cmds = ctx.Commands;
                if (!cmds.IsValid)
                {
                    queued.Set();
                    return;
                }

                var corpse = cmds.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(7, 14)));
                if (!corpse.IsNull)
                {
                    var loot = cmds.Spawn<CmdLoot>(CmdLoot.Position.Set(new CmdPosition(7, 14)), CmdLoot.Owner.Set(new CmdOwner(corpse)));
                    corpseId = corpse;
                    lootId = loot;
                }

                queued.Set();
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 500 });

        runtime.Start();
        Assert.That(queued.Wait(TimeSpan.FromSeconds(5)), Is.True, "the system never ran");
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksAfter) >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(corpseId.IsNull, Is.False, "precondition: the corpse was queued");
        Assert.That(lootId.IsNull, Is.False, "precondition: the loot was queued");

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(tx.IsAlive(corpseId), Is.True, "the corpse must be alive");
            Assert.That(tx.IsAlive(lootId), Is.True, "and so must the loot");
            Assert.That(tx.TryOpen(lootId, out var loot), Is.True);
            var stored = EntityId.FromRawValue((ulong)loot.Read(CmdLoot.Owner).OwnerRaw);
            Assert.That(stored, Is.EqualTo(corpseId), "the 8 bytes stored at queue time must be the corpse's id, unrewritten");
            Assert.That(tx.IsAlive(stored), Is.True, "and must resolve to a live entity — the point of returning a final id");
        });
    }

    /// <summary>
    /// A destroy queued for an entity spawned in the same tick collapses the pair to nothing observable, rather than leaving a row nothing can reach. The
    /// drain applies every spawn before any destroy for exactly this.
    /// </summary>
    [Test]
    public void ASpawnAndADestroyQueuedInOneTickLeaveNothingBehind()
    {
        using var dbe = SetupEngine();

        var id = EntityId.Null;
        var queued = new ManualResetEventSlim(false);
        var ticksAfter = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("SpawnThenDestroy", ctx =>
            {
                if (queued.IsSet)
                {
                    Interlocked.Increment(ref ticksAfter);
                    return;
                }

                var cmds = ctx.Commands;
                if (cmds.IsValid)
                {
                    id = cmds.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(1, 2)));
                    if (!id.IsNull)
                    {
                        cmds.Destroy(id);
                    }
                }

                queued.Set();
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 500 });

        runtime.Start();
        Assert.That(queued.Wait(TimeSpan.FromSeconds(5)), Is.True, "the system never ran");
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksAfter) >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(id.IsNull, Is.False, "precondition: the spawn was queued");

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.IsAlive(id), Is.False, "the entity must not survive a destroy queued in the same tick");
    }
}
