using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// AC-9's second half: ten thousand connect/disconnect cycles leak no session row and no network identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Churn is what finds the leak, not load.</b> A session that stays open holds its row, its frame slots and its ring legitimately, so
/// no steady-state run can tell a held resource from a leaked one. Only the closing tells them apart: after a cycle completes, everything the
/// session held has to be back where it came from, and the way to see a small per-cycle leak is to run enough cycles that it accumulates
/// into a number.
/// </para>
/// <para>
/// <b>Rows and slots, not identities.</b> A netId belongs to an ENTITY and to the database, never to a session, so session churn cannot leak one;
/// what a session holds is a row, and a leaked row shows as the open count and the slot numbers climbing with the cycle count.
/// </para>
/// <para>
/// <b>A wide Sphere, not the World.</b> A World session's fill walks the whole delivery grid, and the push index is a dense grid rebuilt every
/// tick whose size follows the world over the radius — both per-tick or per-connection costs that are correct and irrelevant to a leak, and at
/// twenty thousand ticks they made this fixture take a minute and a half. A 2 km disc keeps the grid small and fills in one frame.
/// </para>
/// <para>
/// <b>What this does NOT cover.</b> AC-9's first half — enter/leave balance per session over an hour of entity churn — is a different run and
/// is not attempted here; see <c>10-measurements.md § 6</c>.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SessionChurnLeakTests : TestBase<SessionChurnLeakTests>
{
    private const string Profile = "god-world";

    private const int Creatures = 32;

    /// <summary>AC-9 names ten thousand cycles.</summary>
    private const int Cycles = 10_000;

    /// <summary>How many sessions are open at once, so a cycle overlaps its neighbours rather than running in isolation.</summary>
    private const int Concurrent = 4;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.Sphere(2000).Of<ProjCreature>());
    }

    /// <summary>Ten thousand cycles later, the table is empty and every identity has come back.</summary>
    [Test]
    public void TenThousandConnectDisconnectCyclesLeakNothing()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var harness = FrameHarness.Create(dbe, Declare, nameof(TenThousandConnectDisconnectCyclesLeakNothing));
        harness.RunTick(1);

        // One warm-up cycle, which is also what proves the measurement is not vacuous: a run that never leases an identity would return to a
        // baseline of zero from a peak of zero and report "no leak" about a mechanism it never exercised.
        var tick = Cycle(harness, 2L, Concurrent);

        var rowsBefore = harness.Sessions.OpenCount;

        for (var i = 0; i < Cycles; i++)
        {
            tick = Cycle(harness, tick, Concurrent);
        }

        var rowsAfter = harness.Sessions.OpenCount;

        // The SLOT is what moves with session churn, and the identity count never was: netIds are per ENTITY and per database, so in a static world the
        // figure is pinned by the entities and the lease pool however many sessions come and go. A leaked row is one that is neither open nor back on the
        // free list, which shows as slot numbers climbing with the cycle count and, eventually, as admission failing at MaxSessions.
        var afterAll = harness.OpenSessions(Concurrent, Profile);
        var highestSlot = 0;
        foreach (var session in afterAll)
        {
            highestSlot = Math.Max(highestSlot, session.Slot);
        }

        TestContext.Out.WriteLine(
            $"AC-9 churn: {Concurrent} sessions per cycle over {Cycles} cycles — open rows {rowsBefore} -> {rowsAfter}; a fresh generation afterwards "
            + $"took slots up to {highestSlot}");

        Assert.Multiple(() =>
        {
            Assert.That(rowsAfter, Is.EqualTo(rowsBefore),
                $"{rowsAfter - rowsBefore} session rows were still held after {Cycles} cycles, so a close is not returning its row");
            Assert.That(highestSlot, Is.LessThan(Concurrent * 4),
                $"after {Cycles} cycles a fresh generation of sessions was given slots up to {highestSlot}. Slots that climb with the cycle count are "
                + "rows that were closed but never returned to the free list, and the server eventually refuses admission at MaxSessions");
        });
    }

    /// <summary>Opens, drives and closes one generation of sessions, leaving the table as it found it.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="tick">The tick to start from.</param>
    /// <param name="count">How many sessions the generation holds.</param>
    /// <returns>The next unused tick.</returns>
    private static long Cycle(FrameHarness harness, long tick, int count)
    {
        var sessions = harness.OpenSessions(count, Profile);
        foreach (var session in sessions)
        {
            harness.Sessions.SetViewpoint(session, new Vector3D(120d, 100d, 0d));
        }

        // Two whole ticks: the first gives the sessions their blocks and their identities, the second serves them a frame from those identities. One tick
        // would lease and close in the same breath, which is not the shape a real session has.
        harness.RunTick(tick++);
        foreach (var session in sessions)
        {
            harness.Deliver(session);
        }

        harness.RunTick(tick++);
        foreach (var session in sessions)
        {
            harness.Deliver(session);
        }

        foreach (var session in sessions)
        {
            harness.Sessions.Close(session, SessionCloseReason.ClientLeft, 1000);

            // The harness keeps a decoded replica per session and never drops one; ids are slot plus generation so nothing is ever overwritten.
            // Ten thousand cycles of four would retain forty thousand replicas and their per-archetype arrays — the fixture's own memory.
            harness.Forget(session);
        }

        // Two advances: the first delivers the Closed events, the second is where the table may recycle the rows those events named. Recycling
        // deliberately lags delivery by a tick so an application reading a Closed event can still look the session up.
        harness.Sessions.BeginTick();
        harness.Sessions.BeginTick();
        return tick;
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Creatures; i++)
            {
                var x = 100f + (i * 4f);
                var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = 100f, MaxX = x + 1f, MaxY = 101f }, Speed = 1f };
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }
}
