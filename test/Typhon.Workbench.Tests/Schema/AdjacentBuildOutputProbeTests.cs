using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Schema;

/// <summary>
/// The <c>bin/{Configuration}/{tfm}/</c> probe that makes the golden path work: `typhon new` → `dotnet run` → open in
/// the Workbench. The app's database lands in its working directory and the app's own assembly — the one the database
/// names as its schema — sits one level down in <c>bin/</c>, which the manifest search did not look in. The first thing
/// a new user saw was a red schema banner about binaries they had built five minutes earlier.
/// </summary>
[TestFixture]
public class AdjacentBuildOutputProbeTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-binprobe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void NoBinDirectory_ProbesNothing()
    {
        // The production case: a database that does not live beside a build tree. One failed Directory.Exists.
        Assert.That(EngineLifecycle.EnumerateAdjacentBuildOutputs(_tempDir), Is.Empty);
    }

    [Test]
    public void FindsTheFrameworkDirectoryUnderBin()
    {
        var tfm = Path.Combine(_tempDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(tfm);

        Assert.That(EngineLifecycle.EnumerateAdjacentBuildOutputs(_tempDir), Is.EqualTo(new[] { tfm }));
    }

    [Test]
    public void NewestConfigurationComesFirst()
    {
        // A stale Release build must not outrank the Debug build the user just produced.
        var release = Path.Combine(_tempDir, "bin", "Release", "net10.0");
        var debug = Path.Combine(_tempDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(release);
        Directory.CreateDirectory(debug);
        Directory.SetLastWriteTimeUtc(release, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(debug, DateTime.UtcNow);

        Assert.That(EngineLifecycle.EnumerateAdjacentBuildOutputs(_tempDir).First(), Is.EqualTo(debug));
    }

    [Test]
    public void DoesNotRecurseBelowTheFrameworkDirectory()
    {
        // Bounded on purpose: exactly bin/*/*. A recursive sweep of an arbitrary directory is slow, and it is a way to
        // load an assembly the user never meant to expose — `runtimes/` and `ref/` under a publish output would qualify.
        var tfm = Path.Combine(_tempDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(Path.Combine(tfm, "runtimes", "win-x64", "lib"));

        var probed = EngineLifecycle.EnumerateAdjacentBuildOutputs(_tempDir).ToArray();

        Assert.That(probed, Is.EqualTo(new[] { tfm }));
        Assert.That(probed.Any(p => p.Contains("runtimes", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void MissingOrEmptyParent_IsNotAnError()
    {
        Assert.That(EngineLifecycle.EnumerateAdjacentBuildOutputs(null), Is.Empty);
        Assert.That(EngineLifecycle.EnumerateAdjacentBuildOutputs(""), Is.Empty);
        Assert.That(EngineLifecycle.EnumerateAdjacentBuildOutputs(Path.Combine(_tempDir, "gone")), Is.Empty);
    }
}
