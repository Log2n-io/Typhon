using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Typhon.Client;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// Group sets over the push path (14 § 3, § 3.8): a session whose Sphere meets a town holds every member of it, is told so once, and gives the town back.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupTests : TestBase<PushGroupTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;

    private static SubscriptionsOptions Options(int enterBudget = 500) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 8,
        EnterBudgetPerFrame = enterBudget,
        PushShadow = true,
    };

    private FrameHarness Harness(DatabaseEngine dbe, string name, int enterBudget = 500)
    {
        var harness = FrameHarness.Create(dbe, s => GroupTestSchema.Declare(s, Radius, Reach, Margin), name, Options(enterBudget));
        harness.RunFence = true;
        return harness;
    }

    private static EntityId Town(DatabaseEngine dbe, int key, float x, float y, float half)
    {
        using var tx = dbe.CreateQuickTransaction();
        var town = tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, half) }));
        tx.Commit();
        return town;
    }

    private static List<EntityId> Buildings(DatabaseEngine dbe, int key, float x, float y, int count, int template = 1)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = GroupTestSchema.At(x + (i % 8 * 6f), y + (i / 8 * 6f));
            var ai = new ProjAi { Template = (byte)(template + i) };
            ids.Add(tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(in ai),
                GroupBuilding.Key.Set(new GroupKey { Town = key })));
        }

        tx.Commit();
        return ids;
    }

    private static int BuildingPlan(FrameHarness harness) => harness.CatalogPlan.ArchetypeByName(nameof(GroupBuilding)).Idx;

    private static void Run(FrameHarness harness, ref long tick, int ticks, params SessionId[] sessions)
    {
        for (var t = 0; t < ticks; t++)
        {
            tick++;
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }
    }

    [Test]
    [VerifiesRule("SUB-33")]
    public void ASessionMeetingATownHoldsEveryBuildingOfItAndIsToldOnce()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Town(dbe, 9, 1500f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 30);
        Buildings(dbe, 9, 1460f, -40f, 30, template: 100);
        Buildings(dbe, 0, -40f, 40f, 5, template: 200);

        using var harness = Harness(dbe, nameof(ASessionMeetingATownHoldsEveryBuildingOfItAndIsToldOnce));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);

        var tick = 0L;
        var completions = 0;
        for (var t = 0; t < 6; t++)
        {
            tick++;
            harness.RunTick(tick);

            // The store's list is the last frame's: read only when a frame arrived.
            if (harness.Deliver(session) > 0)
            {
                completions += harness.Replica(session).Store.Groups.Count(g => g.Kind == SourceStatus.GroupComplete && g.Key == 7);
            }
        }

        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(30), "town 7's buildings, not town 9's nor the keyless");
            Assert.That(store.IsGroupComplete(0, 7), Is.True);
            Assert.That(store.IsGroupComplete(0, 9), Is.False);
            Assert.That(completions, Is.EqualTo(1), "a key is completed once per hold");
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// The oracle (step 4.1's acceptance): towns met and left by walking, teleporting, skipping sessions while their members change key, spawn, die and
    /// teleport — every session's store is what it is entitled to at each quiet point, every met town is complete, and no completion arrives early.
    /// </summary>
    /// <param name="seed">The run's seed.</param>
    /// <param name="enterBudget">A budget small enough that a town fills over several frames, or ample.</param>
    [TestCase(1, 16)]
    [TestCase(2, 16)]
    [TestCase(3, 500)]
    [TestCase(4, 7)]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-16")]
    [VerifiesRule("SUB-03")]
    [VerifiesRule("SUB-01")]
    public void SessionsHoldExactlyTheTownsTheyMeet(int seed, int enterBudget)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var oracle = GroupOracle.Create(dbe, seed, [0, 30, 70, 90], nameof(SessionsHoldExactlyTheTownsTheyMeet) + seed, enterBudget);
        for (var round = 0; round < 8; round++)
        {
            for (var step = 0; step < 30; step++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged($"seed {seed}, budget {enterBudget}, round {round}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Compared, Is.GreaterThan(200), "the comparison looked at members");
            Assert.That(oracle.RequiredKeys, Is.GreaterThan(5), "sessions met towns");
            Assert.That(oracle.CompletionsChecked, Is.GreaterThan(3), "completions arrived and were checked against the world");
            Assert.That(oracle.Releases, Is.GreaterThan(0), "towns were released");
            Assert.That(oracle.KeyChanges, Is.GreaterThan(10), "members changed town through the tap alone");
            Assert.That(oracle.ResetsDroppingGroups, Is.GreaterThan(0), "a RESET dropped held towns, which were filled and completed again");
            Assert.That(oracle.AnswersChecked, Is.GreaterThan(3), "SubscribeRequests were answered, and each answer checked");
            Assert.That(oracle.RequiredLists, Is.GreaterThan(0), "sessions held lists they asked for");
        });
    }

    /// <summary>
    /// The oracle with masks (step 4.4's acceptance, 14 § 6): the same world, its members tagged — most public, the rest under one of two tags — and the
    /// tags changing under the churn, with sessions that see public only, one tag, or both, now and then switching. At every quiet point a session holds
    /// exactly what it is entitled to and sees; every completion the skip-free sessions receive holds every member they see; no record is ever illegal.
    /// </summary>
    [TestCase(11, 16)]
    [TestCase(12, 500)]
    [TestCase(13, 7)]
    [VerifiesRule("SUB-34")]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-03")]
    public void SessionsHoldExactlyWhatTheySee(int seed, int enterBudget)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var oracle = GroupOracle.Create(dbe, seed, [0, 0, 0, 30, 70, 90], nameof(SessionsHoldExactlyWhatTheySee) + seed, enterBudget, tags: true);
        for (var round = 0; round < 8; round++)
        {
            for (var step = 0; step < 30; step++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged($"seed {seed}, budget {enterBudget}, round {round}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Compared, Is.GreaterThan(200), "the comparison looked at members");
            Assert.That(oracle.HiddenMembers, Is.GreaterThan(100), "members were hidden from sessions, and each checked not held");
            Assert.That(oracle.MaskFlips, Is.GreaterThan(20), "members changed their masks");
            Assert.That(oracle.SessionMaskChanges, Is.GreaterThan(0), "a session's own mask changed");
            Assert.That(oracle.CompletionsChecked, Is.GreaterThan(3), "completions arrived and were checked against what each session sees");
            Assert.That(oracle.Push.VisibilityResets, Is.GreaterThan(0), "a session mask change reset its view");
        });
    }

    /// <summary>What one frame told a session about town <paramref name="key"/>, and how many buildings entered and left in it.</summary>
    private static (int Completed, int Released, int Entered, int Left) Frame(FrameHarness harness, SessionId session, long key)
    {
        var store = harness.Replica(session).Store;
        var buildings = store.Archetypes[BuildingPlan(harness)];
        return (store.Groups.Count(g => g.Kind == SourceStatus.GroupComplete && g.Key == key),
            store.Groups.Count(g => g.Kind == SourceStatus.GroupReleased && g.Key == key), buildings.EnteredCount, buildings.LeftCount);
    }

    /// <summary>Runs a tick and delivers; the frame's tallies, or zeros when no frame was produced.</summary>
    private static (int Completed, int Released, int Entered, int Left) Tick(FrameHarness harness, SessionId session, ref long tick, long key)
    {
        tick++;
        harness.RunTick(tick);
        return harness.Deliver(session) > 0 ? Frame(harness, session, key) : default;
    }

    /// <summary>
    /// P6's hysteresis and re-entry: beyond the reach but within the margin the town is kept, beyond the margin it is released with its buildings, and met
    /// again it is filled and completed again.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ATownIsKeptInsideTheMarginReleasedBeyondItAndCompletedAgainOnReturn()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 30f);
        Buildings(dbe, 7, -20f, -20f, 12);

        using var harness = Harness(dbe, nameof(ATownIsKeptInsideTheMarginReleasedBeyondItAndCompletedAgainOnReturn));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        var x = 0d;
        harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
        var tick = 0L;
        var completed = 0;
        var released = 0;
        for (var t = 0; t < 4; t++)
        {
            completed += Tick(harness, session, ref tick, 7).Completed;
        }

        Assert.That(completed, Is.EqualTo(1));

        // Walk out by steps under a cell, to the middle of the band between reach and reach + margin: kept.
        while (x < 30d + Reach + (Margin / 2d))
        {
            x += 8d;
            harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
            var f = Tick(harness, session, ref tick, 7);
            completed += f.Completed;
            released += f.Released;
        }

        for (var t = 0; t < 3; t++)
        {
            released += Tick(harness, session, ref tick, 7).Released;
        }

        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(released, Is.Zero, "inside the margin the town is kept");
            Assert.That(store.IsGroupComplete(0, 7), Is.True);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(12));
        });

        // On beyond the margin: released, and every building leaves in that frame.
        var left = 0;
        while (x < 30d + Reach + Margin + 20d)
        {
            x += 8d;
            harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
            var f = Tick(harness, session, ref tick, 7);
            released += f.Released;
            left += f.Left;
            if (f.Released > 0)
            {
                Assert.That(f.Left, Is.EqualTo(12), "a release carries its members' leaves in its own frame");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(left, Is.EqualTo(12));
            Assert.That(store.IsGroupComplete(0, 7), Is.False);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Is.Empty);
        });

        // Back in: filled and completed again (SWG's requirement: hysteresis only delays the leave).
        while (x > 0d)
        {
            x -= 8d;
            harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
            completed += Tick(harness, session, ref tick, 7).Completed;
        }

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.EqualTo(2), "completed once per hold");
            Assert.That(store.IsGroupComplete(0, 7), Is.True);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(12));
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>P8: a town larger than the enter budget fills over several frames, never more than the budget a frame, and completes with its last member.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ATownLargerThanTheBudgetFillsOverFramesAndCompletesWithItsLastMember()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 30);

        using var harness = Harness(dbe, nameof(ATownLargerThanTheBudgetFillsOverFramesAndCompletesWithItsLastMember), enterBudget: 7);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var frames = new List<(int Completed, int Entered)>();
        for (var t = 0; t < 10; t++)
        {
            var f = Tick(harness, session, ref tick, 7);
            frames.Add((f.Completed, f.Entered));
        }

        var completedAt = frames.FindIndex(f => f.Completed > 0);
        Assert.Multiple(() =>
        {
            Assert.That(frames.Max(f => f.Entered), Is.LessThanOrEqualTo(7), "the budget bounds a frame's enters");
            Assert.That(completedAt, Is.GreaterThanOrEqualTo(4), "30 members at 7 a frame take five frames");
            Assert.That(frames.Take(completedAt + 1).Sum(f => f.Entered), Is.EqualTo(30), "completed in the frame carrying the last member");
            Assert.That(frames[completedAt].Entered, Is.GreaterThan(0), "the completing frame carries members");
            Assert.That(frames.Take(completedAt).Sum(f => f.Entered), Is.LessThan(30), "and not a frame after the last one");
            Assert.That(frames.Sum(f => f.Completed), Is.EqualTo(1));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// P11: a RESET in the middle of a fill — the session teleports across its town, a cell or more — drops what the client holds; the town is filled again
    /// from its first member and completed once, after every member entered again.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-03")]
    public void AResetInTheMiddleOfAFillFillsTheTownAgainAndCompletesItOnce()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 30);

        using var harness = Harness(dbe, nameof(AResetInTheMiddleOfAFillFillsTheTownAgainAndCompletesItOnce), enterBudget: 7);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(-50d, 0d, 0d));
        var tick = 0L;
        var before = 0;
        for (var t = 0; t < 2; t++)
        {
            var f = Tick(harness, session, ref tick, 7);
            before += f.Entered;
            Assert.That(f.Completed, Is.Zero, "two frames of seven cannot complete thirty");
        }

        Assert.That(before, Is.InRange(1, 29), "the fill is under way");
        var resetsBefore = harness.Subscriptions.Push.GroupResetsMidFill;

        // 100 m inside the town: past a cell, so the session resets, and still in the town.
        harness.Sessions.SetViewpoint(session, new Vector3D(50d, 0d, 0d));
        var entered = 0;
        var completed = 0;
        var sawReset = false;
        for (var t = 0; t < 10; t++)
        {
            tick++;
            harness.RunTick(tick);
            if (harness.Deliver(session) == 0)
            {
                continue;
            }

            var store = harness.Replica(session).Store;
            sawReset |= (store.Flags & Typhon.Protocol.TickFlags.Reset) != 0;
            var f = Frame(harness, session, 7);
            entered += f.Entered;
            completed += f.Completed;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawReset, Is.True, "the teleport reset the session");
            Assert.That(harness.Subscriptions.Push.GroupResetsMidFill, Is.GreaterThan(resetsBefore), "the reset met a key still filling");
            Assert.That(entered, Is.EqualTo(30), "every member entered again after the reset, once");
            Assert.That(completed, Is.EqualTo(1), "and the town completed once");
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(30));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// SUB-33's bound: past MaxGroupsPerSession towns met at once, the ones held are kept and the nearest new ones are added; the rest are counted, and
    /// held as the bound frees.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void PastTheBoundTheHeldTownsAreKeptAndTheNearestAdded()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);

        // Ten towns 4 m wide in a row, all within reach of a session at the origin; the bound is eight.
        for (var k = 0; k < 10; k++)
        {
            Town(dbe, 100 + k, (k * 6f) - 27f, 0f, 2f);
            Buildings(dbe, 100 + k, (k * 6f) - 27f, 0f, 1, template: 10 * k);
        }

        using var harness = Harness(dbe, nameof(PastTheBoundTheHeldTownsAreKeptAndTheNearestAdded));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 0);
        }

        var store = harness.Replica(session).Store;
        var nearest = Enumerable.Range(0, 10).OrderBy(k => System.Math.Abs((k * 6d) - 27d)).Take(8).Select(k => 100L + k).ToHashSet();
        Assert.Multiple(() =>
        {
            Assert.That(store.CompleteGroupCount, Is.EqualTo(8), "the bound");
            Assert.That(store.CompleteGroups.Select(g => g.Key), Is.EquivalentTo(nearest), "the nearest eight");
            Assert.That(harness.Subscriptions.Push.GroupsOverLimit, Is.GreaterThan(0), "the rest counted");
        });

        // Walk west by steps under a cell: the held ones are kept while they stay in reach, whatever is nearer.
        var x = 0d;
        while (x > -12d)
        {
            x -= 4d;
            harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
            Tick(harness, session, ref tick, 0);
        }

        Assert.That(store.CompleteGroups.Select(g => g.Key), Is.EquivalentTo(nearest), "kept first: a nearer town does not evict a held one in reach");
    }

    /// <summary>P9: a member moving between two held towns is an update, never a leave and an enter; moved to a town not held, it leaves.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-16")]
    public void AMemberMovingBetweenHeldTownsStaysAndLeavesForATownNotHeld()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var seven = Town(dbe, 7, -40f, 0f, 30f);
        Town(dbe, 9, 40f, 0f, 30f);
        Town(dbe, 11, 3000f, 0f, 30f);
        var building = Buildings(dbe, 7, -50f, 0f, 1)[0];

        using var harness = Harness(dbe, nameof(AMemberMovingBetweenHeldTownsStaysAndLeavesForATownNotHeld));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        var netId = harness.NetIdOf(building);
        Assert.That(harness.Replica(session).Store.TryLocate(netId, out _, out _), Is.True);

        SetKey(dbe, building, 9);
        var moved = Tick(harness, session, ref tick, 9);
        Assert.Multiple(() =>
        {
            // A static member has no update to send, so the move may produce no frame at all: what is asserted is that nothing left.
            Assert.That(moved.Left, Is.Zero, "7 to 9, both held: no leave");
            Assert.That(moved.Entered, Is.Zero, "and no enter");
            Assert.That(harness.Replica(session).Store.TryLocate(netId, out _, out _), Is.True);
        });

        // Town 7 goes: it is released, and the building, held now by 9 alone, stays.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(seven);
            tx.Commit();
        }

        var released = Tick(harness, session, ref tick, 7);
        Assert.Multiple(() =>
        {
            Assert.That(released.Released, Is.EqualTo(1), "town 7 is released");
            Assert.That(released.Left, Is.Zero, "its former member is held by town 9");
            Assert.That(harness.Replica(session).Store.TryLocate(netId, out _, out _), Is.True);
        });

        SetKey(dbe, building, 11);
        var gone = Tick(harness, session, ref tick, 11);
        Assert.Multiple(() =>
        {
            Assert.That(gone.Left, Is.EqualTo(1), "to a town nobody here holds: it leaves");
            Assert.That(harness.Replica(session).Store.TryLocate(netId, out _, out _), Is.False);
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>A town that is destroyed is released, and its buildings, still carrying its key, leave.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ADestroyedTownIsReleasedWithItsBuildings()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        EntityId town;
        using (var tx = dbe.CreateQuickTransaction())
        {
            town = tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 7, Extent = GroupTestSchema.Square(0f, 0f, 30f) }));
            tx.Commit();
        }

        Buildings(dbe, 7, -20f, -20f, 6);
        using var harness = Harness(dbe, nameof(ADestroyedTownIsReleasedWithItsBuildings));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.That(harness.Replica(session).Store.IsGroupComplete(0, 7), Is.True);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(town);
            tx.Commit();
        }

        var f = Tick(harness, session, ref tick, 7);
        Assert.Multiple(() =>
        {
            Assert.That(f.Released, Is.EqualTo(1));
            Assert.That(f.Left, Is.EqualTo(6));
            Assert.That(harness.Replica(session).Store.IsGroupComplete(0, 7), Is.False);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Is.Empty);
        });
    }

    private static void SetKey(DatabaseEngine dbe, EntityId entity, int key)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(entity).Set(GroupBuilding.Key, new GroupKey { Town = key });
        tx.Commit();
    }

    /// <summary>
    /// The oracle above against a push path with the tap removed (SUB-33): a member's key written through EntityRefMut reaches the index and never the
    /// projection, so its town's fill and its cold entry disagree. The oracle must reject it.
    /// </summary>
    [Test]
    [RuleMutant("SUB-33")]
    public void AKeyChangeTheProjectionNeverReadsIsDetected()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var oracle = GroupOracle.Create(dbe, 4, [0, 30, 70, 90], nameof(AKeyChangeTheProjectionNeverReadsIsDetected), 7);
        oracle.RemoveTheTapForTest();
        RuleMutants.AssertDetects("SUB-33", GroupOracle.FailureMarker, () =>
        {
            for (var round = 0; round < 8; round++)
            {
                for (var step = 0; step < 30; step++)
                {
                    oracle.Step();
                }

                oracle.Quiesce();
                oracle.AssertConverged($"round {round}");
            }
        });
    }

    /// <summary>
    /// The tagged oracle against a projection that never reads a mask flip (SUB-34): members change their masks and replication is not told, so what the
    /// sessions hold follows the old masks. The oracle must reject it.
    /// </summary>
    [Test]
    [RuleMutant("SUB-34")]
    public void AMaskFlipTheProjectionNeverReadsIsDetected()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var oracle = GroupOracle.Create(dbe, 11, [0, 30, 70, 90], nameof(AMaskFlipTheProjectionNeverReadsIsDetected), 16, tags: true);
        oracle.FlipMasksUntoldForTest();
        RuleMutants.AssertDetects("SUB-34", GroupOracle.FailureMarker, () =>
        {
            for (var round = 0; round < 8; round++)
            {
                for (var step = 0; step < 30; step++)
                {
                    oracle.Step();
                }

                oracle.Quiesce();
                oracle.AssertConverged($"round {round}");
            }
        });
    }

    /// <summary>The catalog names the set (14 § 3.5): its index is what a group record carries, with its key's integer type and its member archetypes.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void TheCatalogListsTheSetWithItsKeyTypeAndMembers()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(TheCatalogListsTheSetWithItsKeyTypeAndMembers));
        var plan = harness.CatalogPlan;
        int[] members = [plan.ArchetypeByName(nameof(GroupBuilding)).Idx, plan.ArchetypeByName(nameof(GroupCitizen)).Idx];
        System.Array.Sort(members);
        Assert.That(plan.GroupSets, Has.Length.EqualTo(1));
        var set = plan.GroupSets[0];
        Assert.Multiple(() =>
        {
            Assert.That(set.Idx, Is.Zero);
            Assert.That(set.Name, Is.EqualTo(GroupTestSchema.Set));
            Assert.That(set.Selector, Is.EqualTo(CatalogGroupSet.RegionSelector));
            Assert.That(set.Key, Is.EqualTo("i32"), "the region key's type");
            Assert.That(set.Members, Is.EqualTo(members));
        });
    }

    // ══ Declarations the engine refuses at Start ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A refusal the compiler makes against the schema, when the runtime is built.</summary>
    private void Refused(System.Action<SubscriptionsRegistry> declare, string contains)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var thrown = Assert.Catch(() => FrameHarness.Create(dbe, declare, TestContext.CurrentContext.Test.Name, Options()).Dispose());
        Assert.That(thrown.ToString(), Does.Contain(contains));
    }

    /// <summary>A refusal that needs no schema, made when Start freezes the registry, before any runtime is built.</summary>
    private static void RefusedAtFreeze(System.Action<SubscriptionsRegistry> declare, string contains, SubscriptionsOptions options = null)
    {
        var registry = new SubscriptionsRegistry(options ?? Options());
        declare(registry);
        var thrown = Assert.Catch(registry.Freeze);
        Assert.That(thrown.Message, Does.Contain(contains));
    }

    private static void Members(SubscriptionsRegistry s) => s.Static<GroupBuilding>(a => a.Position(GroupBuilding.Bounds));

    private static void TownSet(SubscriptionsRegistry s) =>
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town)));

    [Test]
    [VerifiesRule("SUB-33")]
    public void ASetWithoutItsPerSessionBoundIsRefused() =>
        RefusedAtFreeze(s => GroupTestSchema.Declare(s, Radius, Reach, Margin), "MaxGroupsPerSession is not set", new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        });

    [Test]
    [VerifiesRule("SUB-33")]
    public void GroupsOnAWorldObserverAreRefused() => RefusedAtFreeze(s =>
    {
        Members(s);
        TownSet(s);
        s.Profile("w", p => p.World().Of<GroupBuilding>().Groups("town", 10));
    }, "on a World observer");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AnUndeclaredSetIsRefused() => RefusedAtFreeze(s =>
    {
        Members(s);
        TownSet(s);
        s.Profile("p", p => p.Sphere(Radius).Of<GroupBuilding>().Groups("city", 10));
    }, "'city', which is not declared");

    [Test]
    [VerifiesRule("SUB-33")]
    public void ASetWithoutARegionIsRefused() => RefusedAtFreeze(s =>
    {
        Members(s);
        s.Groups("town", g => g.Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town)));
    }, "declares no selector");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AKeyWhoseIndexIsNotSparseIsRefused() => Refused(s =>
    {
        s.Static<GroupDenseMember>(a => a.Position(GroupDenseMember.Bounds));
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupDenseMember>(m => m.Key(GroupDenseMember.Key, k => k.Town)));
    }, "needs [Index(AllowMultiple = true, Sparse = true)]");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AKeyOnATransientComponentIsRefused() => Refused(s =>
    {
        s.Static<GroupTransientMember>(a => a.Position(GroupTransientMember.Bounds));
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupTransientMember>(m => m.Key(GroupTransientMember.Key, k => k.Town)));
    }, "on a Transient component");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AMemberWithoutAProjectionIsRefused() => Refused(s =>
    {
        ProjectionTestSchema.DeclareRock(s);
        TownSet(s);
    }, "which declares no projection");

    [Test]
    [VerifiesRule("SUB-33")]
    public void GroupsOnAClientRegionObserverAreRefused() => RefusedAtFreeze(s =>
    {
        Members(s);
        TownSet(s);
        s.Profile("r", p => p.ClientRegion(200).Of<GroupBuilding>().Groups("town", 10));
    }, "on a ClientRegion observer");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AProfileNamingASetWhenNoneIsDeclaredIsRefused() => RefusedAtFreeze(s =>
    {
        Members(s);
        s.Profile("p", p => p.Sphere(Radius).Of<GroupBuilding>().Groups("town", 10));
    }, "which is not declared");

    [Test]
    [VerifiesRule("SUB-33")]
    public void ASetWithNoMemberIsRefused() => RefusedAtFreeze(s =>
    {
        Members(s);
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent)));
    }, "names no member archetype");

    [Test]
    [VerifiesRule("SUB-33")]
    public void ASetDeclaredTwiceIsRefused()
    {
        var registry = new SubscriptionsRegistry(Options());
        TownSet(registry);
        var thrown = Assert.Catch(() => TownSet(registry));
        Assert.That(thrown.Message, Does.Contain("is already declared"));
    }

    [Test]
    [VerifiesRule("SUB-33")]
    public void ARegionKeyThatIsNotAnIntegerIsRefused() => Refused(s =>
    {
        Members(s);
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Extent).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town)));
    }, "not an integer");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AMemberKeyThatIsNotAnIntegerIsRefused() => Refused(s =>
    {
        Members(s);
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupBuilding>(m => m.Key(GroupBuilding.Bounds, b => b.Speed)));
    }, "A group key is an integer field");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AMemberOfFiveSetsIsRefused() => Refused(s =>
    {
        Members(s);
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            s.Groups(name, g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town)));
        }
    }, "is a member of 5 group sets");

    [Test]
    [VerifiesRule("SUB-33")]
    public void APositionlessArchetypeInAnObserverIsRefused() => Refused(s =>
    {
        s.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
        s.Profile("p", p => p.Sphere(50).Of<GroupPositionless>().Groups("town", 10));
    }, "has no position and a profile's observer names it");

    [Test]
    [VerifiesRule("SUB-33")]
    public void AnExtentThatIsNotAShapeIsRefused() => Refused(s =>
    {
        Members(s);
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Key))
            .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town)));
    }, "An extent is an AABB2F");

    /// <summary>
    /// SUB-03 with groups: a catch-up frame decides an entity's geometry from the whole window, whatever key bucket its events were filed under. A citizen
    /// held by its town moves to a town nobody holds and, a tick later, into the sphere — both while the session is skipped; it must stay held.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-03")]
    [VerifiesRule("SUB-33")]
    public void ACatchUpKeepsAMemberThatLeftItsTownForTheSphereInTheWindow()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 30f);
        Town(dbe, 9, 3000f, 0f, 30f);
        EntityId walker;
        EntityId filler;
        using (var tx = dbe.CreateQuickTransaction())
        {
            // The walker: 300 m out, past the 120 m sphere, held through town 7 alone. The filler: inside the sphere, moved every tick so frames are made.
            walker = tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(GroupTestSchema.At(300f, 0f)), GroupCitizen.Ai.Set(new ProjAi { Template = 1 }),
                GroupCitizen.Key.Set(new GroupKey { Town = 7 }));
            filler = tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(GroupTestSchema.At(10f, 0f)), GroupCitizen.Ai.Set(new ProjAi { Template = 2 }),
                GroupCitizen.Key.Set(default));
            tx.Commit();
        }

        using var harness = Harness(dbe, nameof(ACatchUpKeepsAMemberThatLeftItsTownForTheSphereInTheWindow));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        var netId = harness.NetIdOf(walker);
        Assert.That(harness.Replica(session).Store.TryLocate(netId, out _, out _), Is.True, "held through town 7");

        // Two frames left undelivered fill the session's slots; the two ticks after are skipped, and they carry the walker's moves.
        var x = 10f;
        void MoveFiller()
        {
            x += 3f;
            Teleport(dbe, filler, x, 0f);
        }

        for (var t = 0; t < 2; t++)
        {
            MoveFiller();
            harness.RunTick(++tick);
        }

        var catchUpsBefore = harness.Subscriptions.Push.LogCatchUps;
        SetCitizenKey(dbe, walker, 9);
        MoveFiller();
        harness.RunTick(++tick);
        Teleport(dbe, walker, 40f, 0f);
        MoveFiller();
        harness.RunTick(++tick);

        harness.Deliver(session);
        for (var t = 0; t < 3; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Push.LogCatchUps, Is.GreaterThan(catchUpsBefore), "the session caught up from the log");
            Assert.That(harness.Replica(session).Store.TryLocate(netId, out _, out _), Is.True, "inside the sphere now: geometry holds it");
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>A member of two sets is held while either holds it, and leaves only when neither does (P9, multi-set).</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-16")]
    public void AMemberOfTwoSetsIsHeldWhileEitherHoldsIt()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using (var tx = dbe.CreateQuickTransaction())
        {
            // The town spans x in [-100, 100]; the district a small square at its west end. The resident lives in both.
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 7, Extent = new AABB2F { MinX = -100, MinY = -20, MaxX = 100, MaxY = 20 } }));
            tx.Spawn<GroupDistrictRegion>(GroupDistrictRegion.Zone.Set(new GroupZone { Key = 50, Extent = GroupTestSchema.Square(-90f, 0f, 10f) }));
            tx.Spawn<GroupResident>(GroupResident.Bounds.Set(GroupTestSchema.At(-90f, 0f)), GroupResident.Ai.Set(new ProjAi { Template = 3 }),
                GroupResident.Key.Set(new GroupKey { Town = 7 }), GroupResident.District.Set(new GroupDistrictKey { District = 50 }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Static<GroupResident>(a => a.Position(GroupResident.Bounds).Field(GroupResident.Ai, x => x.Template, Codec.U8, name: "kind"));
            s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupResident>(m => m.Key(GroupResident.Key, k => k.Town)));
            s.Groups("district", g => g.Region<GroupDistrictRegion>(r => r.Key(GroupDistrictRegion.Zone, z => z.Key).Extent(GroupDistrictRegion.Zone, z => z.Extent))
                .Member<GroupResident>(m => m.Key(GroupResident.District, k => k.District)));
            s.Profile("p", p => p.Sphere(30).Groups("town", 5).Groups("district", 5));
        }, nameof(AMemberOfTwoSetsIsHeldWhileEitherHoldsIt), Options());
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "p")[0];
        var plan = harness.CatalogPlan.ArchetypeByName(nameof(GroupResident)).Idx;
        var town = harness.CatalogPlan.GroupSetIndex("town");
        var district = harness.CatalogPlan.GroupSetIndex("district");
        var tick = 0L;
        var store = harness.Replica(session).Store;

        void Walk(double from, double to)
        {
            var step = Math.Sign(to - from) * 8d;
            for (var x = from; Math.Abs(to - x) > 1e-9; x = Math.Abs(to - x) < Math.Abs(step) ? to : x + step)
            {
                harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
                harness.RunTick(++tick);
                harness.Deliver(session);
            }

            harness.Sessions.SetViewpoint(session, new Vector3D(to, 0d, 0d));
            for (var t = 0; t < 3; t++)
            {
                harness.RunTick(++tick);
                harness.Deliver(session);
            }
        }

        harness.Sessions.SetViewpoint(session, new Vector3D(-90d, 0d, 0d));
        for (var t = 0; t < 3; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(store.IsGroupComplete((uint)town, 7) && store.IsGroupComplete((uint)district, 50), Is.True, "both met");
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(1));
        });

        // East along the town, out of the district's reach: the district goes, the town keeps the resident.
        Walk(-90d, 60d);
        Assert.Multiple(() =>
        {
            Assert.That(store.IsGroupComplete((uint)district, 50), Is.False, "the district is released");
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(1), "the town still holds it");
        });

        // North, out of the town too: nothing holds it.
        Walk(0d, 0d);
        harness.Sessions.SetViewpoint(session, new Vector3D(60d, 0d, 0d));
        for (var y = 0d; y <= 80d; y += 8d)
        {
            harness.Sessions.SetViewpoint(session, new Vector3D(60d, y, 0d));
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(store.CompleteGroupCount, Is.Zero, "neither set holds a key");
            Assert.That(harness.Replica(session).NetIds(plan), Is.Empty, "and the resident left");
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>Two regions with one key are one group: it fills once and completes once (B4 of the 4.1 review).</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void TwoRegionsWithOneKeyAreOneGroup()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 30f);
        Town(dbe, 7, 20f, 0f, 30f);
        Buildings(dbe, 7, -20f, -20f, 12);

        using var harness = Harness(dbe, nameof(TwoRegionsWithOneKeyAreOneGroup));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(10d, 0d, 0d));
        var tick = 0L;
        var completed = 0;
        var entered = 0;
        for (var t = 0; t < 5; t++)
        {
            var f = Tick(harness, session, ref tick, 7);
            completed += f.Completed;
            entered += f.Entered;
        }

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(entered, Is.EqualTo(12));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>In a deep grid a 2D extent lies on the plane z = 0, and a session above it meets it there (B5 of the 4.1 review).</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ADeepGridSessionAboveATwoDimensionalTownMeetsIt()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider, deep: true);
        Town(dbe, 7, 0f, 0f, 30f);
        Buildings(dbe, 7, -20f, -20f, 6);

        using var harness = Harness(dbe, nameof(ADeepGridSessionAboveATwoDimensionalTownMeetsIt));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];

        // 300 m up: past every cell the plane z = 0 is filed under, and the extent test reads a 2D extent's distance in the plane.
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 300d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).Store.IsGroupComplete(0, 7), Is.True);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(6));
        });
    }

    private static void Teleport(DatabaseEngine dbe, EntityId entity, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<GroupCitizen>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (cluster.GetEntityId(slot) == entity)
                    {
                        cluster.WriteSpatial(GroupCitizen.Bounds, slot, GroupTestSchema.At(x, y));
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static void SetCitizenKey(DatabaseEngine dbe, EntityId entity, int key)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(entity).Set(GroupCitizen.Key, new GroupKey { Town = key });
        tx.Commit();
    }

    /// <summary>
    /// A key on a Versioned component: its commit copies the HEAD into the cluster slot and marks it (the tap), and moves the index entry, before the
    /// fence. A positioned member's archetype has a spatial field, so no off-DAG commit can write it (EW-02) and the gather reads the index the projection
    /// saw: spawns, moves between held towns and moves out behave as a SingleVersion key's do.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AVersionedKeyMovesItsMemberLikeASingleVersionOne()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, -40f, 0f, 30f);
        Town(dbe, 9, 40f, 0f, 30f);
        Town(dbe, 11, 3000f, 0f, 30f);
        var members = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 3; i++)
            {
                members.Add(tx.Spawn<GroupVersionedMember>(GroupVersionedMember.Bounds.Set(GroupTestSchema.At(-60f + (i * 6f), 0f)),
                    GroupVersionedMember.Key.Set(new GroupVersionedKey { Town = 7 })));
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Static<GroupVersionedMember>(a => a.Position(GroupVersionedMember.Bounds));
            s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupVersionedMember>(m => m.Key(GroupVersionedMember.Key, k => k.Town)));

            // A sphere too small to reach any member: whatever the session holds, it holds through a town.
            s.Profile("p", p => p.Sphere(20).Groups("town", Reach));
        }, nameof(AVersionedKeyMovesItsMemberLikeASingleVersionOne), Options());
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "p")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var plan = harness.CatalogPlan.ArchetypeByName(nameof(GroupVersionedMember)).Idx;
        var replica = harness.Replica(session);
        var tick = 0L;

        void Run(int ticks)
        {
            for (var t = 0; t < ticks; t++)
            {
                harness.RunTick(++tick);
                harness.Deliver(session);
            }
        }

        void SetKey(EntityId member, int key)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.OpenMut(member).Set(GroupVersionedMember.Key, new GroupVersionedKey { Town = key });
            tx.Commit();
        }

        Run(4);
        Assert.Multiple(() =>
        {
            Assert.That(replica.Store.IsGroupComplete(0, 7) && replica.Store.IsGroupComplete(0, 9), Is.True, "both towns met");
            Assert.That(replica.NetIds(plan), Has.Length.EqualTo(3), "town 7's members, through the town alone");
        });

        var stays = harness.NetIdOf(members[0]);
        var goes = harness.NetIdOf(members[1]);
        SetKey(members[0], 9);
        SetKey(members[1], 11);
        EntityId joined;
        using (var tx = dbe.CreateQuickTransaction())
        {
            joined = tx.Spawn<GroupVersionedMember>(GroupVersionedMember.Bounds.Set(GroupTestSchema.At(40f, 0f)),
                GroupVersionedMember.Key.Set(new GroupVersionedKey { Town = 9 }));
            tx.Commit();
        }

        Run(3);
        Assert.Multiple(() =>
        {
            Assert.That(replica.Store.TryLocate(stays, out _, out _), Is.True, "7 to 9, both held: it stays");
            Assert.That(replica.Store.TryLocate(goes, out _, out _), Is.False, "to a town nobody here holds: it leaves");
            Assert.That(replica.Store.TryLocate(harness.NetIdOf(joined), out _, out _), Is.True, "a member spawned into a held town enters");
            Assert.That(replica.NetIds(plan), Has.Length.EqualTo(3));
            Assert.That(replica.Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    [Test]
    [VerifiesRule("SUB-33")]
    public void AMemberKeyOfAnotherSignednessThanItsRegionKeyIsRefused() => Refused(s =>
    {
        s.Static<GroupUnsignedMember>(a => a.Position(GroupUnsignedMember.Bounds));
        s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupUnsignedMember>(m => m.Key(GroupUnsignedMember.Key, k => k.Town)));
    }, "both signed or both unsigned");

    [Test]
    [VerifiesRule("SUB-33")]
    public void TooManyKeysPerSessionIsRefused() =>
        RefusedAtFreeze(s => GroupTestSchema.Declare(s, Radius, Reach, Margin), "MaxGroupsPerSession = 5000", new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius), MaxGroupsPerSession = 5000,
        });

    /// <summary>The engine's set index is the catalog's: two sets declared out of name order, each record naming the set it belongs to.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void TwoSetsDeclaredOutOfNameOrderCarryTheCatalogsIndices()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 7, Extent = GroupTestSchema.Square(0f, 0f, 30f) }));
            tx.Spawn<GroupDistrictRegion>(GroupDistrictRegion.Zone.Set(new GroupZone { Key = 50, Extent = GroupTestSchema.Square(0f, 0f, 10f) }));
            tx.Spawn<GroupResident>(GroupResident.Bounds.Set(GroupTestSchema.At(0f, 0f)), GroupResident.Ai.Set(new ProjAi { Template = 3 }),
                GroupResident.Key.Set(new GroupKey { Town = 7 }), GroupResident.District.Set(new GroupDistrictKey { District = 50 }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Static<GroupResident>(a => a.Position(GroupResident.Bounds));
            s.Groups("zz-town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupResident>(m => m.Key(GroupResident.Key, k => k.Town)));
            s.Groups("aa-district", g => g.Region<GroupDistrictRegion>(r => r.Key(GroupDistrictRegion.Zone, z => z.Key).Extent(GroupDistrictRegion.Zone, z => z.Extent))
                .Member<GroupResident>(m => m.Key(GroupResident.District, k => k.District)));
            s.Profile("p", p => p.Sphere(30).Groups("zz-town", 5).Groups("aa-district", 5));
        }, nameof(TwoSetsDeclaredOutOfNameOrderCarryTheCatalogsIndices), Options());
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "p")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        for (var t = 1; t <= 3; t++)
        {
            harness.RunTick(t);
            harness.Deliver(session);
        }

        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(harness.CatalogPlan.GroupSetIndex("aa-district"), Is.Zero, "ordinal name order");
            Assert.That(harness.CatalogPlan.GroupSetIndex("zz-town"), Is.EqualTo(1));
            Assert.That(store.IsGroupComplete(0, 50), Is.True, "the district's key under the district's index");
            Assert.That(store.IsGroupComplete(1, 7), Is.True, "the town's under the town's");
            Assert.That(store.IsGroupComplete(0, 7) || store.IsGroupComplete(1, 50), Is.False);
        });
    }
}
