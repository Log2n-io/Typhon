using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #614 (F1) AC8 — the live-engine accounting that D-9 rests on. A capture's routing ids are only meaningful if exactly one engine was alive for its
/// whole duration; this counter is how the exporter knows.
/// </summary>
/// <remarks>
/// The high-water mark, not the instantaneous count, is what gets read at close — because the case that silently corrupts a trace is a second engine that
/// appears <b>and disappears</b> inside the capture window. By the time the trace is finalized that engine is gone, the live count reads 1 again, and its
/// events are already interleaved into the file under a second routing-id space. A snapshot would call that trace clean.
/// </remarks>
[TestFixture]
[NonParallelizable] // mutates the process-global live-engine counter
class LiveEngineHighWaterTests : TestBase<LiveEngineHighWaterTests>
{
    [Test]
    public void HighWater_RecordsAnEngineThatAppearedAndVanishedInsideTheWindow()
    {
        ArchetypeRegistry.ResetLiveEngineHighWater();
        var baseline = ArchetypeRegistry.CurrentLiveEngineCount;
        Assert.That(ArchetypeRegistry.MaxLiveEngineCount, Is.EqualTo(baseline), "reset rebases the mark onto the current count");

        // A second engine comes and goes entirely within the capture window.
        ArchetypeRegistry.RegisterLiveEngine();
        ArchetypeRegistry.UnregisterLiveEngine();

        Assert.Multiple(() =>
        {
            Assert.That(ArchetypeRegistry.CurrentLiveEngineCount, Is.EqualTo(baseline), "it is gone by close…");
            Assert.That(ArchetypeRegistry.MaxLiveEngineCount, Is.EqualTo(baseline + 1), "…but the trace still contains its events, so the mark must remember");
        });

        ArchetypeRegistry.ResetLiveEngineHighWater();
    }

    [Test]
    public void Reset_ScopesTheMarkToTheCurrentCapture_NotTheProcessLifetime()
    {
        ArchetypeRegistry.RegisterLiveEngine();
        ArchetypeRegistry.UnregisterLiveEngine();
        var pollutedMark = ArchetypeRegistry.MaxLiveEngineCount;
        var current = ArchetypeRegistry.CurrentLiveEngineCount;
        Assert.That(pollutedMark, Is.GreaterThan(current), "precondition: a past engine has raised the mark above the live count");

        ArchetypeRegistry.ResetLiveEngineHighWater();

        Assert.That(ArchetypeRegistry.MaxLiveEngineCount, Is.EqualTo(current),
            "a new capture must not inherit a previous one's verdict — otherwise every capture after the first multi-engine window is flagged forever");
    }

    [Test]
    public void UnregisterLiveEngine_NeverDrivesTheCountNegative()
    {
        ArchetypeRegistry.ResetLiveEngineHighWater();
        var baseline = ArchetypeRegistry.CurrentLiveEngineCount;

        for (var i = 0; i <= baseline + 3; i++)
        {
            ArchetypeRegistry.UnregisterLiveEngine();
        }
        Assert.That(ArchetypeRegistry.CurrentLiveEngineCount, Is.Zero, "the floor holds");

        // Restore what this test tore down so sibling fixtures see an honest count.
        for (var i = 0; i < baseline; i++)
        {
            ArchetypeRegistry.RegisterLiveEngine();
        }
        ArchetypeRegistry.ResetLiveEngineHighWater();
        Assert.That(ArchetypeRegistry.CurrentLiveEngineCount, Is.EqualTo(baseline));
    }

    // The counter has to track real engines, not just respond to direct calls — this is the wiring that InitializeArchetypes / Dispose provide.
    [Test]
    public void ARealEngineRaisesTheCountWhileItIsAlive_AndReleasesItOnDispose()
    {
        ArchetypeRegistry.ResetLiveEngineHighWater();
        var baseline = ArchetypeRegistry.CurrentLiveEngineCount;

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();

            Assert.That(ArchetypeRegistry.CurrentLiveEngineCount, Is.EqualTo(baseline + 1), "an initialized engine counts as live");
            dbe.Dispose();
            Assert.That(ArchetypeRegistry.CurrentLiveEngineCount, Is.EqualTo(baseline), "and stops counting once disposed");

            dbe.Dispose();
            Assert.That(ArchetypeRegistry.CurrentLiveEngineCount, Is.EqualTo(baseline), "a double-dispose must not decrement twice — that would mask a live peer");
        }

        Assert.That(ArchetypeRegistry.MaxLiveEngineCount, Is.EqualTo(baseline + 1), "the mark recorded the peak, which is what the exporter reads at close");
        ArchetypeRegistry.ResetLiveEngineHighWater();
    }
}
