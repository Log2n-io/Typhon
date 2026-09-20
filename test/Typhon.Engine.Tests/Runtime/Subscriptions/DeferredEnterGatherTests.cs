using NUnit.Framework;
using System;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// An enter deferred by the per-frame budget is still delivered, however long the entity then sits still.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this pins.</b> <c>SelectEnters</c> drops candidates over <c>EnterBudgetPerFrame</c> and records them nowhere: they stay unknown to the session and
/// are meant to be rediscovered by the next gather. The changed-only gather (C-1) is that walk, and it masks each run down to the slots S1 reported as
/// changed BEFORE probing the known-set — so a deferred entity that then stops changing is never probed again, never produces an enter, and is never sent.
/// It also still counts toward the hit total that proves "nothing left the view" and skips the leave sweep, so the same inflation hides leaves.
/// </para>
/// <para>
/// <b>It needs no exotic observer.</b> This fixture uses a plain <c>World</c> observer and a stationary world, which is why restricting the fast path to
/// <c>World</c> profiles — the first repair attempted — closes nothing.
/// </para>
/// <para>
/// <b>The mover is what makes it bite.</b> Without it the session has nothing to publish on the tick after the deferral, so its baseline stalls and the next
/// tick takes the full walk, which finds the leftovers: the defect is self-limiting to one tick. One entity that changes every tick keeps a frame flowing,
/// which keeps <c>Baseline == tick - 1</c> true, which keeps the fast path engaged.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class DeferredEnterGatherTests : TestBase<DeferredEnterGatherTests>
{
    private const string Profile = "god-world";

    /// <summary>Deliberately tiny, so the deferral happens on the tick the statics appear rather than needing hundreds of entities.</summary>
    private const int EnterBudget = 2;

    private const int Statics = 5;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    /// <summary>
    /// Every entity the enter budget defers still arrives.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-03")]
    public void EntitiesDeferredByTheEnterBudgetAreStillDelivered()
    {
        var options = new SubscriptionsOptions
        {
            EnterBudgetPerFrame = EnterBudget,
        };

        using var harness = FrameHarness.Create(
            ProjectionTestSchema.SetupEngine(ServiceProvider),
            Declare,
            nameof(EntitiesDeferredByTheEnterBudgetAreStillDelivered),
            options);

        // The mover, alone, so the session completes its view and reaches a steady state before anything is deferred.
        Spawn(harness, 0f, 0f, speed: 9f);
        var session = harness.OpenSessions(1, Profile)[0];

        var tick = 2L;
        for (; tick <= 4; tick++)
        {
            Nudge(harness, tick);
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var creatures = harness.PlanIndex(nameof(ProjCreature));
        Assert.That(harness.Replica(session).NetIds(creatures), Has.Length.EqualTo(1), "the session must hold the mover before anything is deferred");

        // Five statics at once, against a budget of two: three are deferred and nothing remembers them. They are never written again.
        for (var i = 0; i < Statics; i++)
        {
            Spawn(harness, 1f + i, 0f, speed: 1f);
        }

        for (; tick <= 16; tick++)
        {
            Nudge(harness, tick);
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        Assert.That(harness.Replica(session).NetIds(creatures), Has.Length.EqualTo(Statics + 1),
            $"the session holds {harness.Replica(session).NetIds(creatures).Length} of the {Statics + 1} entities there are. The enter budget deferred some "
            + "of them, they stopped changing, and the gather never probed them again — so they are never sent, and an entity that does not change has "
            + "nothing to change tomorrow either");
    }

    private static void Nudge(FrameHarness harness, long tick)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
                var bounds = cluster.GetSpan(ProjCreature.Bounds);
#pragma warning restore TYPHON009
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (bounds[slot].Speed < 9f)
                    {
                        // The statics never move: they are the entities that must arrive without any change of their own.
                        continue;
                    }

                    var x = tick % 16 * 0.5f;
                    cluster.WriteSpatial(
                        ProjCreature.Bounds,
                        slot,
                        new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = 0f, MaxX = x + 1f, MaxY = 1f }, Speed = 9f });
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static void Spawn(FrameHarness harness, float x, float y, float speed)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 1f, MaxY = y + 1f }, Speed = speed };
        var ai = new ProjAi { Template = 5, Level = 11, Mode = ProjAiMode.Idle };
        var vitals = new ProjVitals { Health = 6, MaxHealth = 10 };
        tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        tx.Commit();
        harness.Engine.WriteTickFence(1);
    }
}
