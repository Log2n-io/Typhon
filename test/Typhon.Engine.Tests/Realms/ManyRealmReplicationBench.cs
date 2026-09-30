using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Runtime;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms D-7 (12-realms § 8, 03 § R4.4): the per-realm fixed cost of replication. Many one-cell realms, one session and one entity each — the
/// interiors case — against one realm holding the same sessions and entities. The serial share of a tick grows with the ACTIVE realms (each one's blocks
/// step, index finish and frame prologue), so what this reports is microseconds per active realm per tick. Manual: it measures, it asserts only that the
/// served set is what it should be.
/// </summary>
[TestFixture]
[Explicit("A measurement, not a check: run by hand (Realms D-7)")]
[Category("Manual")] // It reports µs per realm per tick and asserts nothing about them: a CI box's timing would be read as a result it is not.
[NonParallelizable]
class ManyRealmReplicationBench : TestBase<ManyRealmReplicationBench>
{
    private const string World = "world";

    private DatabaseEngine SetupEngine(int realms)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(realms + 1);
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1024, 1024), 16));
        dbe.InitializeArchetypes();
        var room = SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(16, 16), 16);
        for (var r = 1; r <= realms; r++)
        {
            dbe.Realms.Register(new RealmId((ushort)r), new RealmConfig
            {
                Grid = room, WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 1,
                Replication = new RealmReplicationConfig { CellM = 16 },
            });
        }

        return dbe;
    }

    /// <remarks>
    /// <para>
    /// <b>Three realm counts, because one point cannot tell a fixed cost from a growing one.</b> Two points suggested growth and the third refuted it, which is
    /// the reason all three are kept. Measured on a 7950X, medians of three runs, cost per <i>added</i> realm in the <b>frame prologue</b> — the serial share,
    /// which is what D-7 gates on:
    /// </para>
    /// <para>
    /// 100 realms <b>0.53 us</b> · 250 realms <b>0.66 us</b> · 500 realms <b>0.65 us</b> — flat, and comfortably under D-7's ~1 us threshold.
    /// </para>
    /// <para>
    /// <b>The whole-tick figure is NOT that quantity and must not be read as it.</b> Its per-added-realm cost reads 0.36 / 1.04 / 0.96 us over the same three
    /// counts, and the low first number is this harness's noise rather than a trend: at 100 realms the whole-tick difference between the two arms is a few tens
    /// of microseconds against a ~190 us baseline, which is inside the run-to-run spread. Reading those two points alone as a 3.4x rise was the wrong
    /// conclusion the 500-realm point exists to prevent. It is still the useful UPPER bound: three serial per-realm stages sit outside the frame prologue, so
    /// the prologue figure is a lower bound on the serial cost and this one is above the whole of it.
    /// </para>
    /// <para>
    /// <b>These realms are the pessimistic shape on purpose</b> — one entity and one session each, so every realm pays a realm's fixed cost to carry almost no
    /// work. The SWG demo's own D-7 figure (<c>demo/SwgTatooine.Tests RealmScaleBench</c>) is <b>~0.24 us</b> per active realm at 65 active realms, less than
    /// half of this, because its interiors hold furniture and NPCs. Both are under the threshold; this one is the bound.
    /// </para>
    /// </remarks>
    [TestCase(100, false)]
    [TestCase(100, true)]
    [TestCase(250, false)]
    [TestCase(250, true)]
    [TestCase(500, false)]
    [TestCase(500, true)]
    public void ServingManyOneCellRealms(int realms, bool apart)
    {
        const int Ticks = 200;
        using var dbe = SetupEngine(realms);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
            subs.Profile(World, p => p.World().Of<RealmUnit>());
        }, nameof(ServingManyOneCellRealms), replicationCellM: 16, maxSessions: realms + 8);
        harness.RunFence = true;

        // One session and one entity per realm: in realm 1..N when N > 1, all in realm 0 for the one-realm baseline.
        var sessions = harness.OpenSessions(realms, World);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < realms; i++)
            {
                var realm = apart ? (ushort)(i + 1) : (ushort)0;
                harness.Subscriptions.Commands.Enter(sessions[i], new RealmId(realm));
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 8, MinY = 8, MaxX = 8, MaxY = 8 }, Realm = realm }));
            }

            tx.Commit();
        }

        for (var t = 0; t < 20; t++)
        {
            harness.RunTick(harness.Tick + 1);
            foreach (var s in sessions)
            {
                harness.Drain(s);
            }
        }

        // The prologue's own share, so the whole-tick figure can be split into the SERIAL part D-7 actually gates on and everything else. Turned on here rather
        // than in setup because it is process-wide and this is the only thing in the fixture that reads it.
        //
        // <b>Restored in a finally, and it has to be:</b> nothing in this assembly ever reassigns PhaseTimingEnabled, so a throw inside the loop would leave
        // every later fixture paying phase timing AND printing a phase report every 500 ticks. [Explicit] bounds the blast radius; it does not remove it.
        var timingWas = FrameAssembler.PhaseTimingEnabled;
        FrameAssembler.PhaseTimingEnabled = true;
        (double Ms, long Ticks) prologueFrom;
        (double Ms, long Ticks) prologueTo;
        Stopwatch watch;
        try
        {
            prologueFrom = harness.Subscriptions.Frames.PrologueTotal;
            watch = Stopwatch.StartNew();
            for (var t = 0; t < Ticks; t++)
            {
                harness.RunTick(harness.Tick + 1);
                foreach (var s in sessions)
                {
                    harness.Drain(s);
                }
            }

            watch.Stop();
            prologueTo = harness.Subscriptions.Frames.PrologueTotal;
        }
        finally
        {
            FrameAssembler.PhaseTimingEnabled = timingWas;
        }

        var perTickUs = watch.Elapsed.TotalMicroseconds / Ticks;
        var prologueTicks = Math.Max(1L, prologueTo.Ticks - prologueFrom.Ticks);
        var prologueUs = (prologueTo.Ms - prologueFrom.Ms) * 1000d / prologueTicks;
        TestContext.Out.WriteLine(
            $"D-7: {(apart ? realms : 1)} realm(s) served, {sessions.Length} session(s): {perTickUs:F1} µs per tick whole, " +
            $"{prologueUs:F1} µs per tick in the frame prologue (serial) " +
            $"(harness: fence + track single-threaded, drain included)");
        Assert.That(harness.Subscriptions.Hub.Active.Length, Is.EqualTo(apart ? realms + 1 : 1), "every realm with a session is served");
    }
}
