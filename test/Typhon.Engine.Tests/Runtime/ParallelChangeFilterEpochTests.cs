using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

[Component("Typhon.Test.CfEpoch.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CfEpochPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

/// <summary>Indexed: what makes a change filter scan the dirty set (the path #1063 took) rather than fall back to the whole view.</summary>
[Component("Typhon.Test.CfEpoch.Score", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct CfEpochScore
{
    [Index(AllowMultiple = true)]
    public int Value;
}

[Archetype]
partial class CfEpochUnit : Archetype<CfEpochUnit>
{
    public static readonly Comp<CfEpochPos> Pos = Register<CfEpochPos>();
    public static readonly Comp<CfEpochScore> Score = Register<CfEpochScore>();
}

/// <summary>
/// #1063: a parallel change-filtered QuerySystem's prepare scans the dirty set's cluster pages, and must hold an epoch while it does. It did not — in
/// Debug the accessor asserted, and the scheduler then hung the tick on the unfinished system; in Release the scan read pages nothing protected.
/// </summary>
[TestFixture]
[NonParallelizable]
class ParallelChangeFilterEpochTests : TestBase<ParallelChangeFilterEpochTests>
{
    [Test]
    [VerifiesRule("RT-01")]
    public void AParallelChangeFilterOverAnIndexedComponent_KeepsTicking_AndSeesItsChanges()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CfEpochPos>();
        dbe.RegisterComponentFromAccessor<CfEpochScore>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10));
        dbe.InitializeArchetypes();
        var ids = new EntityId[4];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var box = new AABB2F { MinX = 5 + (10 * i), MinY = 5, MaxX = 5 + (10 * i), MaxY = 5 };
                ids[i] = tx.Spawn<CfEpochUnit>(CfEpochUnit.Pos.Set(new CfEpochPos { Bounds = box }), CfEpochUnit.Score.Set(new CfEpochScore { Value = i }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        using (dbe)
        {
            using var txView = dbe.CreateQuickTransaction();
            using var view = txView.Query<CfEpochUnit>().ToView();
            var delivered = new ConcurrentBag<EntityId>();
            var ticks = 0;
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");
                dag.CallbackSystem("Write", ctx =>
                {
                    ctx.Transaction.OpenMut(ids[0]).Write(CfEpochUnit.Score).Value++;
                    Interlocked.Increment(ref ticks);
                });
                dag.QuerySystem("Reactive", ctx =>
                {
                    foreach (var id in ctx.Entities)
                    {
                        delivered.Add(id);
                    }
                }, input: () => view, parallel: true, changeFilter: [typeof(CfEpochScore)], after: "Write");
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

            runtime.Start();
            var advanced = SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 10, TimeSpan.FromSeconds(3));
            runtime.Shutdown();
            Assert.Multiple(() =>
            {
                Assert.That(advanced, Is.True, "the runtime keeps ticking");
                Assert.That(delivered.ToHashSet(), Is.EquivalentTo(new[] { ids[0] }), "the changed entity, and only it");
            });
        }
    }
}
