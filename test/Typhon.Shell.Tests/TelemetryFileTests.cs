using System.IO;
using NUnit.Framework;
using Typhon.Shell.Telemetry;

namespace Typhon.Shell.Tests;

/// <summary>
/// Verifies <c>typhon telemetry trace</c> support in <see cref="TelemetryFile"/> (#532/F2-T3): the string
/// <c>Typhon:Profiler:Trace</c> path round-trips through save/load and set/clear preserves the boolean gate flags.
/// </summary>
[TestFixture]
public sealed class TelemetryFileTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "typhon-telemetry-tests", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void Trace_RoundTrips_And_PreservesGateFlags()
    {
        var file = Path.Combine(_dir, TelemetryFile.DefaultFileName);

        var model = TelemetryFile.Load(file);
        model.Set("", true);              // master Enabled
        model.Set("CpuSampling", true);   // a gate flag
        model.SetTrace("captures/app.typhon-trace");
        model.Save();

        var reloaded = TelemetryFile.Load(file);
        Assert.That(reloaded.TracePath, Is.EqualTo("captures/app.typhon-trace"), "trace path must round-trip");
        Assert.That(reloaded.TryGetExplicit("", out var master) && master, Is.True, "master flag preserved");
        Assert.That(reloaded.TryGetExplicit("CpuSampling", out var cpu) && cpu, Is.True, "gate flag preserved alongside the trace path");
    }

    [Test]
    public void ClearTrace_RemovesPath_ButKeepsGateFlags()
    {
        var file = Path.Combine(_dir, TelemetryFile.DefaultFileName);

        var model = TelemetryFile.Load(file);
        model.Set("CpuSampling", true);
        model.SetTrace("t.typhon-trace");
        model.Save();

        var loaded = TelemetryFile.Load(file);
        loaded.ClearTrace();
        loaded.Save();

        var afterClear = TelemetryFile.Load(file);
        Assert.That(afterClear.TracePath, Is.Null, "trace path removed");
        Assert.That(afterClear.TryGetExplicit("CpuSampling", out var cpu) && cpu, Is.True, "clearing the trace preserves gate flags");
    }

    [Test]
    public void Trace_Only_ProducesValidReloadableJson()
    {
        var file = Path.Combine(_dir, TelemetryFile.DefaultFileName);

        var model = TelemetryFile.Load(file);
        model.SetTrace("captures/solo.typhon-trace");   // no gate flags at all
        model.Save();

        var reloaded = TelemetryFile.Load(file);
        Assert.That(reloaded.TracePath, Is.EqualTo("captures/solo.typhon-trace"));
    }
}
