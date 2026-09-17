using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-12 — the <c>World</c> observer: everything of the archetypes it lists, nothing of the archetypes it does not, and nothing that has left the world.
/// </summary>
/// <remarks>
/// <para>
/// <b>Watched is asserted as an equality against occupancy, never as a containment.</b> "Every live entity is marked" and "nothing but a live entity is
/// marked" are the same statement read from two ends, and only the second one fails when a mask is never cleared — which is the failure the prologue exists
/// to prevent and the one a containment assertion would sail straight past.
/// </para>
/// <para>
/// <b>Two passes, because the blocks step is P1-09's.</b> The first pass finds clusters with no block and asks for them; the harness creates them, standing
/// in for the stage that will; the second pass marks. That one-tick lag for a brand-new cluster is the shape the design specifies
/// (<c>foundation/03 § 2.5</c>: the blocks step runs in <c>Project</c>'s <c>Prepare</c>, after <c>Interest</c> has run), not an artefact of the fixture.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class WorldObserverTests : TestBase<WorldObserverTests>
{
    private const int CreatureCount = 300;
    private const int PlayerCount = 90;
    private const int RockCount = 120;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    /// <summary>Three projections; the profile lists two of them, so the third is the control.</summary>
    private static void DeclareTwoOfThree(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile("world", p => p.World().Of<ProjCreature>().Of<ProjPlayer>());
    }

    /// <summary>Spawns the fixture's population and fences once, so the clusters and the spatial index are settled.</summary>
    private static List<EntityId> Populate(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                ids.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(10f + (i % 40), 10f + (i / 40)))));
            }

            for (var i = 0; i < PlayerCount; i++)
            {
                tx.Spawn<ProjPlayer>(ProjPlayer.Bounds.Set(PointAt(600f + (i % 30), 600f + (i / 30))));
            }

            for (var i = 0; i < RockCount; i++)
            {
                tx.Spawn<ProjRock>(ProjRock.Bounds.Set(PointAt(-400f + (i % 20), -400f + (i / 20))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return ids;
    }

    /// <summary>
    /// A <c>World</c> observer over two archetypes marks every live entity of both, and marks nothing else.
    /// </summary>
    [Test]
    public void AWorldObserverMarksEveryLiveEntityWatched()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareTwoOfThree, nameof(AWorldObserverMarksEveryLiveEntityWatched));
        harness.OpenSessions(3, "world");

        var creature = harness.PlanIndex(nameof(ProjCreature));
        var player = harness.PlanIndex(nameof(ProjPlayer));

        // Pass 1: no cluster has a block yet, so nothing can be marked and every cluster is on the new-block list.
        harness.RunPass(tick: 1);
        Assert.That(harness.WatchedSlotCount(creature), Is.Zero, "a cluster with no block cannot carry a watched bit");
        Assert.That(harness.CreateRequestedBlocks(), Is.GreaterThan(0), "the pass asked for no blocks at all");

        // Pass 2: the blocks exist, so the marks land.
        harness.RunPass(tick: 2);

        Assert.Multiple(() =>
        {
            Assert.That(harness.WatchedSlotCount(creature), Is.EqualTo(harness.LiveEntityCount(creature)));
            Assert.That(harness.WatchedSlotCount(player), Is.EqualTo(harness.LiveEntityCount(player)));
            Assert.That(harness.LiveEntityCount(creature), Is.EqualTo(CreatureCount), "the fixture did not spawn what it thinks it did");
            Assert.That(harness.LiveEntityCount(player), Is.EqualTo(PlayerCount));
        });

        harness.AssertWatchedMatchesOccupancy(creature, "a World observer reaches every live entity of a listed archetype");
        harness.AssertWatchedMatchesOccupancy(player, "and of every listed archetype, not just the first");
    }

    /// <summary>
    /// An archetype the profile does not list is never reached: no block is asked for, none is created, and nothing of it is watched.
    /// </summary>
    /// <remarks>
    /// The rock archetype is declared, replicated and populated — it is a projection the engine could serve. What keeps it out of the watched set is the
    /// profile, which is the whole point of SUB-13: per-tick cost follows what clients asked for, not what the database holds.
    /// </remarks>
    [Test]
    public void AnArchetypeTheProfileDoesNotListIsNeverWatched()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareTwoOfThree, nameof(AnArchetypeTheProfileDoesNotListIsNeverWatched));
        harness.OpenSessions(2, "world");

        var rock = harness.PlanIndex(nameof(ProjRock));

        harness.RunPass(tick: 1);
        harness.CreateRequestedBlocks();
        harness.RunPass(tick: 2);

        Assert.Multiple(() =>
        {
            Assert.That(harness.LiveEntityCount(rock), Is.EqualTo(RockCount), "the unlisted archetype has to be populated, or this proves nothing");
            Assert.That(harness.Subscriptions.ReplicationStates[rock].WatchedClusterCount, Is.Zero, "an unlisted archetype takes no blocks at all");
            Assert.That(harness.WatchedSlotCount(rock), Is.Zero);

            foreach (var run in AllRuns(harness))
            {
                Assert.That(run.ArchetypeIndex, Is.Not.EqualTo(rock), "a run named an archetype no profile observes");
            }
        });
    }

    /// <summary>
    /// An entity destroyed between two passes is unmarked by the second — the mask is this tick's answer, not an accumulation.
    /// </summary>
    [Test]
    public void AnEntityThatLeavesTheWorldIsUnmarked()
    {
        using var dbe = SetupEngine();
        var ids = Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareTwoOfThree, nameof(AnEntityThatLeavesTheWorldIsUnmarked));
        harness.OpenSessions(2, "world");

        var creature = harness.PlanIndex(nameof(ProjCreature));

        harness.RunPass(tick: 1);
        harness.CreateRequestedBlocks();
        harness.RunPass(tick: 2);

        var watchedBefore = harness.WatchedSlotCount(creature);
        Assert.That(watchedBefore, Is.EqualTo(CreatureCount));

        // Destroy a third of them. Half the clusters lose entities without draining, which is the case worth testing: a drained cluster loses its block at the
        // drain hook, while a surviving one keeps a mask that has to be narrowed instead.
        const int Destroyed = CreatureCount / 3;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Destroyed; i++)
            {
                tx.Destroy(ids[i * 3]);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        harness.RunPass(tick: 3);

        // A cluster that drained gives its block back at the drain hook, so it is asked for again; create those before the assertion, exactly as a tick would.
        harness.CreateRequestedBlocks();
        harness.RunPass(tick: 4);

        Assert.Multiple(() =>
        {
            Assert.That(harness.LiveEntityCount(creature), Is.EqualTo(CreatureCount - Destroyed));
            Assert.That(harness.WatchedSlotCount(creature), Is.EqualTo(CreatureCount - Destroyed), "a destroyed entity is still marked watched");
        });

        harness.AssertWatchedMatchesOccupancy(creature, "the watched mask is this tick's answer, not last tick's plus this tick's");
    }

    /// <summary>Every run produced by the last pass, across every session.</summary>
    private static List<InterestRun> AllRuns(InterestHarness harness)
    {
        var runs = new List<InterestRun>();
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            foreach (var run in harness.Interest.HitsOf(i))
            {
                runs.Add(run);
            }
        }

        return runs;
    }
}
