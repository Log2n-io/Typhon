using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// Own archetype: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).
[Component("Typhon.Test.ShutdownDrain.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ShutdownDrainPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class ShutdownDrainUnit : Archetype<ShutdownDrainUnit>
{
    public static readonly Comp<ShutdownDrainPos> Pos = Register<ShutdownDrainPos>();
}

/// <summary>
/// <see cref="TyphonRuntime.Shutdown"/> waits for the tick in flight, fence included, so the world read after it is a fenced one.
/// </summary>
/// <remarks>
/// Before, a stop landing between a tick's systems and its fence abandoned the fence: the systems' writes stayed committed and unfenced, and a box query
/// near a moved entity missed it until a later fence (seen as SwgTatooine's <c>GalaxyTests</c> failing about one run in fifty). The race is staged, not
/// waited for: the system moves an entity across the world and holds its tick open until the stop has closed the tick entry.
/// </remarks>
[TestFixture]
[NonParallelizable]
class ShutdownDrainTests : TestBase<ShutdownDrainTests>
{
    private const float World = 1_000f;

    [Test]
    [VerifiesRule("TP-01a")]
    public void AShutdownDuringATick_LeavesThatTickFenced()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ShutdownDrainPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), 100f));
        dbe.InitializeArchetypes();

        EntityId mover;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var start = new ShutdownDrainPos { Bounds = new AABB2F { MinX = 10f, MinY = 10f, MaxX = 11f, MaxY = 11f } };
            mover = tx.Spawn<ShutdownDrainUnit>(ShutdownDrainUnit.Pos.Set(in start));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var moved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var moves = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Mover", ctx =>
            {
                // Once: across the world, then the tick is held open until the stop has closed the tick entry.
                if (Interlocked.Increment(ref moves) != 1)
                {
                    return;
                }

                ref var pos = ref ctx.Transaction.OpenMut(mover).Write(ShutdownDrainUnit.Pos);
                pos.Bounds = new AABB2F { MinX = 900f, MinY = 900f, MaxX = 901f, MaxY = 901f };
                moved.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200, CostBasedChunking = false });
        runtime.Start();

        Assert.That(moved.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the system ran");
        var stop = Task.Run(runtime.Shutdown);
        Assert.That(SpinWait.SpinUntil(() => runtime.Scheduler.AreTicksStopped, TimeSpan.FromSeconds(10)), Is.True, "precondition: the stop began");
        release.Set();
        Assert.That(stop.Wait(TimeSpan.FromSeconds(20)), Is.True, "the stop returned");

        Assert.Multiple(() =>
        {
            Assert.That(Near(dbe, 900f), Does.Contain(mover), "the moved entity answers where it went: its tick was fenced");
            Assert.That(Near(dbe, 10f), Does.Not.Contain(mover), "and no longer where it was");
        });
    }

    // The entities a tight box query around (at, at) returns.
    private static HashSet<EntityId> Near(DatabaseEngine dbe, float at)
    {
        var ids = new HashSet<EntityId>();
        var box = new AABB2F { MinX = at - 0.5f, MinY = at - 0.5f, MaxX = at + 1.5f, MaxY = at + 1.5f };
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[16];
        var e = dbe.ClusterSpatialQuery<ShutdownDrainUnit>().AABB(in box);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (var i = 0; i < got; i++)
                {
                    ids.Add(buffer[i].Entity);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return ids;
    }
}
