using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Disposing a runtime while a tick is still being dispatched must not fail that tick's fence.
/// </summary>
/// <remarks>
/// <para><c>DagScheduler.Dispose</c> used to dispose <c>_tickStartSignal</c> right after joining the workers, and only then let the base class stop the timer
/// thread. A tick already past the shutdown check keeps dispatching its tracks on that thread, and each ends with <c>_tickStartSignal.Reset()</c> — which
/// throws <see cref="ObjectDisposedException"/> on a disposed event. The runtime reported it as a <c>FenceFailure</c> in <c>'&lt;tick fence&gt;'</c>, to
/// the host's unhandled-exception hook: about one SWG demo run in ten on its last tick, and <c>FencePhaseFailureTests</c>' healthy-fence control under load.
/// </para>
/// <para>Deterministic: a system holds one tick open until shutdown has been requested, so Dispose always lands mid-tick.</para>
/// </remarks>
[TestFixture]
class SchedulerDisposeRaceTests : TestBase<SchedulerDisposeRaceTests>
{
    private static ClMigPos PointAt(float x, float y, int tag) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    [Test]
    [CancelAfter(30_000)]
    public void DisposingTheRuntimeMidTick_DoesNotFailTheFenceOnADisposedSignal()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000f, 1000f), 100f, reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f + i, 10f, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        Exception unhandled = null;
        var disposedFenceAborts = 0;
        var ticks = 0;
        TyphonRuntime runtime = null;
        using var inTick = new ManualResetEventSlim(false);

        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Gate").CallbackSystem("Gate", _ =>
            {
                // Hold the third tick open until Dispose has asked the workers to stop: the rest of this tick — its fence included — is then dispatched
                // after shutdown, which is exactly the window the signal used to be disposed in.
                if (Interlocked.Increment(ref ticks) == 3)
                {
                    inTick.Set();
                    SpinWait.SpinUntil(() => runtime.Scheduler.IsShutdownRequested, TimeSpan.FromSeconds(10));
                }
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200, EnableParallelFence = true });

        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
        runtime.OnTickAborted += (_, outcome) =>
        {
            if (outcome.FailedSystemException is ObjectDisposedException)
            {
                Interlocked.Increment(ref disposedFenceAborts);
            }
        };

        runtime.Start();
        Assert.That(inTick.Wait(TimeSpan.FromSeconds(15)), Is.True, "precondition: the gate never reached its tick");
        runtime.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(unhandled, Is.Null, $"disposing mid-tick reached the host's unhandled-exception hook: {unhandled}");
            Assert.That(disposedFenceAborts, Is.Zero, "a tick was aborted on an ObjectDisposedException");
        });
    }
}
