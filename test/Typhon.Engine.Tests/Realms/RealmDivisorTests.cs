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

/// <summary>
/// Realms D4 (RT-6, RLM-05): a realm simulated at divisor N hands each of its clusters to a QuerySystem once every N runs of that system — exactly, keyed
/// on the system's run count so no TickDivisor aliases it — and the system integrates it over <c>ctx.Realms.DeltaTime(realm)</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmDivisorTests : TestBase<RealmDivisorTests>
{
    private const int Divisor = 4;
    private const int Planet1Entities = 8;

    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static RealmPos At(float x, float y, ushort realm) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm };

    /// <summary>Realm 0 at full rate; realm 1 simulated at divisor 4, its entities one per cell so each is its own cluster.</summary>
    private DatabaseEngine Engine(out EntityId in0, out EntityId[] in1)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(2);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.Realms.Register(new RealmId(1),
            new RealmConfig { Grid = Grid(), WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = Divisor });
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            in0 = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0)));
            in1 = new EntityId[Planet1Entities];
            for (var i = 0; i < Planet1Entities; i++)
            {
                in1[i] = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5 + (10 * i), 55, 1)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    /// <summary>Per system run (in order), the entities the system saw — the first <paramref name="runs"/> runs.</summary>
    private static List<HashSet<EntityId>> Runs(DatabaseEngine dbe, int runs, bool parallel, RealmRate rate = RealmRate.Divided, int tickDivisor = 1,
        Action<TickContext> probe = null)
    {
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<RealmUnit>().ToView();
        var seen = new ConcurrentDictionary<long, ConcurrentBag<EntityId>>();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Walk", ctx =>
            {
                probe?.Invoke(ctx);
                var bag = seen.GetOrAdd(ctx.TickNumber, _ => []);
                foreach (var id in ctx.Entities)
                {
                    bag.Add(id);
                }
            }, input: () => view, parallel: parallel, tickDivisor: tickDivisor, realmRate: rate);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => seen.Count >= runs, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(seen.Count, Is.GreaterThanOrEqualTo(runs), "the system ran");
        return seen.OrderBy(p => p.Key).Take(runs).Select(p => p.Value.ToHashSet()).ToList();
    }

    private static int[] Visits(List<HashSet<EntityId>> runs, EntityId[] ids) => ids.Select(id => runs.Count(r => r.Contains(id))).ToArray();

    [TestCase(true)]
    [TestCase(false)]
    [VerifiesRule("RLM-05")]
    public void Divisor4_EachClusterExactlyOnceEvery4Runs(bool parallel)
    {
        using var dbe = Engine(out var in0, out var in1);
        var runs = Runs(dbe, 2 * Divisor, parallel);

        Assert.That(Visits(runs, in1), Is.All.EqualTo(2), "8 runs of a divisor-4 realm: every cluster exactly twice");
        Assert.That(Visits(runs, [in0]), Is.All.EqualTo(2 * Divisor), "the full-rate realm runs every time");
        Assert.That(runs.Select(r => r.Count(in1.Contains)), Is.All.EqualTo(Planet1Entities / Divisor), "the realm's load spreads evenly over the runs");
    }

    [Test]
    [VerifiesRule("RLM-05")]
    public void Divisor4_UnderTickDivisor2_NoClusterStarves()
    {
        using var dbe = Engine(out _, out var in1);
        var runs = Runs(dbe, 2 * Divisor, true, tickDivisor: 2);
        Assert.That(Visits(runs, in1), Is.All.EqualTo(2), "keyed on the system's runs, not the tick: an even-ticks-only system still visits everything");
    }

    [Test]
    public void RealmRateFull_SeesEveryRunnableClusterEveryRun()
    {
        using var dbe = Engine(out var in0, out var in1);
        var runs = Runs(dbe, Divisor, true, RealmRate.Full);
        Assert.That(Visits(runs, in1.Append(in0).ToArray()), Is.All.EqualTo(Divisor));
    }

    [Test]
    public void AnObservedRealm_RunsAtFullRate()
    {
        using var dbe = Engine(out _, out var in1);
        using var pin = dbe.Realms.Observe(new RealmId(1));
        var runs = Runs(dbe, Divisor, true);
        Assert.That(Visits(runs, in1), Is.All.EqualTo(Divisor));
    }

    [Test]
    public void RealmDeltaTime_IsTheAmortizedDeltaTimeTimesTheDivisor()
    {
        using var dbe = Engine(out _, out _);
        var ratios1 = new ConcurrentBag<float>();
        var ratios0 = new ConcurrentBag<float>();
        Runs(dbe, 6, true, probe: ctx =>
        {
            if (ctx.AmortizedDeltaTime > 0f)
            {
                ratios1.Add(ctx.Realms.DeltaTime(new RealmId(1)) / ctx.AmortizedDeltaTime);
                ratios0.Add(ctx.Realms.DeltaTime(RealmId.Default) / ctx.AmortizedDeltaTime);
            }
        });

        Assert.That(ratios1, Is.Not.Empty.And.All.EqualTo((float)Divisor));
        Assert.That(ratios0, Is.All.EqualTo(1f));
    }
}
