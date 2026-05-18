using System.Collections.Generic;
using System.Linq;
using AntHill.Core;
using NUnit.Framework;

namespace AntHill.Harness.Tests;

[TestFixture]
public sealed class ScenarioExpanderTests
{
    private static Scenario MakeScenario() => new()
    {
        Name = "swp",
        Seed = 99,
        Ants = 12345,
        World = 20_000,
        Workers = new List<int> { 4, 8, 16 },
        Duration = 5,
        Trace = true,
        Simulation = new SimulationSettings { TierMode = "uniform-t0" },
    };

    [Test]
    public void Expand_ProducesOneRunPerWorkerCount()
    {
        var runs = ScenarioExpander.Expand(MakeScenario());
        Assert.That(runs.Count, Is.EqualTo(3));
        Assert.That(runs.Select(r => r.WorkerCount).ToArray(), Is.EqualTo(new[] { 4, 8, 16 }));
    }

    [Test]
    public void Expand_CarriesConfigAndTierMode()
    {
        var run = ScenarioExpander.Expand(MakeScenario())[0];
        Assert.That(run.Config.Seed, Is.EqualTo(99));
        Assert.That(run.Config.AntCount, Is.EqualTo(12345));
        Assert.That(run.Config.WorkerCount, Is.EqualTo(4));
        Assert.That(run.Config.TierMode, Is.EqualTo(TierMode.UniformT0));
        Assert.That(run.DurationSeconds, Is.EqualTo(5));
    }

    [Test]
    public void Expand_TracePathsAreDistinctPerRun()
    {
        var runs = ScenarioExpander.Expand(MakeScenario());
        var paths = runs.Select(r => r.TracePath).ToArray();
        Assert.That(paths, Is.Unique);
        foreach (var p in paths)
        {
            Assert.That(p, Does.Contain(".typhon-trace"));
        }
        Assert.That(runs[0].TracePath, Does.Contain("w4"));
    }

    [Test]
    public void Expand_NoTrace_LeavesTracePathNull()
    {
        var s = MakeScenario();
        s.Trace = false;
        Assert.That(ScenarioExpander.Expand(s)[0].TracePath, Is.Null);
    }
}
