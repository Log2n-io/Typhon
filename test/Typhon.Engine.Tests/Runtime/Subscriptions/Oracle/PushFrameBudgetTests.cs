using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The frame byte budget (15, #1408): every record fits a frame, the records due in one tick do not, and the session still converges — each frame within
/// the limit, nothing refused, nothing lost.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushFrameBudgetTests : TestBase<PushFrameBudgetTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;

    // A frame a few hundred records wide: the scalar test schema's records are a few bytes, so a burst of hundreds overflows it.
    private const int FrameBytes = 2048;

    private static SubscriptionsOptions Options(int enterBudget, int frameBytes, bool budgetOff = false) => new()
    {
        CheckFrameMeterForTest = true,
        FrameBudgetOffForTest = budgetOff,
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 8,
        EnterBudgetPerFrame = enterBudget,
        FrameBytes = frameBytes,
        PushShadow = true,
    };

    private FrameHarness Harness(DatabaseEngine dbe, string name, int enterBudget = 500, int frameBytes = FrameBytes, bool shards = false,
        bool tags = false, bool budgetOff = false)
    {
        var harness = FrameHarness.Create(dbe, s => GroupTestSchema.Declare(s, Radius, Reach, Margin, shards: shards, tags: tags), name,
            Options(enterBudget, frameBytes, budgetOff));
        harness.RunFence = true;
        harness.DrainNetIds = shards;
        return harness;
    }

    private static void Town(DatabaseEngine dbe, int key, float x, float y, float half)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, half) }));
        tx.Commit();
    }

    // Packed on a 2 m lattice, 20 a row: 400 fit a 40 m square, inside the town (a few replication cells: they are 40 m, edges at −32 and 8).
    private static List<EntityId> Buildings(DatabaseEngine dbe, int key, float x, float y, int count)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = GroupTestSchema.At(x + (i % 20 * 2f), y + (i / 20 * 2f));
            var ai = new ProjAi { Template = (byte)(1 + (i % 200)) };
            ids.Add(tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(in ai),
                GroupBuilding.Key.Set(new GroupKey { Town = key })));
        }

        tx.Commit();
        return ids;
    }

    private static void Citizens(DatabaseEngine dbe, float x, float y, int count, float spacing = 2f)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = GroupTestSchema.At(x + (i % 20 * spacing), y + (i / 20 * spacing));
            tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(in bounds), GroupCitizen.Ai.Set(new ProjAi { Template = 1 }), GroupCitizen.Key.Set(default));
        }

        tx.Commit();
    }

    private static List<EntityId> Shards(DatabaseEngine dbe, int key, int count)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = 1 }), GroupPositionless.Key.Set(new GroupKey { Town = key })));
        }

        tx.Commit();
        return ids;
    }

    // The profile's detection is explicit: a write is pushed by the application.
    private static void Rewrite(FrameHarness harness, List<EntityId> shards, byte template)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        foreach (var shard in shards)
        {
            var entity = tx.OpenMut(shard);
            entity.Set(GroupPositionless.Ai, new ProjAi { Template = template });
            harness.Subscriptions.Commands.Replicate(in entity);
        }

        tx.Commit();
    }

    /// <summary>One tick and its delivery; the frame's completions of <paramref name="key"/>, or 0 when no frame arrived.</summary>
    private static int Tick(FrameHarness harness, SessionId session, ref long tick, long key)
    {
        tick++;
        harness.RunTick(tick);
        return harness.Deliver(session) > 0
            ? harness.Replica(session).Store.Groups.Count(g => g.Kind == SourceStatus.GroupComplete && g.Key == key)
            : 0;
    }

    private static void AssertWithinLimit(FrameHarness harness, SessionId session)
    {
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.MeterViolations, Is.Zero, "the meter bounds every record it let through");
            Assert.That(harness.Assembler.OversizeSkips, Is.Zero, "no frame was refused for its size");
            Assert.That(harness.LargestFrame, Is.LessThanOrEqualTo(FrameBytes), "every frame within the limit");
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// The issue's workload: a held, complete town gains far more than a frame of buildings in one tick. Each enter fits; together they are several frames.
    /// On main every frame from then on is refused, the log ages out, and the RESET's refill is refused too.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void ACompleteTownGainingMoreThanAFrameOfBuildingsDeliversThemOverFrames()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 10);
        using var harness = Harness(dbe, nameof(ACompleteTownGainingMoreThanAFrameOfBuildingsDeliversThemOverFrames));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var completed = 0;
        for (var t = 0; t < 4; t++)
        {
            completed += Tick(harness, session, ref tick, 7);
        }

        var plan = harness.PlanIndex(nameof(GroupBuilding));
        Assert.That(completed, Is.EqualTo(1));
        Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(10));
        var frameBefore = harness.LargestFrame;

        Buildings(dbe, 7, -20f, -20f, 400);
        for (var t = 0; t < 40; t++)
        {
            completed += Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(frameBefore * 4, Is.LessThan(FrameBytes), "the steady state is a small frame");
            Assert.That(harness.Assembler.FramesOwing, Is.GreaterThan(0), "the burst exceeded the frame: its tail was owed");
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(410), "every building arrived");
            Assert.That(harness.Subscriptions.Push.Resets, Is.Zero, "and no RESET was needed for it");
            Assert.That(completed, Is.EqualTo(1), "the town stays complete: a late member is a later join");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>
    /// SUB-36's mutant: with the frame budget off — the engine as it was — the issue's workload stalls. Every frame after the burst is refused for its
    /// size, the log ages out into a RESET, and the RESET's refill is refused too: the buildings never arrive.
    /// </summary>
    [Test]
    [RuleMutant("SUB-36")]
    public void Mutant_WithoutTheFrameBudgetABurstStalls()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 10);
        using var harness = Harness(dbe, nameof(Mutant_WithoutTheFrameBudgetABurstStalls), budgetOff: true);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Buildings(dbe, 7, -20f, -20f, 400);
        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.OversizeSkips, Is.GreaterThan(0), "frames refused for their size");
            Assert.That(harness.Subscriptions.Push.Resets, Is.GreaterThan(0), "the log aged out into a RESET");
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Has.Length.LessThan(410), "the burst never arrived");
        });
    }

    /// <summary>A town heavier than a frame, met with an ample count budget: the fill is spread by bytes and the town completes once, with its last member.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void AFillHeavierThanAFrameIsSpreadByBytes()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -20f, -20f, 400);
        using var harness = Harness(dbe, nameof(AFillHeavierThanAFrameIsSpreadByBytes));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var completed = 0;
        var filling = 0;
        var plan = harness.PlanIndex(nameof(GroupBuilding));
        for (var t = 0; t < 40; t++)
        {
            var c = Tick(harness, session, ref tick, 7);
            filling += harness.Replica(session).Store.Archetypes[plan].EnteredCount > 0 ? 1 : 0;
            if (c > 0)
            {
                Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(400), "the town completes with every building in the store");
            }

            completed += c;
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(400));
            Assert.That(filling, Is.GreaterThan(2), "the fill took several frames: the count alone would have taken them all at once");
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(harness.Assembler.FramesOwing, Is.Zero, "the fill is paced by its allowance: nothing it takes is owed");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>
    /// Group-only updates above a frame a tick, for ten ticks, then quiet: the updates are carried across frames, none lost — the client converges on the
    /// server's values. On main no frame is published while they last.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void GroupOnlyUpdatesAboveAFrameAreCarriedAndNeverLost()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Town(dbe, 9, 200f, 0f, 30f);
        Buildings(dbe, 9, 190f, -10f, 3);
        var shards = Shards(dbe, 7, 1000);
        using var harness = Harness(dbe, nameof(GroupOnlyUpdatesAboveAFrameAreCarriedAndNeverLost), shards: true);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var plan = harness.PlanIndex(nameof(GroupPositionless));
        for (var t = 0; t < 20; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(1000), "the fill, under its count, fits");

        // Every shard rewritten every tick, several frames' worth: the owed updates go oldest first, so no shard falls behind by more than a few ticks.
        // Meanwhile the session walks until it meets town 9 (and keeps 7): owed updates hold back neither its fill nor its completion, and the view stays
        // complete — only an owed ENTER withholds a completion.
        var netIds = harness.Replica(session).NetIds(plan);
        var worstLag = 0d;
        var completed9 = 0;
        var viewIncomplete = 0;
        var owing = harness.Assembler.FramesOwing;
        for (var t = 0; t < 30; t++)
        {
            Rewrite(harness, shards, (byte)(10 + t));
            if (t < 13)
            {
                harness.Sessions.SetViewpoint(session, new Vector3D(8d * (t + 1), 0d, 0d));
            }

            completed9 += Tick(harness, session, ref tick, 9);

            // While it walks, new cells come into reach and the view fills again; once it stands, owed updates must not take it back.
            viewIncomplete += t >= 18 && !harness.StateOf(session).ViewComplete ? 1 : 0;
            if (t >= 8)
            {
                foreach (var n in netIds)
                {
                    worstLag = System.Math.Max(worstLag, 10 + t - (harness.Replica(session).Value(plan, n, "kind") ?? 0d));
                }
            }
        }

        for (var t = 0; t < 20; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(1000));
            Assert.That(harness.Assembler.FramesOwing - owing, Is.GreaterThan(10), "the rewrites exceeded the frame: updates were owed");
            Assert.That(worstLag, Is.LessThanOrEqualTo(8d), "no shard is starved while the updates exceed the frame");
            Assert.That(completed9, Is.EqualTo(1), "a town met while updates are owed is filled and completed");
            Assert.That(viewIncomplete, Is.Zero, "owed updates do not take the view's completeness back");
            Assert.That(netIds.Count(n => harness.Replica(session).Value(plan, n, "kind") != 39), Is.Zero, "every shard holds the last value written");
            Assert.That(harness.Subscriptions.Push.Resets, Is.Zero, "carried, not reset");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>Geometry alone: a spawn burst inside the delivered sphere — event-driven enters, under no budget — arrives over frames.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void ASpawnBurstInsideTheSphereArrivesOverFrames()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(ASpawnBurstInsideTheSphereArrivesOverFrames));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Citizens(dbe, -20f, -20f, 400);
        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupCitizen))), Has.Length.EqualTo(400));
            Assert.That(harness.Assembler.FramesOwing, Is.GreaterThan(0), "the burst's enters exceeded the frame");
            Assert.That(harness.Subscriptions.Push.Resets, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>One cell holding more than a frame, delivered whole by the fill: the overshoot is carried, and the view completes.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void ACellHeavierThanAFrameIsDeliveredOverFrames()
    {
        // One 40 m cell, [8, 48) on both axes: 400 citizens on a 1.5 m lattice.
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Citizens(dbe, 10f, 10f, 400, spacing: 1.5f);
        using var harness = Harness(dbe, nameof(ACellHeavierThanAFrameIsDeliveredOverFrames));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupCitizen))), Has.Length.EqualTo(400));
            Assert.That(harness.Assembler.FramesOwing, Is.GreaterThan(0), "the cell, delivered whole, overshot the frame: the rest was owed");
            Assert.That(harness.StateOf(session).ViewComplete, Is.True);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>A cell heavier than the owed cap at a small enter budget: the overflow's RESET may not overflow again — it is the repair — and converges.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void ACellHeavierThanTheOwedCapConverges()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Citizens(dbe, 10f, 10f, 400, spacing: 1.5f);
        using var harness = Harness(dbe, nameof(ACellHeavierThanTheOwedCapConverges), enterBudget: 16);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 60; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupCitizen))), Has.Length.EqualTo(400));
            Assert.That(harness.Assembler.OwedResets, Is.LessThanOrEqualTo(1), "one RESET at most: a RESET frame never overflows");
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>
    /// C3: the completion gate is global — an owed geometric enter withholds the GROUP_COMPLETE of a town met in the same frame, until the session's
    /// store holds every enter it is owed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    [VerifiesRule("SUB-33")]
    public void ACompletionWaitsForEveryOwedEnter()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 9, 200f, 0f, 30f);
        Buildings(dbe, 9, 190f, -10f, 3);
        using var harness = Harness(dbe, nameof(ACompletionWaitsForEveryOwedEnter));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(80d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 9);
        }

        // A burst of citizens inside the sphere, and a step that meets town 9, in one tick.
        Citizens(dbe, 60f, -20f, 400);
        harness.Sessions.SetViewpoint(session, new Vector3D(96d, 0d, 0d));
        var citizens = harness.PlanIndex(nameof(GroupCitizen));
        var completed = 0;
        var heldAtCompletion = -1;
        var owedSeen = false;
        for (var t = 0; t < 40; t++)
        {
            var c = Tick(harness, session, ref tick, 9);
            owedSeen |= harness.StateOf(session).OwedEnters > 0;
            if (c > 0)
            {
                heldAtCompletion = harness.Replica(session).NetIds(citizens).Length;
            }

            completed += c;
        }

        Assert.Multiple(() =>
        {
            Assert.That(owedSeen, Is.True, "the burst's enters were owed");
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(heldAtCompletion, Is.EqualTo(400), "town 9 completed only once no enter was owed");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>
    /// SUB-33's "the first frame after it that owes none": a town filled in a frame that spilled — its completion withheld — completes as soon as nothing
    /// is owed, not once a nearer town met meanwhile has taken the fill allowance every frame and finished its own fill.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    [VerifiesRule("SUB-33")]
    public void AWithheldCompletionGoesOutOnceNothingIsOwedWhateverTheBudget()
    {
        // Both towns' buildings exist before their towns do: met, a town fills through its cursor — none is a member event.
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Buildings(dbe, 9, 90f, -10f, 3);
        Buildings(dbe, 3, 80f, 40f, 1000);
        using var harness = Harness(dbe, nameof(AWithheldCompletionGoesOutOnceNothingIsOwedWhateverTheBudget));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 9);
        }

        // Town 9 and a burst of citizens in one commit: the town fills in the frame the burst spills, so its completion is withheld behind the owed enters.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 9, Extent = GroupTestSchema.Square(100f, 0f, 30f) }));
            for (var i = 0; i < 300; i++)
            {
                var bounds = GroupTestSchema.At(-60f + (i % 20 * 2f), -20f + (i / 20 * 2f));
                tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(in bounds), GroupCitizen.Ai.Set(new ProjAi { Template = 1 }), GroupCitizen.Key.Set(default));
            }

            tx.Commit();
        }

        var withheld = false;
        for (var t = 0; t < 3 && !withheld; t++)
        {
            var c = Tick(harness, session, ref tick, 9);
            Assert.That(c, Is.Zero, "town 9 is not completed while the burst is owed");
            withheld = harness.StateOf(session).OwedEnters > 0 && harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))).Length == 3;
        }

        Assert.That(withheld, Is.True, "town 9's buildings entered, the burst owed: its completion withheld");

        // Town 3, nearer than town 9: met while the burst drains, it then takes the allowance every frame for many frames.
        Town(dbe, 3, 60f, 60f, 30f);
        long completed9At = -1, completed3At = -1, drainedAt = -1;
        for (var t = 0; t < 60; t++)
        {
            tick++;
            harness.RunTick(tick);
            if (harness.Deliver(session) == 0)
            {
                continue;
            }

            drainedAt = drainedAt < 0 && harness.StateOf(session).OwedEnters == 0 ? tick : drainedAt;
            var groups = harness.Replica(session).Store.Groups;
            completed9At = completed9At < 0 && groups.Any(g => g.Kind == SourceStatus.GroupComplete && g.Key == 9) ? tick : completed9At;
            completed3At = completed3At < 0 && groups.Any(g => g.Kind == SourceStatus.GroupComplete && g.Key == 3) ? tick : completed3At;
        }

        Assert.Multiple(() =>
        {
            Assert.That(completed3At, Is.GreaterThan(0), "town 3 filled and completed");
            Assert.That(completed9At, Is.EqualTo(drainedAt), "town 9 completed in the first frame that owed nothing");
            Assert.That(completed9At, Is.LessThan(completed3At), "town 9 did not wait for town 3's fill");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>C10: a session's own <c>SessionLimits.FrameBytes</c>, below the engine's, bounds its frames — events, entities and the backstop alike.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void ASessionsOwnFrameLimitBoundsItsFrames()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 10);
        using var harness = Harness(dbe, nameof(ASessionsOwnFrameLimitBoundsItsFrames), frameBytes: 8192);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetFrameBytesForTest(session, 1024);
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Buildings(dbe, 7, -20f, -20f, 400);
        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Has.Length.EqualTo(410));
            Assert.That(harness.LargestFrame, Is.LessThanOrEqualTo(1024), "the session's limit, not the engine's 8 KiB");
            Assert.That(harness.LargestFrame, Is.GreaterThan(512), "and the frames used it");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>A held, complete town of 10 buildings, then <paramref name="burst"/> more in one tick: the next frame owes most of them.</summary>
    private (FrameHarness Harness, SessionId Session, List<EntityId> Burst, long Tick) Burst(DatabaseEngine dbe, string name, int burst = 400,
        int enterBudget = 500, bool tags = false)
    {
        Town(dbe, 7, 0f, 0f, 60f);
        Buildings(dbe, 7, -40f, -40f, 10);
        var harness = Harness(dbe, name, enterBudget, tags: tags);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        // The tick the burst is projected in, and its frame: owed from there.
        var ids = Buildings(dbe, 7, -20f, -20f, burst);
        for (var t = 0; t < 3 && harness.StateOf(session).OwedEnters == 0; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        return (harness, session, ids, tick);
    }

    /// <summary>An owed enter whose entity is destroyed before it is sent: the client is never told of it, neither its enter nor its leave.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void AnOwedEnterThatLeavesIsNeverSent()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, burst, tick) = Burst(dbe, nameof(AnOwedEnterThatLeavesIsNeverSent));
        using var _ = harness;
        var owed = harness.StateOf(session).OwedEnters;
        var plan = harness.PlanIndex(nameof(GroupBuilding));
        var held = harness.Replica(session).NetIds(plan).Length - 10;
        Assert.That(owed, Is.GreaterThan(100), "the burst's tail is owed");

        // Every building of the burst but the first hundred, sent or owed: destroyed.
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in burst.Skip(100))
            {
                tx.Destroy(id);
            }

            tx.Commit();
        }

        var left = 0;
        var entered = 0;
        for (var t = 0; t < 40; t++)
        {
            tick++;
            harness.RunTick(tick);
            if (harness.Deliver(session) > 0)
            {
                entered += harness.Replica(session).Store.Archetypes[plan].EnteredCount;
                left += harness.Replica(session).Store.Archetypes[plan].LeftCount;
            }
        }

        // Owed enters still drain in the frames before the destroys reach the push; those were sent, and leave. The rest are cancelled, never sent.
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(110));
            Assert.That(left, Is.EqualTo(held + entered - 100), "a leave for each destroyed building the client was sent, and only those");
            Assert.That(held + entered, Is.LessThan(400), "destroyed while owed: never sent");
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>A town released while its new members are owed: the owed enters are cancelled, and no leave is sent for a building the client never had.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    [VerifiesRule("SUB-33")]
    public void AnOwedEnterOfAReleasedTownIsNeverSent()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, _, tick) = Burst(dbe, nameof(AnOwedEnterOfAReleasedTownIsNeverSent), burst: 1800);
        using var owner = harness;
        var plan = harness.PlanIndex(nameof(GroupBuilding));
        var held = harness.Replica(session).NetIds(plan).Length;
        Assert.That(harness.StateOf(session).OwedEnters, Is.GreaterThan(1000), "the burst's tail is owed, many frames of it");

        // Out past the reach and the margin by steps under a cell, faster than the tail drains: the town is released, every member it held leaves.
        var left = 0;
        var entered = 0;
        for (var x = 30d; x <= 240d; x += 30d)
        {
            harness.Sessions.SetViewpoint(session, new Vector3D(x, 0d, 0d));
            tick++;
            harness.RunTick(tick);
            if (harness.Deliver(session) > 0)
            {
                entered += harness.Replica(session).Store.Archetypes[plan].EnteredCount;
                left += harness.Replica(session).Store.Archetypes[plan].LeftCount;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(plan), Is.Empty, "released");
            Assert.That(left, Is.EqualTo(held + entered), "a leave for every building the client had, and only those");
            Assert.That(held + entered, Is.LessThan(1810), "some owed enters were cancelled, never sent");
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>An owed enter whose entity changes before it is sent arrives with the value of the frame that sends it.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void AnOwedEnterUpdatedMeanwhileArrivesWithItsCurrentState()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        using var harness = Harness(dbe, nameof(AnOwedEnterUpdatedMeanwhileArrivesWithItsCurrentState), shards: true);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        var shards = Shards(dbe, 7, 1000);
        Tick(harness, session, ref tick, 7);
        Tick(harness, session, ref tick, 7);
        Assert.That(harness.StateOf(session).OwedEnters, Is.GreaterThan(100), "most shards are owed");

        Rewrite(harness, shards, 42);
        for (var t = 0; t < 30; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        var plan = harness.PlanIndex(nameof(GroupPositionless));
        var netIds = harness.Replica(session).NetIds(plan);
        Assert.Multiple(() =>
        {
            Assert.That(netIds, Has.Length.EqualTo(1000));
            Assert.That(netIds.Count(n => harness.Replica(session).Value(plan, n, "kind") != 42), Is.Zero, "every shard holds the value written while owed");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>A session whose frames are left undrained across the burst keeps what it is owed through the skips, and converges once drained.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    [VerifiesRule("SUB-03")]
    public void ASkippedSessionKeepsWhatItIsOwed()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, _, tick) = Burst(dbe, nameof(ASkippedSessionKeepsWhatItIsOwed));
        using var __ = harness;
        for (var t = 0; t < 5; t++)
        {
            tick++;
            harness.RunTick(tick);
        }

        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Has.Length.EqualTo(410));
            Assert.That(harness.Subscriptions.Push.Resets, Is.Zero, "caught up from the log with what it owed, not refilled by a RESET");
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>A RESET clears the client's store, and with it what the session was owed: the refill sends each entity once.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void AResetEmptiesTheOwed()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, _, tick) = Burst(dbe, nameof(AResetEmptiesTheOwed), tags: true);
        using var __ = harness;
        Assert.That(harness.StateOf(session).OwedCount, Is.GreaterThan(0));

        // A change of the session's own visibility mask resets its view (14 § 6).
        Assert.That(harness.Sessions.SetVisibility(session, 1u), Is.True);
        var resets = harness.Subscriptions.Push.Resets;
        Tick(harness, session, ref tick, 7);
        Assert.That(harness.Subscriptions.Push.Resets, Is.GreaterThan(resets), "the frame was a RESET");
        Assert.That(harness.StateOf(session).OwedCount, Is.Zero, "the RESET emptied the owed set; its refill is paced, so owes nothing");
        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Has.Length.EqualTo(410));
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>More owed than the cap (four enter budgets): the frame is given up for a RESET, whose refill the budgets pace — and the session converges.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void PastTheOwedCapTheNextFrameIsAReset()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, _, tick) = Burst(dbe, nameof(PastTheOwedCapTheNextFrameIsAReset), enterBudget: 16);
        using var __ = harness;
        for (var t = 0; t < 80; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.OwedResets, Is.EqualTo(1), "the owed set passed its cap once; the RESET's paced refill never again");
            Assert.That(harness.Assembler.OwedOverflows, Is.EqualTo(1), "counted as an overflow, not a lost entity");
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Has.Length.EqualTo(410));
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero);
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>
    /// An owed entity that becomes the session's controlled one is carried first, whatever its age: the frame's SELF names it, so its client must hold it
    /// by then (11 § 2).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    [VerifiesRule("SUB-11")]
    public void AnOwedEntityTheSessionComesToControlIsCarriedFirst()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, burst, tick) = Burst(dbe, nameof(AnOwedEntityTheSessionComesToControlIsCarriedFirst));
        using var owner = harness;
        var state = harness.StateOf(session);
        var newest = burst.Last(id => state.OwesEnter(harness.NetIdOf(id)));
        Assert.That(state.OwedEnters, Is.GreaterThan(150), "more owed than one frame carries: the newest would wait several frames");

        Assert.That(harness.Sessions.SetControlled(session, newest), Is.True);
        Tick(harness, session, ref tick, 7);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Does.Contain(harness.NetIdOf(newest)),
                "sent in the frame whose SELF names it");
            Assert.That(harness.StateOf(session).OwedEnters, Is.GreaterThan(0), "while older owed ones still wait");
        });
        AssertWithinLimit(harness, session);
    }

    /// <summary>SUB-26: an entity the session is owed the enter of is not one it may name — its client was never given its identity.</summary>
    [Test]
    [VerifiesRule("SUB-26")]
    [VerifiesRule("SUB-36")]
    public void AnOwedEntityIsNotHeldForACommand()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var (harness, session, burst, tick) = Burst(dbe, nameof(AnOwedEntityIsNotHeldForACommand));
        using var owner = harness;
        var state = harness.StateOf(session);
        var owed = burst.First(id => state.OwesEnter(harness.NetIdOf(id)));
        var sent = burst.First(id => !state.OwesEnter(harness.NetIdOf(id)));
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.TryHeldNetId(session, owed, out _), Is.False, "owed: not on the client");
            Assert.That(harness.Assembler.TryHeldNetId(session, sent, out var netId) && netId == harness.NetIdOf(sent), Is.True, "sent: held");
        });

        for (var t = 0; t < 40; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.That(harness.Assembler.TryHeldNetId(session, owed, out _), Is.True, "sent since");
    }

    /// <summary>
    /// An archetype whose enter record could exceed half the frame is refused at Start (15 § 4.7), though each of its wide sections is under the quarter
    /// a section may reach: it is the whole record that must fit, so the oldest owed record always does. A frame two bytes wider is accepted.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void ARecordLargerThanHalfAFrameIsRefusedAtStart()
    {
        // The wide archetype's enter record, from a frame far too small for it: the refusal names it.
        var dbe = WideGroupTests.Engine(ServiceProvider);
        SubscriptionsOptions At(int frameBytes) => new() { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0), FrameBytes = frameBytes };
        var probe = Assert.Catch(() => FrameHarness.Create(dbe, WideGroupTests.Declare, "probe", At(300)).Dispose()).Message;
        var enter = int.Parse(System.Text.RegularExpressions.Regex.Match(probe, @"enter record of (\d+) bytes").Groups[1].Value);

        // Two bytes short of twice the record: refused by the whole record — the message is the record's, so every section passed its quarter first.
        var refused = (2 * enter) - 2;
        var thrown = Assert.Catch(() => FrameHarness.Create(dbe, WideGroupTests.Declare, "refused", At(refused)).Dispose());
        Assert.That(thrown.Message, Does.Contain($"can produce an enter record of {enter} bytes").And.Contain($"half the {refused}-byte frame"));
        Assert.DoesNotThrow(() => FrameHarness.Create(dbe, WideGroupTests.Declare, "accepted", At((2 * enter) + 2)).Dispose());
    }

    /// <summary>The meter (15 § 4.1) over wide sections: every record of every frame — enters, text changes — encodes within its metered bound.</summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void TheMeterBoundsEveryWideRecord()
    {
        var dbe = WideGroupTests.Engine(ServiceProvider);
        var random = new System.Random(1408);
        string Text(int max) => new('é', random.Next(0, max));
        for (var i = 0; i < 40; i++)
        {
            WideGroupTests.Spawn(dbe, -500f + (i * 20f), Text(40), Text(8), Text(400), i);
        }

        using var harness = FrameHarness.Create(dbe, WideGroupTests.Declare, nameof(TheMeterBoundsEveryWideRecord),
            new SubscriptionsOptions { CheckFrameMeterForTest = true, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) });
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "world")[0];
        for (var t = 1; t <= 3; t++)
        {
            harness.RunTick(t);
            harness.Deliver(session);
        }

        // Every name rewritten to a new length, and pushed: state records with wide groups.
        using (var tx = harness.Engine.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<ProjLabelled>().GetClusterEnumerator())
            {
#pragma warning disable TYPHON009
                var labels = cluster.GetSpan(ProjLabelled.Label);
#pragma warning restore TYPHON009
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    labels[slot].Name = Text(40);
                    harness.Subscriptions.Commands.Replicate(in cluster, slot);
                }
            }

            tx.Commit();
        }

        harness.RunTick(4);
        harness.Deliver(session);
        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(ProjLabelled)).Idx;
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(archetype), Has.Length.EqualTo(40));
            Assert.That(harness.Assembler.MeterChecks, Is.GreaterThanOrEqualTo(80), "the meter was checked on the 40 enters and the 40 text changes");
            Assert.That(harness.Assembler.MeterViolations, Is.Zero);
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        });
    }
}
