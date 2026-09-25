using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms D1 (RT-3) and D3 (RT-5): each realm's policy is decided once per tick, before any dispatch; a dormant realm's clusters reach no QuerySystem on
/// any path; an observer keeps a realm active; an entry wakes a dormant realm, which sleeps again after its hold.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmPolicyTests : TestBase<RealmPolicyTests>
{
    private const int SleepAfter = 2;

    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static RealmPos At(float x, float y, ushort realm, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    /// <summary>Realm 0 simulated always; realm 1 an interior that sleeps after <see cref="SleepAfter"/> unobserved ticks; realm 2 simulated always.</summary>
    private DatabaseEngine ThreeRealms(out EntityId[] in0, out EntityId[] in1, out EntityId[] in2)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.Realms.Register(new RealmId(1),
            new RealmConfig { Grid = Grid(), WhenUnobserved = RealmUnobserved.Sleep, UnobservedTickDivisor = 1, SleepAfterTicks = SleepAfter });
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid()));
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            in0 = [tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0))), tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 0)))];
            in1 = [tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 1))), tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 1))),
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(95, 95, 1)))];
            in2 = [tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 2)))];
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Runs a schedule of one tick counter and one QuerySystem recording the entities it saw per tick, until <paramref name="ticks"/> ran.</summary>
    private static Dictionary<long, HashSet<EntityId>> Dispatched(DatabaseEngine dbe, int ticks, bool parallel, SimTier tier = SimTier.All)
    {
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var seen = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        var ticksSeen = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", ctx =>
            {
                var bag = seen.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: parallel, tier: tier, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= ticks, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(ticksSeen, Is.GreaterThanOrEqualTo(ticks), "the runtime ran");
        return seen.ToDictionary(p => p.Key, p => p.Value.ToHashSet());
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [VerifiesRule("RLM-03")]
    [VerifiesRule("RLM-04")]
    public void DormantRealm_ZeroClustersDispatched(bool parallel, bool tierSystem)
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out var in2);
        if (tierSystem)
        {
            // Every entity's cell, in every realm, in Tier0: the tier index, not the runnable list, is what this system selects from.
            foreach (var realm in dbe.RealmTable.Registered)
            {
                foreach (var p in (ReadOnlySpan<float>)[5f, 55f, 95f])
                {
                    realm.Grid.SetCellTier(realm.Grid.WorldToCellKey(p, p, 0), SimTier.Tier0);
                }
            }
        }

        var perTick = Dispatched(dbe, 8, parallel, tierSystem ? SimTier.Tier0 : SimTier.All);

        var ticks = perTick.Keys.OrderBy(t => t).ToArray();
        var first = perTick[ticks[0]];
        Assert.That(first, Is.SupersetOf(in1), "a Sleep realm holds SleepAfterTicks before going dormant: its entities run at first");
        var last = perTick[ticks[^1]];
        Assert.That(last, Is.EquivalentTo(in0.Concat(in2)), "once dormant, realm 1's clusters reach no system; realms 0 and 2 still run");
        Assert.That(dbe.Realms.StateOf(new RealmId(1)), Is.EqualTo(RealmRunState.Dormant));
        Assert.That(dbe.Realms.StateOf(new RealmId(0)), Is.EqualTo(RealmRunState.Simulated));
        Assert.That(StateOf(dbe).RealmDispatch.ExcludedCount, Is.GreaterThan(0));
    }

    [Test]
    public void Observer_KeepsTheRealmActive()
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out var in2);
        using var pin = dbe.Realms.Observe(new RealmId(1));
        var perTick = Dispatched(dbe, 8, true);

        foreach (var (_, ids) in perTick)
        {
            Assert.That(ids, Is.SupersetOf(in1), "an observed realm is dispatched every tick");
        }

        Assert.That(dbe.Realms.StateOf(new RealmId(1)), Is.EqualTo(RealmRunState.Active));
        Assert.That(StateOf(dbe).RealmDispatch, Is.Null, "no realm ever dormant: the runnable index was never needed (RLM-04)");
    }

    [Test]
    [VerifiesRule("RLM-03")]
    public void AllRealmsRunnable_NothingIsFiltered()
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out var in2);
        dbe.Realms.Wake(new RealmId(1));
        var table = dbe.RealmTable;
        table.EvaluatePolicy();
        Assert.That((table.NonRunnableCount, table.StateOf(1)), Is.EqualTo((0, RealmRunState.Simulated)));

        // An entry-free realm with no pin sleeps after its hold — counted in evaluations, one per tick (RLM-03).
        for (var i = 0; i < SleepAfter; i++)
        {
            table.EvaluatePolicy();
            Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Simulated), $"still holding after {i + 2} evaluations");
        }

        table.EvaluatePolicy();
        Assert.That((table.NonRunnableCount, table.StateOf(1)), Is.EqualTo((1, RealmRunState.Dormant)));
    }

    [Test]
    public void AnEntry_WakesADormantRealm_WhichSleepsAgainAfterItsHold()
    {
        using var dbe = ThreeRealms(out var in0, out _, out _);
        var table = dbe.RealmTable;
        for (var i = 0; i <= SleepAfter; i++)
        {
            table.EvaluatePolicy();
        }

        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Dormant));
        var epoch = table.PolicyEpoch;

        // A teleport into the dormant realm: the fence's migration is the entry, and it wakes the realm from the next evaluation on.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(in0[0], RealmUnit.Pos, new RealmId(1), At(50, 50, 1));
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Dormant), "the policy moves at tick start, never mid-tick");
        table.EvaluatePolicy();
        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Simulated), "the entry woke it");
        Assert.That(table.PolicyEpoch, Is.Not.EqualTo(epoch));

        for (var i = 0; i < SleepAfter; i++)
        {
            table.EvaluatePolicy();
        }

        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Simulated), "the hold restarts at the entry");
        table.EvaluatePolicy();
        Assert.That(table.StateOf(1), Is.EqualTo(RealmRunState.Dormant));
    }

    [Test]
    [VerifiesRule("RLM-04")]
    public void ChangeFilter_DirtyInDormantRealm_NotDelivered()
    {
        using var dbe = ThreeRealms(out var in0, out var in1, out _);
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var delivered = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        var ticksSeen = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");

            // Writes one entity of realm 0 and one of realm 1 every tick, through the tick transaction: a callback is not realm-filtered.
            dag.CallbackSystem("Write", ctx =>
            {
                ctx.Transaction.OpenMut(in0[0]).Write(RealmUnit.Pos).Tag++;
                ctx.Transaction.OpenMut(in1[0]).Write(RealmUnit.Pos).Tag++;
                Interlocked.Increment(ref ticksSeen);
            });
            dag.QuerySystem("Reactive", ctx =>
            {
                var bag = delivered.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: true, changeFilter: [typeof(RealmPos)], after: "Write");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 8, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var ticks = delivered.Keys.OrderBy(t => t).ToArray();
        var early = ticks.Where(t => delivered[t].Contains(in1[0])).ToArray();
        Assert.That(early, Is.Not.Empty, "while the realm held, its changes were delivered");
        var last = delivered[ticks[^1]].ToHashSet();
        Assert.That(last, Does.Contain(in0[0]));
        Assert.That(last, Does.Not.Contain(in1[0]), "a change in a dormant realm reaches no change-filtered system");
    }
}
