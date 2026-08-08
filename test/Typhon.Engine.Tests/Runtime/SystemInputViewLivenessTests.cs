using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Issue #718 — a View built from an unfiltered <c>Query&lt;T&gt;().ToView()</c> is a snapshot taken at construction, not a live set.
/// </summary>
/// <remarks>
/// <para>
/// <c>EcsQuery.ToPullView</c> populates the view once and registers it with no <c>ViewRegistry</c>, unlike <c>ToIncrementalView</c>, which does. A system
/// fed such a view therefore runs against the membership that existed when the view was built, for the entire life of the runtime — so no system ever
/// processes an entity spawned after startup. Silent: the system runs every tick and reports a plausible entity count.
/// </para>
/// <para>
/// Found while root-causing #631, and it is that issue's actual cause. Kept here rather than in the issue text because the whole class of defect exists
/// only because no fixture anywhere spawns an entity while a runtime is ticking — every view test spawns first and creates the view second.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SystemInputViewLivenessTests : TestBase<SystemInputViewLivenessTests>
{
    [Test]
    [VerifiesRule("BIND-04")]
    public void SystemInputView_SeesEntitiesSpawnedWhileTheRuntimeIsRunning()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        Assert.That(view.Count, Is.EqualTo(10), "PREMISE: the view is populated at creation");

        var ticksSeen = 0;
        var lastSeen = -1;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", ctx => Volatile.Write(ref lastSeen, ctx.Entities.Count), input: () => view, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
            Assert.That(Volatile.Read(ref lastSeen), Is.EqualTo(10), "PREMISE: the system sees the entities that existed when its view was built");

            // Spawn while the runtime is ticking — the ordering a simulation actually produces, and the one no fixture covers.
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 10; i < 20; i++)
                {
                    var v = new TouchPos { X = i };
                    tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
                }

                tx.Commit();
            }

            var target = ticksSeen + 5;
            SpinWait.SpinUntil(() => ticksSeen >= target, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        Assert.That(Volatile.Read(ref lastSeen), Is.EqualTo(20),
            "a system's input view must include entities spawned while the runtime runs — measured: it stays at 10 forever, so those entities are "
            + "committed, durable and queryable, and no system will ever touch them");

        view.Dispose();
    }

    /// <summary>The same mechanism without a runtime: two views over one engine, differing only in when they were built.</summary>
    /// <remarks>
    /// Still quarantined, and deliberately so — this one pins the ENDPOINT, not the interim. The runtime now re-queries the pull views it feeds to systems
    /// once per tick, which is what makes the test above pass; a pull view held by user code and never refreshed is still a snapshot, because nothing
    /// publishes membership to it. Making this green needs direction 1 of #718 — a lifecycle-level notification channel views subscribe to by archetype —
    /// which is a design change to the view subsystem. Rewriting it to call Refresh() first would make it assert that RefreshPull works, which was never in
    /// doubt.
    /// </remarks>
    [Test]
    [Ignore("#718 — a pull View nobody refreshes is still frozen; needs the lifecycle notification channel (direction 1), not the per-tick refresh.")]
    public void ViewCreatedBeforeTheSpawns_ConvergesWithOneCreatedAfter()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txEarly = dbe.CreateQuickTransaction();
        var early = txEarly.Query<TouchArch>().ToView();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var txLate = dbe.CreateQuickTransaction();
        var late = txLate.Query<TouchArch>().ToView();

        Assert.That(late.Count, Is.EqualTo(10), "PREMISE: a view built after the spawns sees them");
        Assert.That(early.Count, Is.EqualTo(late.Count), "measured: early=0, late=10 — same engine, same moment, different construction time");

        early.Dispose();
        late.Dispose();
    }
}
