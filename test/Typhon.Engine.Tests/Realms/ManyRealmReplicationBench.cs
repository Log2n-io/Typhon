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

    [TestCase(100, false)]
    [TestCase(100, true)]
    [TestCase(250, false)]
    [TestCase(250, true)]
    public void ServingManyOneCellRealms(int realms, bool apart)
    {
        const int Ticks = 200;
        using var dbe = SetupEngine(realms);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
            subs.Profile(World, p => p.World().Of<RealmUnit>());
        }, nameof(ServingManyOneCellRealms), replicationCellM: 16);
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

        var watch = Stopwatch.StartNew();
        for (var t = 0; t < Ticks; t++)
        {
            harness.RunTick(harness.Tick + 1);
            foreach (var s in sessions)
            {
                harness.Drain(s);
            }
        }

        watch.Stop();
        var perTickUs = watch.Elapsed.TotalMicroseconds / Ticks;
        TestContext.Out.WriteLine(
            $"D-7: {(apart ? realms : 1)} realm(s) served, {sessions.Length} session(s): {perTickUs:F1} µs per tick " +
            $"(harness: fence + track single-threaded, drain included)");
        Assert.That(harness.Subscriptions.Hub.Active.Length, Is.EqualTo(apart ? realms + 1 : 1), "every realm with a session is served");
    }
}
