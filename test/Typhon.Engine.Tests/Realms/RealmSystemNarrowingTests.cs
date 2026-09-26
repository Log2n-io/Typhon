using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>An indexed field: what makes a change filter scan the dirty set rather than fall back to the whole view.</summary>
[Component("Typhon.Test.Realm.Score", 1, StorageMode = StorageMode.SingleVersion)]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
struct RealmScore
{
    [Index(AllowMultiple = true)]
    public int Value;
}

[Archetype]
partial class ScoredUnit : Archetype<ScoredUnit>
{
    public static readonly Comp<RealmPos> Pos = Register<RealmPos>();
    public static readonly Comp<RealmScore> Score = Register<RealmScore>();
}

/// <summary>
/// Realms: a QuerySystem narrowed to realms (<c>InRealm</c> / <c>InRealms</c>) is dispatched those realms' clusters only — read from their own lists —
/// and, with a change filter, sees their changes only; a dormant realm in the set gives it nothing, like any other system.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmSystemNarrowingTests : TestBase<RealmSystemNarrowingTests>
{
    private const int PerRealm = 6;

    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static RealmPos At(float x, float y, ushort realm) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm };

    /// <summary>Realms 0, 1 and 2 simulated always, <see cref="PerRealm"/> entities in each, one per cell (a cluster each); realm 3 sleeps at once.</summary>
    private DatabaseEngine Engine(out EntityId[][] byRealm)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(5);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Grid()));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid()));
        dbe.Realms.Register(new RealmId(3),
            new RealmConfig { Grid = Grid(), WhenUnobserved = RealmUnobserved.Sleep, SleepAfterTicks = 1, UnobservedTickDivisor = 1 });
        dbe.InitializeArchetypes();
        byRealm = new EntityId[4][];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (ushort r = 0; r < 4; r++)
            {
                byRealm[r] = new EntityId[PerRealm];
                for (var i = 0; i < PerRealm; i++)
                {
                    byRealm[r][i] = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5 + (10 * i), 5 + (10 * r), r)));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    /// <summary>What a narrowed system saw on each of its runs past the first few (the policy settles in a tick).</summary>
    private static List<HashSet<EntityId>> Runs(DatabaseEngine dbe, RealmId[] realms, bool parallel, SimTier tier = SimTier.All, int runs = 4)
    {
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var seen = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        var ticks = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticks));
            dag.QuerySystem("Walk", ctx =>
            {
                var bag = seen.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: parallel, tier: tier, realms: realms, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= runs + 4, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        return seen.OrderBy(kv => kv.Key).Skip(2).Select(kv => kv.Value.ToHashSet()).ToList();
    }

    [TestCase(true)]
    [TestCase(false)]
    [VerifiesRule("RLM-07")]
    public void ANarrowedSystemSeesItsRealmsEntitiesOnly(bool parallel)
    {
        using var dbe = Engine(out var byRealm);
        var one = Runs(dbe, [new RealmId(1)], parallel);
        var two = Runs(dbe, [RealmId.Default, new RealmId(2)], parallel);
        Assert.Multiple(() =>
        {
            Assert.That(one, Is.Not.Empty);
            Assert.That(one, Is.All.EquivalentTo(byRealm[1]), "InRealm(1): realm 1's entities, every run");
            Assert.That(two, Is.All.EquivalentTo(byRealm[0].Concat(byRealm[2])), "InRealms(0, 2): both, nothing else");
        });
    }

    [Test]
    [VerifiesRule("RLM-07")]
    public void ADormantRealmGivesANarrowedSystemNothing()
    {
        using var dbe = Engine(out _);
        var runs = Runs(dbe, [new RealmId(3)], true);
        Assert.That(runs.All(r => r.Count == 0), Is.True, "realm 3 sleeps once unobserved: its clusters reach no system (RLM-04)");
    }

    [Test]
    public void ATierFilterAndARealmNarrowingIntersect()
    {
        using var dbe = Engine(out var byRealm);
        foreach (var realm in dbe.RealmTable.Registered)
        {
            realm.Grid.ResetAllTiers(SimTier.Tier3);
            for (var i = 0; i < PerRealm / 2; i++)
            {
                for (ushort r = 0; r < 3; r++)
                {
                    realm.Grid.SetCellTier(realm.Grid.WorldToCellKey(5 + (10 * i), 5 + (10 * r), 0), SimTier.Tier0);
                }
            }
        }

        var runs = Runs(dbe, [new RealmId(1)], true, SimTier.Tier0);
        Assert.That(runs, Is.All.EquivalentTo(byRealm[1].Take(PerRealm / 2)), "Tier0 cells of realm 1 only");
    }

    [Test]
    [VerifiesRule("RLM-07")]
    public void ANarrowedChangeFilterSeesItsRealmsChangesOnly()
    {
        using var dbe = Engine(out var byRealm);
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var delivered = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        var ticks = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");

            // One entity of realm 0 and one of realm 1 written every tick.
            dag.CallbackSystem("Write", ctx =>
            {
                ctx.Transaction.OpenMut(byRealm[0][0]).Write(RealmUnit.Pos).Tag++;
                ctx.Transaction.OpenMut(byRealm[1][0]).Write(RealmUnit.Pos).Tag++;
                Interlocked.Increment(ref ticks);
            });
            dag.QuerySystem("Reactive", ctx =>
            {
                var bag = delivered.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: true, changeFilter: [typeof(RealmPos)], realms: [new RealmId(1)], after: "Write");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 8, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var all = delivered.Values.SelectMany(b => b).ToHashSet();
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain(byRealm[1][0]), "realm 1's change is delivered");
            Assert.That(all.Any(id => !byRealm[1].Contains(id)), Is.False, "nothing of another realm, changed or not");
        });
    }

    [Test]
    [VerifiesRule("RLM-07")]
    public void ANarrowedChangeFilterOverTheDirtySetSeesItsRealmsChangesOnly()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.RegisterComponentFromAccessor<RealmScore>();
        dbe.ConfigureRealms(2);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Grid()));
        dbe.InitializeArchetypes();
        var ids = new EntityId[2][];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (ushort r = 0; r < 2; r++)
            {
                ids[r] = new EntityId[PerRealm];
                for (var i = 0; i < PerRealm; i++)
                {
                    ids[r][i] = tx.Spawn<ScoredUnit>(ScoredUnit.Pos.Set(At(5 + (10 * i), 5 + (10 * r), r)), ScoredUnit.Score.Set(new RealmScore { Value = i }));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        using (dbe)
        {
            using var txView = dbe.CreateQuickTransaction();
            using var view = txView.Query<ScoredUnit>().ToView();
            var delivered = new ConcurrentBag<EntityId>();
            var ticks = 0;
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");

                // One entity of each realm scored every tick: only realm 1's may reach the narrowed system, and only that one.
                dag.CallbackSystem("Write", ctx =>
                {
                    ctx.Transaction.OpenMut(ids[0][0]).Write(ScoredUnit.Score).Value++;
                    ctx.Transaction.OpenMut(ids[1][0]).Write(ScoredUnit.Score).Value++;
                    Interlocked.Increment(ref ticks);
                });
                dag.QuerySystem("Reactive", ctx =>
                {
                    foreach (var id in ctx.Entities)
                    {
                        delivered.Add(id);
                    }
                }, input: () => view, parallel: true, changeFilter: [typeof(RealmScore)], realms: [new RealmId(1)], after: "Write");
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 8, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
            Assert.That(delivered.ToHashSet(), Is.EquivalentTo(new[] { ids[1][0] }), "the one entity changed in realm 1, and nothing of realm 0");
        }
    }

    [Test]
    public void ANarrowingOnACallbackSystemOrBeyondTheRealmCountIsRefused()
    {
        using var dbe = Engine(out _);
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        Assert.That(() => TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Walk", _ => { }, realms: [new RealmId(1)]);
        }, new RuntimeOptions { WorkerCount = 1 }).Dispose(), Throws.InvalidOperationException.With.Message.Contains("cluster archetype"), "no input");
        Assert.That(() => TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Walk", _ => { }, input: () => view, realms: [new RealmId(9)]);
        }, new RuntimeOptions { WorkerCount = 1 }).Dispose(), Throws.InvalidOperationException.With.Message.Contains("realm ids"));
        Assert.That(() => TyphonRuntime.Create(dbe, _ => { }, new RuntimeOptions { WorkerCount = 1 }).Dispose(), Throws.Nothing);
        Assert.That(() => new SystemBuilder().InRealms(), Throws.ArgumentException);
        Assert.That(() => new SystemBuilder().InRealm(RealmId.None), Throws.ArgumentException);
    }
}
