using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The four defects a review of #1099/#1102 found, each pinned by the test whose absence let it through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of these were CRITICAL and one of them was hidden by a passing test of mine</b>, which is the part worth remembering. The spawn-then-destroy case
/// in <see cref="EntityCommandApplyTests"/> asserted that the entity was not alive afterwards — and it was satisfied by the drain throwing and losing the
/// entire tick's transaction, which is the opposite of the behaviour it was written to check. An assertion that a thing is ABSENT is satisfied by everything
/// having failed.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class EntityCommandReviewFixTests : TestBase<EntityCommandReviewFixTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CmdPosition>();
        dbe.RegisterComponentFromAccessor<CmdOwner>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static TyphonRuntime QuietRuntime(DatabaseEngine dbe, int workerCount = 4) =>
        TyphonRuntime.Create(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Idle", _ => { }),
            new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 1000, EntityCommandsPerTick = 4096 });

    /// <summary>
    /// <b>C1 — two producers on one chunk index must never be handed the same key.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cursor is keyed by (chunk index, archetype) and the scheduler guarantees disjoint worker SLOTS, not disjoint chunk indices across systems. Two
    /// systems with no DAG edge run concurrently, and a <c>CallbackSystem</c> is one chunk, so both see <c>ChunkIndex</c> 0 — both read <c>used == 0</c> and
    /// both got the same key, producing two identical <see cref="EntityId"/>s and two EntityMap rows under one key.
    /// </para>
    /// <para>
    /// <b>Staged rather than raced.</b> Two writers on one chunk index are obtained directly and driven from two threads released by a barrier, because
    /// hoping two real systems overlap is how a race test passes while proving nothing. The ids are collected per thread and compared afterwards, so the
    /// assertion is on the set and not on the interleaving.
    /// </para>
    /// </remarks>
    [Test]
    [Repeat(5)]
    public void TwoProducersOnOneChunkIndexNeverShareAKey()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        const int each = 400;
        var a = new List<long>(each);
        var b = new List<long>(each);
        using var gate = new Barrier(2);

        void Drive(List<long> into, int slot)
        {
            // Same CHUNK index, different worker slots — which is exactly the shape the scheduler permits and the cursor did not account for.
            var w = runtime.EntityCommands.GetWriter(slot, 0);
            gate.SignalAndWait();
            for (var i = 0; i < each; i++)
            {
                var id = w.Spawn<CmdCorpse>();
                if (!id.IsNull)
                {
                    into.Add(id.EntityKey);
                }
            }
        }

        var t1 = new Thread(() => Drive(a, 0));
        var t2 = new Thread(() => Drive(b, 1));
        t1.Start();
        t2.Start();
        Assert.That(t1.Join(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(t2.Join(TimeSpan.FromSeconds(10)), Is.True);

        var all = new HashSet<long>(a);
        var collisions = 0;
        foreach (var key in b)
        {
            if (!all.Add(key))
            {
                collisions++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(a, Has.Count.EqualTo(each), "producer A must have been given every key it asked for");
            Assert.That(b, Has.Count.EqualTo(each), "and so must producer B");
            Assert.That(collisions, Is.Zero, $"{collisions} keys were handed to BOTH producers — every one is a duplicate EntityId");
        });
    }

    /// <summary>
    /// <b>C2 — a queued destroy actually destroys, and does not take the tick's spawns down with it.</b>
    /// </summary>
    /// <remarks>
    /// A destroy carries archetype id <c>-1</c>, and the drain grouped by archetype and resolved <c>(ushort)-1 == 65535</c> against a 4 096-entry table —
    /// an <see cref="IndexOutOfRangeException"/> on the tick driver, before the transaction committed, so every spawn of that tick was lost as well. The
    /// second assertion here is the one the earlier test lacked: something unrelated must SURVIVE the tick, or "not alive" proves nothing.
    /// </remarks>
    [Test]
    public void AQueuedDestroyRemovesItsTargetAndLeavesTheTicksOtherSpawnsAlone()
    {
        using var dbe = SetupEngine();

        // A pre-existing entity to destroy, so the destroy is not of something spawned in the same tick.
        EntityId victim;
        using (var seed = dbe.CreateQuickTransaction())
        {
            victim = seed.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(1, 1)));
            seed.Commit();
        }

        var survivor = EntityId.Null;
        var queued = new ManualResetEventSlim(false);
        var ticksAfter = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Mix", ctx =>
            {
                if (queued.IsSet)
                {
                    Interlocked.Increment(ref ticksAfter);
                    return;
                }

                var cmds = ctx.Commands;
                if (cmds.IsValid)
                {
                    cmds.Destroy(victim);
                    survivor = cmds.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(9, 9)));
                }

                queued.Set();
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 500 });

        runtime.Start();
        Assert.That(queued.Wait(TimeSpan.FromSeconds(5)), Is.True, "the system never ran");
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksAfter) >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(tx.IsAlive(victim), Is.False, "the queued destroy must have removed its target");
            Assert.That(survivor.IsNull, Is.False, "precondition: the spawn was accepted");
            Assert.That(tx.IsAlive(survivor), Is.True,
                "and the spawn queued in the SAME tick must have survived — a drain that threw would satisfy the first assertion and fail this one");
        });
    }

    /// <summary>
    /// <b>C3 — a value naming a component the archetype does not have is refused, not thrown.</b>
    /// </summary>
    /// <remarks>
    /// The apply cannot refuse: it runs on the tick driver past the point where any caller could be told, so an unknown component id there threw and lost
    /// the tick. One wrong <c>Comp&lt;T&gt;.Set</c> is an ordinary mistake and belongs in the rejected count.
    /// </remarks>
    [Test]
    public void AValueForAComponentTheArchetypeLacksIsRefusedAtTheCall()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        // CmdCorpse has Position and not Owner.
        var id = runtime.EntityCommands.GetWriter(0, 0).Spawn<CmdCorpse>(CmdLoot.Owner.Set(new CmdOwner(EntityId.FromParts(1, 1))));

        Assert.Multiple(() =>
        {
            Assert.That(id.IsNull, Is.True, "a value for a component the archetype does not have cannot be queued");
            Assert.That(runtime.EntityCommands.RejectedCount, Is.EqualTo(1u), "and is a rejection, counted");
            Assert.That(runtime.EntityCommands.Count, Is.Zero, "with nothing left in the segment");
        });
    }

    /// <summary>
    /// <b>C4 — the per-tick counters are per-tick even when the tick accepted nothing.</b>
    /// </summary>
    /// <remarks>
    /// <c>Reset</c>'s body was gated on a flag raised only by a SUCCESSFUL append, so a tick that only refused cleared nothing and the counts accumulated —
    /// which breaks the exactness the tolerate-and-count overflow verdict depends on, and turned the once-per-tick refusal log into once-per-process.
    /// </remarks>
    [Test]
    public void RefusalCountersAreClearedByATickThatAcceptedNothing()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var w = runtime.EntityCommands.GetWriter(0, 0);

        for (var i = 0; i < 3; i++)
        {
            Assert.That(w.Spawn<SpawnUnregArch>().IsNull, Is.True);
        }

        Assert.That(runtime.EntityCommands.RejectedCount, Is.EqualTo(3u), "precondition: the refusals were counted");

        runtime.EntityCommands.Reset();
        Assert.That(runtime.EntityCommands.RejectedCount, Is.Zero, "a tick boundary must clear them even though nothing was accepted");

        for (var i = 0; i < 2; i++)
        {
            w.Spawn<SpawnUnregArch>();
        }

        Assert.That(runtime.EntityCommands.RejectedCount, Is.EqualTo(2u), "and the next tick must report its OWN count, not a running total");
    }

    /// <summary>
    /// The key arithmetic as a property: every run <c>Reserve</c> hands out is disjoint from every other, over randomised chunk and count sequences.
    /// </summary>
    /// <remarks>
    /// Cheaper and broader than the threaded case above, and it covers the boundary the threaded one does not: a run that straddles a block takes a fresh
    /// generation, and the tail it leaves must not be reissued.
    /// </remarks>
    [Test]
    public void EveryReservedRunIsDisjoint()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var keys = runtime.EntityCommands.Keys;
        var archetype = ArchetypeRegistry.GetMetadata<CmdCorpse>().ArchetypeId;

        var seen = new HashSet<long>();
        var rng = new Random(1102);
        var issued = 0;
        for (var i = 0; i < 2000; i++)
        {
            var chunk = rng.Next(keys.Stride);
            var count = 1 + rng.Next(5);
            var first = keys.Reserve(archetype, chunk, count);
            if (first < 0)
            {
                continue;
            }

            for (var k = 0; k < count; k++)
            {
                Assert.That(seen.Add(first + k), Is.True, $"key {first + k} was reserved twice (iteration {i}, chunk {chunk}, count {count})");
                issued++;
            }
        }

        Assert.That(issued, Is.GreaterThan(1000), "precondition: the loop must have issued a meaningful number of keys");
    }
}
