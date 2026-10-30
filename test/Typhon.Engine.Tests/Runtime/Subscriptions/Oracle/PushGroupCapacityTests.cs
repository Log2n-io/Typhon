using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// Group sets past the old 1 024 ceiling (#1407): a session holds every key it meets up to its bound, at a cost of the keys it holds; is told how many it left
/// out past the bound; and completes and releases thousands of keys over frames, never more of either in one frame than the enter budget.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupCapacityTests : TestBase<PushGroupCapacityTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;
    private const int Budget = 500;
    private const string Plain = "plain";
    private const string World = "world";

    private static SubscriptionsOptions Options(int maxGroups, int enterBudget, int frameBytes) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 4,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = maxGroups,
        EnterBudgetPerFrame = enterBudget,
        FrameBytes = frameBytes,
        PushShadow = true,
    };

    private static FrameHarness Harness(DatabaseEngine dbe, string name, int maxGroups, int enterBudget = Budget, int frameBytes = 256 * 1024)
    {
        var harness = FrameHarness.Create(dbe, s =>
        {
            GroupTestSchema.Declare(s, Radius, Reach, Margin);
            s.Profile(Plain, p => p.Detection(PushDetection.Explicit).Sphere(Radius).Of<GroupCitizen>());
            s.Profile(World, p => p.World().Of<GroupCitizen>());
        }, name, Options(maxGroups, enterBudget, frameBytes));
        harness.RunFence = true;
        return harness;
    }

    /// <summary>
    /// <paramref name="count"/> towns 1 m wide on a 2 m grid centred on the origin — 64 a row, all within reach of a session there — keyed from 1, each with
    /// <paramref name="buildings"/> buildings at its centre, listed in <paramref name="built"/> town by town.
    /// </summary>
    private static List<EntityId> Towns(DatabaseEngine dbe, int count, int buildings, out List<EntityId> built)
    {
        var towns = new List<EntityId>(count);
        built = new List<EntityId>(count * buildings);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            towns.Add(SpawnTown(tx, i + 1, count));
            var (x, y) = SpotOf(i, count);
            for (var b = 0; b < buildings; b++)
            {
                var bounds = GroupTestSchema.At(x, y);
                var ai = new ProjAi { Template = (byte)i };
                built.Add(tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(in ai),
                    GroupBuilding.Key.Set(new GroupKey { Town = i + 1 })));
            }
        }

        tx.Commit();
        return towns;
    }

    private static EntityId SpawnTown(Transaction tx, int key, int count)
    {
        var (x, y) = SpotOf(key - 1, count);
        return tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, 0.5f) }));
    }

    private static (float X, float Y) SpotOf(int i, int count)
    {
        var rows = (count + 63) / 64;
        return ((i % 64 * 2f) - 63f, (i / 64 * 2f) - rows + 1f);
    }

    private static int BuildingPlan(FrameHarness harness) => harness.CatalogPlan.ArchetypeByName(nameof(GroupBuilding)).Idx;

    /// <summary>What the frames of one session said, frame by frame.</summary>
    private sealed class Tally
    {
        public readonly Dictionary<long, int> Completions = [];
        public readonly Dictionary<long, int> Releases = [];
        public readonly List<uint> Truncations = [];
        public int MostCompletedInAFrame;
        public int MostReleasedInAFrame;
        public int MostEnteredInAFrame;
        public int Entered;
        public int Left;
        public int Frames;
        public int Resets;

        /// <summary>Completions whose town's building was not in the store when the frame completing it was applied — a completion sent early.</summary>
        public int EarlyCompletions;
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> ticks; each delivers its frames one by one into <paramref name="tally"/> unless <paramref name="read"/> is false —
    /// the client not reading, so its two frame slots fill and the next frames are gathered and not published. <paramref name="buildingOf"/> (key − 1 →
    /// building) checks each completion against the store.
    /// </summary>
    private static void Run(FrameHarness harness, SessionId session, ref long tick, int ticks, Tally tally, IReadOnlyList<EntityId> buildingOf = null,
        bool read = true)
    {
        for (var t = 0; t < ticks; t++)
        {
            tick++;
            harness.RunTick(tick);
            if (read)
            {
                Drain(harness, session, tally, buildingOf);
            }
        }
    }

    private static unsafe void Drain(FrameHarness harness, SessionId session, Tally tally, IReadOnlyList<EntityId> buildingOf = null)
    {
        var replica = harness.Replica(session);
        ref var send = ref harness.Assembler.SendStateOf(session.Slot);
        while (send.TryClaimFrame(harness.Assembler.Gate.CommittedTick, out var frame))
        {
            replica.Apply(new ReadOnlySpan<byte>(frame.Bytes, frame.Length));
            send.CompleteSend(frame.Sequence);
            Count(harness, replica, tally, buildingOf);
        }
    }

    private static void Count(FrameHarness harness, SessionReplica replica, Tally tally, IReadOnlyList<EntityId> buildingOf)
    {
        var store = replica.Store;
        var buildings = store.Archetypes[BuildingPlan(harness)];
        tally.Frames++;
        tally.Resets += (store.Flags & TickFlags.Reset) != 0 ? 1 : 0;
        tally.Entered += buildings.EnteredCount;
        tally.Left += buildings.LeftCount;
        tally.MostEnteredInAFrame = Math.Max(tally.MostEnteredInAFrame, buildings.EnteredCount);
        var completed = 0;
        var released = 0;
        foreach (var (kind, _, key) in store.Groups)
        {
            switch (kind)
            {
                case SourceStatus.GroupComplete:
                    completed++;
                    tally.Completions[key] = tally.Completions.GetValueOrDefault(key) + 1;
                    if (buildingOf != null && !store.TryLocate(harness.NetIdOf(buildingOf[(int)key - 1]), out _, out _))
                    {
                        tally.EarlyCompletions++;
                    }

                    break;
                case SourceStatus.GroupReleased:
                    released++;
                    tally.Releases[key] = tally.Releases.GetValueOrDefault(key) + 1;
                    break;
                case SourceStatus.GroupTruncated:
                    tally.Truncations.Add((uint)key);
                    break;
            }
        }

        tally.MostCompletedInAFrame = Math.Max(tally.MostCompletedInAFrame, completed);
        tally.MostReleasedInAFrame = Math.Max(tally.MostReleasedInAFrame, released);
    }

    /// <summary>
    /// A citizen at the origin, in every session's Sphere: zigzagged by <see cref="Walk"/> — a path no motion segment predicts — it keeps each frame from
    /// being idle, so frames the client does not read fill its slots.
    /// </summary>
    private static void Walker(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bounds = GroupTestSchema.At(0f, 0f);
        tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(in bounds), GroupCitizen.Ai.Set(new ProjAi { Template = 1 }), GroupCitizen.Key.Set(default));
        tx.Commit();
    }

    private static void Walk(DatabaseEngine dbe, float x)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<GroupCitizen>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                cluster.WriteSpatial(GroupCitizen.Bounds, slot, GroupTestSchema.At(x, 0f));
            }
        }

        tx.Commit();
    }

    private static void Destroy(DatabaseEngine dbe, IEnumerable<EntityId> entities)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in entities)
        {
            tx.Destroy(id);
        }

        tx.Commit();
    }

    /// <summary>
    /// 2 048 towns met at once, past the old ceiling, with a bound of 4 096: every one is held, filled over frames under the budget, completed once, each
    /// completion arriving with its building already in the store — and the session is never told it left one out.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void TwoThousandTownsAreHeldWholeOverSeveralFrames()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe, 2048, 1, out var buildings);
        using var harness = Harness(dbe, nameof(TwoThousandTownsAreHeldWholeOverSeveralFrames), maxGroups: 4096);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var tally = new Tally();

        // ⌈2 048 / 500⌉ frames fill it; two more show nothing moves after.
        Run(harness, session, ref tick, 7, tally, buildings);

        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(store.CompleteGroupCount, Is.EqualTo(2048), "every town met is held and complete");
            Assert.That(tally.Completions.Values.All(c => c == 1), Is.True, "each completed once");
            Assert.That(tally.EarlyCompletions, Is.Zero, "each completion with its building already in the store");
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(2048));
            Assert.That(tally.MostEnteredInAFrame, Is.LessThanOrEqualTo(Budget), "the fill spans frames under the budget");
            Assert.That(tally.MostCompletedInAFrame, Is.LessThanOrEqualTo(Budget));
            Assert.That(tally.Truncations, Is.Empty, "nothing left out, nothing said");
            Assert.That(store.GroupSetOverLimit(0), Is.Zero);
            Assert.That(harness.Subscriptions.Push.GroupsOverLimit, Is.Zero);
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });

        // Its state is what it holds: the committed side fits 2 048 keys, not the 4 096 of the bound.
        Assert.That(harness.Subscriptions.Push.GroupsOf(session).CapacityForTest(0, committed: true), Is.EqualTo(2048));

        // P9 past the old ceiling: a building moving between two held towns is an update — no leave, no enter — and both stay complete.
        var mover = buildings[2000 - 1];
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(mover).Set(GroupBuilding.Key, new GroupKey { Town = 2040 });
            tx.Commit();
        }

        var moved = new Tally();
        Run(harness, session, ref tick, 2, moved);
        Assert.Multiple(() =>
        {
            Assert.That(moved.Left, Is.Zero, "an update, not a leave");
            Assert.That(moved.Entered, Is.Zero, "nor an enter");
            Assert.That(store.TryLocate(harness.NetIdOf(mover), out _, out _), Is.True);
            Assert.That(store.IsGroupComplete(0, 2000) && store.IsGroupComplete(0, 2040), Is.True);
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// A catch-up past the old ceiling, over 1 100 held towns: the client stops reading, its frames go unpublished while buildings move between towns and
    /// a town is destroyed, and the next published frame folds the window — moves as updates, the town released with its building, nothing illegal.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-03")]
    public void ACatchUpPastTheOldCeilingFoldsTheWindow()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var towns = Towns(dbe, 1100, 1, out var buildings);
        Walker(dbe);
        using var harness = Harness(dbe, nameof(ACatchUpPastTheOldCeilingFoldsTheWindow), maxGroups: 4096);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 5, new Tally());
        var store = harness.Replica(session).Store;
        Assert.That(store.CompleteGroupCount, Is.EqualTo(1100), "precondition");

        // Unread: two frames take the slots, the next two are gathered and not published, each tick changing something.
        var skippedBefore = harness.Assembler.FramesSkipped;
        for (var t = 0; t < 4; t++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.OpenMut(buildings[(t * 300) + 1]).Set(GroupBuilding.Key, new GroupKey { Town = (t * 300) + 3 });
                tx.Commit();
            }

            Walk(dbe, t % 2 == 0 ? 6f : -6f);
            Run(harness, session, ref tick, 1, new Tally(), read: false);
        }

        Destroy(dbe, [towns[1099]]);
        Walk(dbe, 9f);
        Run(harness, session, ref tick, 1, new Tally(), read: false);
        Assert.That(harness.Assembler.FramesSkipped, Is.GreaterThan(skippedBefore), "precondition: frames went unpublished");

        var tally = new Tally();
        Drain(harness, session, tally);
        Run(harness, session, ref tick, 2, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Resets, Is.Zero, "a catch-up, not a RESET");
            Assert.That(tally.Releases.Keys, Is.EqualTo(new long[] { 1100 }), "the destroyed town released");
            Assert.That(tally.Left, Is.EqualTo(1), "with its building, and nothing else left");
            Assert.That(store.CompleteGroupCount, Is.EqualTo(1099));
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(1099));
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// Thousands of empty towns met at once: each completion spends a unit of the budget, so no frame carries more GROUP_COMPLETE than the budget. Under an
    /// 8 KiB frame, 3 000 completions in one frame would not fit; 500 do.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ThousandsOfEmptyTownsCompleteUnderTheEnterBudget()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe, 3000, 0, out _);
        using var harness = Harness(dbe, nameof(ThousandsOfEmptyTownsCompleteUnderTheEnterBudget), maxGroups: 4096, frameBytes: 8 * 1024);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var tally = new Tally();
        Run(harness, session, ref tick, 7, tally);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).Store.CompleteGroupCount, Is.EqualTo(3000));
            Assert.That(tally.MostCompletedInAFrame, Is.EqualTo(Budget), "a completion costs a unit of the budget");
            Assert.That(harness.Assembler.OversizeSkips, Is.Zero, "no frame outgrew the frame size");
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// 1 200 towns destroyed at once: their releases spread over frames, at most the budget a frame, each released once with its building's leave; the
    /// keys kept meanwhile are still held and complete.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AMassReleaseIsSpreadOverFramesUnderTheBudget()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var towns = Towns(dbe, 1200, 1, out _);
        using var harness = Harness(dbe, nameof(AMassReleaseIsSpreadOverFramesUnderTheBudget), maxGroups: 2048);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 5, new Tally());
        var store = harness.Replica(session).Store;
        Assert.That(store.CompleteGroupCount, Is.EqualTo(1200), "precondition: every town complete");

        Destroy(dbe, towns);
        var deferredBefore = harness.Subscriptions.Push.GroupReleasesDeferred;
        var tally = new Tally();
        Run(harness, session, ref tick, 1, tally);
        Assert.That(tally.Releases, Has.Count.EqualTo(Budget), "the first frame releases a budget's worth");
        Assert.That(store.CompleteGroupCount, Is.EqualTo(1200 - Budget), "the rest still held, still complete");

        Run(harness, session, ref tick, 4, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Releases, Has.Count.EqualTo(1200), "every town released");
            Assert.That(tally.Releases.Values.All(r => r == 1), Is.True, "once");
            Assert.That(tally.MostReleasedInAFrame, Is.LessThanOrEqualTo(Budget));
            Assert.That(tally.Left, Is.EqualTo(1200), "each with its building's leave");
            Assert.That(store.CompleteGroupCount, Is.Zero);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Is.Empty);
            Assert.That(harness.Subscriptions.Push.GroupReleasesDeferred - deferredBefore, Is.EqualTo(700 + 200),
                "700 kept by the first frame, 200 by the second");
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>A RESET in the middle of a mass release: the client drops everything, and the keys still waiting to be released are simply gone — no
    /// release for what the client no longer holds.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-03")]
    public void AResetInTheMiddleOfAMassReleaseLeavesNothingHeld()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var towns = Towns(dbe, 1200, 1, out _);
        using var harness = Harness(dbe, nameof(AResetInTheMiddleOfAMassReleaseLeavesNothingHeld), maxGroups: 2048);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 5, new Tally());
        Destroy(dbe, towns);
        Run(harness, session, ref tick, 1, new Tally());
        var store = harness.Replica(session).Store;
        Assert.That(store.CompleteGroupCount, Is.EqualTo(700), "precondition: 700 still waiting to be released");

        harness.StateOf(session).PendingReset = true;
        var tally = new Tally();
        Run(harness, session, ref tick, 3, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Resets, Is.EqualTo(1));
            Assert.That(tally.Releases, Is.Empty, "the RESET dropped them on the client: nothing to release");
            Assert.That(store.CompleteGroupCount, Is.Zero);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Is.Empty);
            Assert.That(harness.Subscriptions.Push.GroupsOf(session).Published(0).Count, Is.Zero, "nothing held");
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// A town still waiting to be released and met again is kept: held all along, never released and never completed a second time.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ATownWaitingToBeReleasedAndMetAgainIsKept()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var towns = Towns(dbe, 1200, 1, out _);
        using var harness = Harness(dbe, nameof(ATownWaitingToBeReleasedAndMetAgainIsKept), maxGroups: 2048);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 5, new Tally());
        Destroy(dbe, towns);
        var tally = new Tally();
        Run(harness, session, ref tick, 1, tally);
        Assert.That(tally.Releases.Keys.Max(), Is.LessThan(1101), "precondition: the last hundred are still waiting");

        // Towns 1 101 to 1 200 again, where they were.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var key = 1101; key <= 1200; key++)
            {
                SpawnTown(tx, key, 1200);
            }

            tx.Commit();
        }

        Run(harness, session, ref tick, 4, tally);
        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(tally.Releases, Has.Count.EqualTo(1100), "the others released");
            Assert.That(tally.Releases.Keys.Any(k => k > 1100), Is.False, "the hundred met again never");
            Assert.That(tally.Completions, Is.Empty, "and never completed again");
            Assert.That(store.CompleteGroupCount, Is.EqualTo(100));
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(100));
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// Past the bound the session is told how many towns it left out, as a level: again after a RESET, which cleared what it was told; carried by the next
    /// published frame when the one that would have said it is not published; and 0 once it holds every town it meets.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-03")]
    public void PastTheBoundTheSessionIsToldHowManyTownsItLeftOut()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var towns = Towns(dbe, 10, 1, out _);
        Walker(dbe);
        using var harness = Harness(dbe, nameof(PastTheBoundTheSessionIsToldHowManyTownsItLeftOut), maxGroups: 8);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var tally = new Tally();
        Run(harness, session, ref tick, 3, tally);
        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(store.GroupSetOverLimit(0), Is.EqualTo(2), "ten met, eight held");
            Assert.That(tally.Truncations, Is.EqualTo(new uint[] { 2 }), "said once while it holds");
            Assert.That(store.CompleteGroupCount, Is.EqualTo(8));
        });

        // A RESET clears what the client was told: the count is said again.
        harness.StateOf(session).PendingReset = true;
        tally = new Tally();
        Run(harness, session, ref tick, 3, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Truncations, Is.EqualTo(new uint[] { 2 }), "said again after the RESET");
            Assert.That(store.GroupSetOverLimit(0), Is.EqualTo(2));
            Assert.That(store.CompleteGroupCount, Is.EqualTo(8));
        });

        // Unread: two frames take the slots; then the towns not held go, and the frame that would say 0 is gathered and not published.
        var held = store.CompleteGroups.Select(g => g.Key).ToHashSet();
        var skippedBefore = harness.Assembler.FramesSkipped;
        for (var t = 0; t < 2; t++)
        {
            Walk(dbe, t % 2 == 0 ? 6f : -6f);
            Run(harness, session, ref tick, 1, new Tally(), read: false);
        }

        Destroy(dbe, towns.Where((_, i) => !held.Contains(i + 1)));
        Walk(dbe, 9f);
        Run(harness, session, ref tick, 1, new Tally(), read: false);
        Assert.That(harness.Assembler.FramesSkipped, Is.GreaterThan(skippedBefore), "precondition: a frame went unpublished");

        tally = new Tally();
        Drain(harness, session, tally);
        Assert.That(store.GroupSetOverLimit(0), Is.EqualTo(2), "the frames read still said two");
        Run(harness, session, ref tick, 2, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Truncations, Is.EqualTo(new uint[] { 0 }), "the next published frame says 0, once");
            Assert.That(store.GroupSetOverLimit(0), Is.Zero);
            Assert.That(store.CompleteGroupCount, Is.EqualTo(8));
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>A bound of 2^20 is accepted, and a session holding three keys under it costs three keys' room, not the bound's.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AMillionKeyBoundCostsWhatIsHeld()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe, 3, 1, out _);
        using var harness = Harness(dbe, nameof(AMillionKeyBoundCostsWhatIsHeld), maxGroups: 1 << 20);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 3, new Tally());

        var groups = harness.Subscriptions.Push.GroupsOf(session);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).Store.CompleteGroupCount, Is.EqualTo(3));
            Assert.That(groups.CapacityForTest(0, committed: true), Is.EqualTo(Engine.Internals.GroupSessionState.InitialCapacity));
            Assert.That(groups.CapacityForTest(0, committed: false), Is.LessThanOrEqualTo(Engine.Internals.GroupSessionState.InitialCapacity));
        });
    }

    /// <summary>
    /// A session whose profile stops naming the set holds none of its keys: the switch resets the client, and the server's held keys follow — a member is no
    /// longer resolved through a key the client no longer holds. Named again, every key fills and completes again, once.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ASessionWhoseProfileStopsNamingItsSetHoldsNoneOfItsKeys()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe, 4, 2, out var buildings);
        using var harness = Harness(dbe, nameof(ASessionWhoseProfileStopsNamingItsSetHoldsNoneOfItsKeys), maxGroups: 8);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 3, new Tally());
        var store = harness.Replica(session).Store;
        Assert.That(store.CompleteGroupCount, Is.EqualTo(4), "precondition");
        Assert.That(harness.Subscriptions.Commands.TryNetIdOf(session, buildings[0], out _), Is.True, "precondition: held through its town");

        Assert.That(harness.Sessions.SetProfile(session, Plain), Is.True);
        var tally = new Tally();
        Run(harness, session, ref tick, 3, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Resets, Is.GreaterThan(0), "the switch reset the client");
            Assert.That(store.CompleteGroupCount, Is.Zero);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Is.Empty);
            Assert.That(harness.Subscriptions.Push.GroupsOf(session) is not { } g || g.Published(0).Count == 0, Is.True, "nothing held");
            Assert.That(harness.Subscriptions.Commands.TryNetIdOf(session, buildings[0], out _), Is.False, "no longer held through a key");
            Assert.That(store.Anomalies, Is.Zero);
        });

        Assert.That(harness.Sessions.SetProfile(session, GroupTestSchema.Profile), Is.True);
        tally = new Tally();
        Run(harness, session, ref tick, 3, tally);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Completions.Keys, Is.EquivalentTo(new long[] { 1, 2, 3, 4 }), "every key completed again");
            Assert.That(tally.Completions.Values.All(c => c == 1), Is.True, "once");
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Has.Length.EqualTo(8));
            Assert.That(harness.Subscriptions.Commands.TryNetIdOf(session, buildings[0], out _), Is.True);
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// A Sphere session holding towns switched to a World profile — groups are a Sphere's — holds none of their keys: the frame resets, the server gives the
    /// keys back, and a building held only through its town is no longer resolved.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-03")]
    public void ASessionSwitchedToAWorldProfileHoldsNoneOfItsKeys()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe, 4, 2, out var buildings);
        using var harness = Harness(dbe, nameof(ASessionSwitchedToAWorldProfileHoldsNoneOfItsKeys), maxGroups: 8);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        Run(harness, session, ref tick, 3, new Tally());
        Assert.That(harness.Replica(session).Store.CompleteGroupCount, Is.EqualTo(4), "precondition");

        Assert.That(harness.Sessions.SetProfile(session, World), Is.True);
        var tally = new Tally();
        Run(harness, session, ref tick, 3, tally);
        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(tally.Resets, Is.GreaterThan(0), "the switch reset the client");
            Assert.That(store.CompleteGroupCount, Is.Zero);
            Assert.That(harness.Replica(session).NetIds(BuildingPlan(harness)), Is.Empty, "the World observes no building");
            Assert.That(harness.Subscriptions.Push.GroupsOf(session) is not { } g || g.Published(0).Count == 0, Is.True, "nothing held");
            Assert.That(harness.Subscriptions.Commands.TryNetIdOf(session, buildings[0], out _), Is.False, "no longer held through a key");
            Assert.That(store.Anomalies, Is.Zero);
        });
    }
}
