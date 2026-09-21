using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

[Component("Typhon.Test.Runtime.Issue907.Driver", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct Issue907Driver
{
    [Field]
    public int Value;
}

[Component("Typhon.Test.Runtime.Issue907.Vitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct Issue907Vitals
{
    [Field]
    public int Health;
}

[Archetype]
partial class Issue907Player : Archetype<Issue907Player>
{
    public static readonly Comp<Issue907Driver> Driver = Register<Issue907Driver>();
}

[Archetype]
partial class Issue907Creature : Archetype<Issue907Creature>
{
    public static readonly Comp<Issue907Vitals> Vitals = Register<Issue907Vitals>();
}

/// <summary>
/// Regression coverage for #907: a QuerySystem is not restricted to mutating its input archetype.
/// The target EntityId routes OpenMut to the target archetype; TryOpen remains the read-only lookup.
/// </summary>
[TestFixture]
[NonParallelizable]
class CrossArchetypeMutationTests : TestBase<CrossArchetypeMutationTests>
{
    [Test]
    public void QuerySystem_OpenMutOnForeignArchetype_WritesAndTickKeepsAdvancing()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<Issue907Driver>();
        dbe.RegisterComponentFromAccessor<Issue907Vitals>();
        dbe.InitializeArchetypes();

        EntityId creatureId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<Issue907Player>(Issue907Player.Driver.Set(new Issue907Driver { Value = 1 }));
            creatureId = tx.Spawn<Issue907Creature>(Issue907Creature.Vitals.Set(new Issue907Vitals { Health = 100 }));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var viewTx = dbe.CreateQuickTransaction();
        var players = viewTx.Query<Issue907Player>().ToView();
        var writer = new ForeignCreatureWriter(players, creatureId);

        Exception unhandled = null;
        var aborted = 0;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Issue907").Add(writer);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Scheduler.UnhandledExceptionCallback =
                (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
            runtime.OnTickAborted += (_, _) => Interlocked.Increment(ref aborted);

            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref writer.Runs) >= 3, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref writer.Runs), Is.GreaterThanOrEqualTo(3),
                "the runtime did not advance through multiple cross-archetype writes");
            Assert.That(Volatile.Read(ref aborted), Is.Zero,
                "cross-archetype OpenMut + Write must not abort the tick");
            Assert.That(unhandled, Is.Null,
                "cross-archetype OpenMut + Write must not surface an unhandled scheduler failure");
        });

        using (var verify = dbe.CreateQuickTransaction())
        {
            ref readonly var vitals = ref verify.Open(creatureId).Read(Issue907Creature.Vitals);
            Assert.That(vitals.Health, Is.LessThan(100), "the foreign-archetype write never reached the target");
        }

        players.Dispose();
    }

    private sealed class ForeignCreatureWriter : QuerySystem
    {
        private readonly EcsView<Issue907Player> _players;
        private readonly EntityId _creatureId;

        public int Runs;

        public ForeignCreatureWriter(EcsView<Issue907Player> players, EntityId creatureId)
        {
            _players = players;
            _creatureId = creatureId;
        }

        protected override void Configure(SystemBuilder b) => b
            .Name("Issue907ForeignCreatureWriter")
            .Phase(Phase.Simulation)
            .Input(() => _players)
            .Writes<Issue907Vitals>();

        protected override void Execute(TickContext ctx)
        {
            var creature = ctx.Transaction.OpenMut(_creatureId);
            ref var vitals = ref creature.Write(Issue907Creature.Vitals);
            vitals.Health--;
            Interlocked.Increment(ref Runs);
        }
    }
}
