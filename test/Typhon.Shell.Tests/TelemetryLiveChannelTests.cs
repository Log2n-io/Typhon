using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Shell.Telemetry;

namespace Typhon.Shell.Tests;

/// <summary>
/// <c>typhon telemetry live</c> — the live TCP channel (<c>Typhon:Profiler:Live</c>) as a first-class output channel
/// alongside <c>Trace</c>.
/// </summary>
/// <remarks>
/// The port stopped being a private launch detail when the engine began publishing it in the database's
/// <c>db.lock</c>: a Workbench opening a bundle its application holds reads it to discover where to watch. A value two
/// processes agree on has to be settable by the tool that writes the file, and — the assertion that actually matters —
/// has to survive a round-trip through it unchanged.
/// </remarks>
[TestFixture]
public sealed class TelemetryLiveChannelTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "typhon-telemetry-live", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string FilePath => Path.Combine(_dir, TelemetryFile.DefaultFileName);

    [Test]
    public void Live_RoundTrips_AlongsideTraceAndGateFlags()
    {
        var model = TelemetryFile.Load(FilePath);
        model.Set("", true);
        model.Set("Gauges", true);
        model.SetTrace("captures/app.typhon-trace");
        model.SetLive(9100, waitMs: 30_000);
        model.Save();

        var reloaded = TelemetryFile.Load(FilePath);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.LivePort, Is.EqualTo(9100));
            Assert.That(reloaded.LiveWaitMs, Is.EqualTo(30_000));
            Assert.That(reloaded.TracePath, Is.EqualTo("captures/app.typhon-trace"), "the two channels must coexist");
            Assert.That(reloaded.TryGetExplicit("Gauges", out var gauges) && gauges, Is.True, "gate flags must survive");
        });
    }

    /// <summary>
    /// The written file must be readable by the engine's own config path, not merely by our loader. A tool that writes
    /// a shape only it can read is worse than no tool.
    /// </summary>
    [Test]
    public void WhatWeWrite_IsWhatProfilerLaunchConfigReads()
    {
        var model = TelemetryFile.Load(FilePath);
        model.Set("", true);
        model.SetLive(9433, waitMs: 1_500);
        model.SetTrace("captures/x.typhon-trace");
        model.Save();

        var config = new ConfigurationBuilder()
            .AddJsonFile(FilePath, optional: false)
            .Build();
        var launch = ProfilerLaunchConfig.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(launch.LivePort, Is.EqualTo(9433));
            Assert.That(launch.LiveWaitMs, Is.EqualTo(1_500));
            Assert.That(launch.TraceFilePath, Is.EqualTo("captures/x.typhon-trace"));
            Assert.That(launch.IsActive, Is.True);
        });
    }

    [Test]
    public void ClearLive_RemovesTheWaitToo()
    {
        var model = TelemetryFile.Load(FilePath);
        model.SetLive(9100, waitMs: 5_000);
        model.ClearLive();
        model.Save();

        var reloaded = TelemetryFile.Load(FilePath);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.LivePort, Is.Null);
            // A wait with no port configures nothing and would read as a stale leftover next time someone opened the file.
            Assert.That(reloaded.LiveWaitMs, Is.Null, "the wait is meaningless without a port and must go with it");
        });
        Assert.That(File.ReadAllText(FilePath), Does.Not.Contain("LiveWaitMs"));
    }

    [Test]
    public void UnsetLive_EmitsNothing()
    {
        var model = TelemetryFile.Load(FilePath);
        model.Set("", true);
        model.Save();

        var text = File.ReadAllText(FilePath);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("\"Live\""), "an absent channel must not appear as null");
            Assert.That(text, Does.Not.Contain("LiveWaitMs"));
        });
        Assert.That(TelemetryFile.Load(FilePath).LivePort, Is.Null);
    }

    /// <summary>
    /// A hand-written file may quote the port. Accept it — the configuration binder does — rather than silently
    /// dropping a setting the user can see in their own file.
    /// </summary>
    [Test]
    public void QuotedPort_IsAccepted()
    {
        File.WriteAllText(FilePath, """{ "Typhon": { "Profiler": { "Enabled": true, "Live": "9100", "LiveWaitMs": "250" } } }""");

        var model = TelemetryFile.Load(FilePath);
        Assert.Multiple(() =>
        {
            Assert.That(model.LivePort, Is.EqualTo(9100));
            Assert.That(model.LiveWaitMs, Is.EqualTo(250));
        });
    }

    /// <summary>
    /// A non-numeric value is left unset rather than coerced. <c>ProfilerLaunchConfig</c> would read junk as the default
    /// port 9100; a config tool that quietly rewrites nonsense into a working port hides the user's typo from them.
    /// </summary>
    [Test]
    public void NonNumericPort_IsLeftUnsetRatherThanCoerced()
    {
        File.WriteAllText(FilePath, """{ "Typhon": { "Profiler": { "Enabled": true, "Live": "yes-please" } } }""");

        Assert.That(TelemetryFile.Load(FilePath).LivePort, Is.Null);
    }

    /// <summary>The emitted JSON must parse. The emitter hand-writes commas, and three optional scalars is where that breaks.</summary>
    [Test]
    public void EmittedJson_IsValid_ForEveryCombinationOfChannels()
    {
        foreach (var (trace, port, wait, gate) in new[]
        {
            ((string)null, (int?)null, (int?)null, false),
            ("t.typhon-trace", null, null, false),
            (null, 9100, null, false),
            (null, 9100, 500, false),
            ("t.typhon-trace", 9100, 500, false),
            ("t.typhon-trace", 9100, 500, true),
            (null, null, null, true),
        })
        {
            var path = Path.Combine(_dir, $"combo-{trace}-{port}-{wait}-{gate}.json".Replace("\\", "_"));
            var model = TelemetryFile.Load(path);
            if (trace != null) model.SetTrace(trace);
            if (port.HasValue) model.SetLive(port.Value, wait);
            if (gate) model.Set("Gauges", true);
            model.Save();

            var text = File.ReadAllText(path);
            Assert.DoesNotThrow(
                () => JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false }),
                $"invalid JSON for trace={trace}, port={port}, wait={wait}, gate={gate}:\n{text}");
        }
    }
}
