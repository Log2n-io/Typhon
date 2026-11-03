using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Shell.Commands;
using Typhon.Workbench.Fixtures;

namespace Typhon.Shell.Tests;

/// <summary>
/// T13 (AC-D2, plan 1416 §8.1; #1485): <c>typhon backup drill</c> in a child process — the CLI as cron runs it, with no schema option — drills a point
/// of a database holding every persisted shape: Versioned, SingleVersion and Transient components, component collections, unique and multi-value
/// indexes, spatial clusters, a polymorphic archetype tree (the SWG dev fixture). With no <c>--schema</c>, the CLI loads the assemblies the point's
/// manifest names from its default search order — here <c>Typhon.Workbench.Fixtures</c>, shipped beside it — at their exact identity, and opens
/// exactly.
/// </summary>
/// <remarks>
/// The point is a clean one (an offline backup of a closed database). Points whose tail holds the application's records, and builds that are not the
/// point's, are <c>BackupDrillSchemaCommandTests</c>' and the Workbench's <c>DrillWithSchemaAssembliesTests</c>'.
/// </remarks>
[TestFixture]
public sealed class DrillWithManifestAssembliesTests
{
    private static readonly TimeSpan ChildBudget = TimeSpan.FromSeconds(60);
    private string _root;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "typhon-shell-drill", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(90_000)]
    public void TheCli_DrillsAPointOfEveryShape_WithTheAssembliesItsManifestNames()
    {
        var fixture = FixtureDatabase.CreateOrReuse(Path.Combine(_root, "db"), force: true, Small, databaseName: "drilled");
        var store = Path.Combine(_root, "store");
        var point = BackupOperations.Create(fixture.TyphonFilePath, store, new BackupCreateOptions { Full = true });
        Assert.That(point.Kind, Is.EqualTo(BackupPointKind.Full));

        var (exit, stdout, stderr) = Typhon("backup", "drill", "--store", store, "--scratch", Path.Combine(_root, "scratch"), "--format", "json");

        Assert.That(exit, Is.EqualTo(BackupCli.ExitOk), $"stdout: {stdout}\nstderr: {stderr}");
        var report = JsonDocument.Parse(stdout).RootElement.GetProperty("report");
        Assert.That(report.GetProperty("outcome").GetString(), Is.EqualTo("passed"), stdout);
        Assert.That(report.GetProperty("verdict").GetString(), Is.EqualTo("sound"), stdout);
        Assert.That(report.GetProperty("point").GetProperty("backupId").GetUInt32(), Is.EqualTo(point.BackupId));
        Assert.That(BackupOperations.List(store).Drills[point.BackupId].Outcome, Is.EqualTo(BackupDrillOutcome.Passed), "the child recorded its drill");
    }

    /// <summary>A few entities of every archetype: the shapes matter here, not the volume.</summary>
    private static FixtureConfig Small => FixtureConfig.Default with
    {
        ResourceTypeCount = 8,
        GuildCount = 4,
        RecipeCount = 8,
        PlayerCount = 40,
        DepositCount = 20,
        HarvesterCount = 10,
        FactoryCount = 6,
        ItemCount = 60,
    };

    /// <summary>Runs the CLI (<c>typhon.dll</c> beside these tests) in its own process; returns its exit code and output.</summary>
    private static (int Exit, string Stdout, string Stderr) Typhon(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "typhon.dll"));
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var child = Process.Start(psi);
        Assert.That(child, Is.Not.Null);
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(ChildBudget))
        {
            child.Kill(entireProcessTree: true);
            Assert.Fail($"typhon {string.Join(' ', args)} did not exit within {ChildBudget}");
        }

        return (child.ExitCode, stdout.Result, stderr.Result);
    }
}
