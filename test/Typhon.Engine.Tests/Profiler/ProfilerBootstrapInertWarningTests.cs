using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Typhon.Engine.internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for <see cref="ProfilerBootstrap.BuildInertProfilerWarning"/> — the diagnostic that turns "profiler enabled, nothing came out" from a silent
/// no-op into one actionable line (#792).
/// </summary>
/// <remarks>
/// The bug these pin is not that the profiler failed; it is that it succeeded at doing nothing. A host set <c>TYPHON__PROFILER__TRACE</c> from inside
/// <c>Main</c>, which the frozen <see cref="TelemetryConfig"/> snapshot had already missed, and the run completed with no file, no warning and an unchanged
/// exit code. So the assertions below are about the branch condition and the *content* of the guidance — the message is the deliverable.
/// </remarks>
[TestFixture]
public sealed class ProfilerBootstrapInertWarningTests
{
    [Test]
    public void NullConfig_NoWarning()
    {
        Assert.That(ProfilerBootstrap.BuildInertProfilerWarning(null), Is.Null);
    }

    [Test]
    public void TraceFileConfigured_NoWarning()
    {
        var cfg = new ProfilerLaunchConfig { TraceFilePath = "run.typhon-trace" };
        Assert.That(cfg.IsActive, Is.True);
        Assert.That(ProfilerBootstrap.BuildInertProfilerWarning(cfg), Is.Null);
    }

    [Test]
    public void LivePortConfigured_NoWarning()
    {
        var cfg = new ProfilerLaunchConfig { LivePort = ProfilerLaunchConfig.DefaultLivePort };
        Assert.That(cfg.IsActive, Is.True);
        Assert.That(ProfilerBootstrap.BuildInertProfilerWarning(cfg), Is.Null);
    }

    [Test]
    public void BothChannelsConfigured_NoWarning()
    {
        var cfg = new ProfilerLaunchConfig { TraceFilePath = "run.typhon-trace", LivePort = 9100 };
        Assert.That(ProfilerBootstrap.BuildInertProfilerWarning(cfg), Is.Null);
    }

    [Test]
    public void NoChannel_WarnsAndNamesBothOutputChannels()
    {
        var warning = ProfilerBootstrap.BuildInertProfilerWarning(new ProfilerLaunchConfig());

        Assert.That(warning, Is.Not.Null);
        Assert.That(warning, Does.Contain("Typhon:Profiler:Trace"));
        Assert.That(warning, Does.Contain("Typhon:Profiler:Live"));
        Assert.That(warning, Does.Contain("TYPHON__PROFILER__TRACE"));
    }

    /// <summary>
    /// The load-bearing half. A reader who only learns "no channel configured" will set the environment variable in the most obvious place — <c>Main</c> —
    /// and reproduce #792 exactly. The message has to say that the configuration is already frozen by then.
    /// </summary>
    [Test]
    public void NoChannel_WarningStatesTheConfigurationIsReadBeforeMain()
    {
        var warning = ProfilerBootstrap.BuildInertProfilerWarning(new ProfilerLaunchConfig());

        Assert.That(warning, Does.Contain("BEFORE the process starts"));
        Assert.That(warning, Does.Contain("Main"));
    }

    /// <summary>
    /// The #792 configuration end to end: the master switch on and sub-gates enabled, but no <c>Trace</c> / <c>Live</c> key — which is what SpaceBattle
    /// produced once its late environment writes were ignored. <see cref="ProfilerLaunchConfig.FromConfiguration"/> must resolve that to inert, and the
    /// bootstrap must have something to say about it.
    /// </summary>
    [Test]
    public void EnabledWithNoTraceKey_ResolvesInertAndWarns()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Typhon:Profiler:Enabled"] = "true",
                ["Typhon:Profiler:CpuSampling:Enabled"] = "true",
                ["Typhon:Profiler:Scheduler:Enabled"] = "true",
            })
            .Build();

        var launch = ProfilerLaunchConfig.FromConfiguration(config);

        Assert.That(launch.IsActive, Is.False, "no Trace and no Live key means there is no output channel");
        Assert.That(ProfilerBootstrap.BuildInertProfilerWarning(launch), Is.Not.Null);
    }

    /// <summary>The same configuration plus a <c>Trace</c> key is the fixed state — it must resolve active and stay quiet.</summary>
    [Test]
    public void EnabledWithTraceKey_ResolvesActiveAndStaysQuiet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Typhon:Profiler:Enabled"] = "true",
                ["Typhon:Profiler:Trace"] = "spacebattle.typhon-trace",
            })
            .Build();

        var launch = ProfilerLaunchConfig.FromConfiguration(config);

        Assert.That(launch.TraceFilePath, Is.EqualTo("spacebattle.typhon-trace"));
        Assert.That(ProfilerBootstrap.BuildInertProfilerWarning(launch), Is.Null);
    }
}
