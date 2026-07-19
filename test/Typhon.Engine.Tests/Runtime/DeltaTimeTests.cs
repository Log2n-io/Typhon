using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[NonParallelizable]
[TestFixture]
class DeltaTimeTests : TestBase<DeltaTimeTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void DeltaTime_SecondTick_IsPositive()
    {
        var dbe = SetupEngine();

        float capturedDeltaTime = -1f;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Capture", ctx =>
            {
                if (Interlocked.Increment(ref ticksSeen) == 2)
                {
                    capturedDeltaTime = ctx.DeltaTime;
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(capturedDeltaTime, Is.GreaterThan(0f),
            "DeltaTime should be positive on second tick");
        Assert.That(capturedDeltaTime, Is.LessThan(0.1f),
            "DeltaTime should be reasonable (< 100ms for 1000Hz tick)");
    }

    [Test]
    public void DeltaTime_FirstTick_IsZero()
    {
        var dbe = SetupEngine();

        float firstDeltaTime = -1f;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Capture", ctx =>
            {
                if (firstDeltaTime < 0f)
                {
                    firstDeltaTime = ctx.DeltaTime;
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(firstDeltaTime, Is.EqualTo(0f),
            "DeltaTime should be zero on first tick");
    }
}
