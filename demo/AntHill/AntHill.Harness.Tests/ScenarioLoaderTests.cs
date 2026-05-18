using System;
using System.IO;
using AntHill.Core;
using NUnit.Framework;

namespace AntHill.Harness.Tests;

[TestFixture]
public sealed class ScenarioLoaderTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "anthill-scn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteYaml(string content)
    {
        var path = Path.Combine(_tempDir, "scenario.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private const string ValidYaml = """
        name: test-scenario
        seed: 12345
        world: 20000
        ants: 5000
        workers: [4, 8]
        duration: 3
        simulation:
          tierMode: uniform-t0
        trace: true
        assertions:
          exceptions: { max: 0 }
          antCount: { invariant: true }
        """;

    [Test]
    public void Load_ValidScenario_ParsesAllFields()
    {
        var s = ScenarioLoader.Load(WriteYaml(ValidYaml));

        Assert.That(s.Name, Is.EqualTo("test-scenario"));
        Assert.That(s.Seed, Is.EqualTo(12345));
        Assert.That(s.Ants, Is.EqualTo(5000));
        Assert.That(s.Workers, Is.EqualTo(new[] { 4, 8 }));
        Assert.That(s.Duration, Is.EqualTo(3));
        Assert.That(s.Trace, Is.True);
        Assert.That(ScenarioLoader.TryParseTierMode(s.Simulation.TierMode, out var mode), Is.True);
        Assert.That(mode, Is.EqualTo(TierMode.UniformT0));
        Assert.That(s.Assertions.Exceptions.Max, Is.EqualTo(0));
        Assert.That(s.Assertions.AntCount.Invariant, Is.True);
    }

    [Test]
    public void Load_FileNotFound_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(Path.Combine(_tempDir, "nope.yaml")));
        Assert.That(ex.Message, Does.Contain("not found"));
    }

    [Test]
    public void Load_MalformedYaml_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml("name: [unclosed")));
        Assert.That(ex.Message, Does.Contain("Malformed"));
    }

    [Test]
    public void Load_UnknownKey_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml(ValidYaml + "\nbogusKey: 1\n")));
        Assert.That(ex.Message, Does.Contain("bogusKey"));
    }

    [Test]
    public void Load_MissingName_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml("ants: 100\nworkers: [4]\nduration: 1\n")));
        Assert.That(ex.Message, Does.Contain("'name'"));
    }

    [Test]
    public void Load_NonPositiveAnts_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml("name: t\nants: 0\nworkers: [4]\nduration: 1\n")));
        Assert.That(ex.Message, Does.Contain("'ants'"));
    }

    [Test]
    public void Load_EmptyWorkers_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml("name: t\nants: 100\nworkers: []\nduration: 1\n")));
        Assert.That(ex.Message, Does.Contain("'workers'"));
    }

    [Test]
    public void Load_UnknownTierMode_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml(
                "name: t\nants: 100\nworkers: [4]\nduration: 1\nsimulation:\n  tierMode: bogus\n")));
        Assert.That(ex.Message, Does.Contain("tierMode"));
    }

    [Test]
    public void Load_BothDurationAndTicks_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml("name: t\nants: 100\nworkers: [4]\nduration: 1\nticks: 100\n")));
        Assert.That(ex.Message, Does.Contain("mutually exclusive"));
    }

    [Test]
    public void Load_WrongWorldSize_Throws()
    {
        var ex = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Load(WriteYaml("name: t\nants: 100\nworkers: [4]\nduration: 1\nworld: 50000\n")));
        Assert.That(ex.Message, Does.Contain("'world'"));
    }
}
