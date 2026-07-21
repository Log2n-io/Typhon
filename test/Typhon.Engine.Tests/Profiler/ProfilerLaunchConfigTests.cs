using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for <see cref="ProfilerLaunchConfig"/> — the engine's host-launch convention parser. Pure data
/// transformations: argv → record, <see cref="IConfiguration"/> → record, merge of two records.
/// </summary>
[TestFixture]
public sealed class ProfilerLaunchConfigTests
{
    [Test]
    public void DefaultConfig_IsInactive()
    {
        var cfg = new ProfilerLaunchConfig();
        Assert.That(cfg.TraceFilePath, Is.Null);
        Assert.That(cfg.LivePort, Is.EqualTo(-1));
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0));
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromArgs_NoArgs_AllSentinels()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(Array.Empty<string>());
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromArgs_NullArgs_AllSentinels()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(null);
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromArgs_TraceOnly()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--trace", "out.bin" });
        Assert.That(cfg.TraceFilePath, Is.EqualTo("out.bin"));
        Assert.That(cfg.LivePort, Is.EqualTo(-1));
        Assert.That(cfg.IsActive, Is.True);
    }

    [Test]
    public void FromArgs_LiveWithExplicitPort()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live", "9001" });
        Assert.That(cfg.LivePort, Is.EqualTo(9001));
        Assert.That(cfg.IsActive, Is.True);
    }

    [Test]
    public void FromArgs_LiveWithoutPort_UsesDefault()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live" });
        Assert.That(cfg.LivePort, Is.EqualTo(ProfilerLaunchConfig.DefaultLivePort));
    }

    [Test]
    public void FromArgs_LiveWithNonNumericFollowing_TreatsAsNoPort()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live", "--trace", "out.bin" });
        Assert.That(cfg.LivePort, Is.EqualTo(ProfilerLaunchConfig.DefaultLivePort));
        Assert.That(cfg.TraceFilePath, Is.EqualTo("out.bin"));
    }

    [Test]
    public void FromArgs_LiveWaitMs()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live", "9100", "--live-wait", "5000" });
        Assert.That(cfg.LivePort, Is.EqualTo(9100));
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(5000));
    }

    [Test]
    public void FromArgs_LiveWaitMs_NegativeIsRejected()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live-wait", "-1" });
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0), "negative wait is silently dropped to 0 (no wait)");
    }

    [Test]
    public void FromArgs_LiveWaitMs_NonNumericIsIgnored()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live-wait", "garbage" });
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0));
    }

    [Test]
    public void FromArgs_DualOutput()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--trace", "out.bin", "--live", "9200" });
        Assert.That(cfg.TraceFilePath, Is.EqualTo("out.bin"));
        Assert.That(cfg.LivePort, Is.EqualTo(9200));
    }

    [Test]
    public void FromArgs_UnknownArgsIgnored()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--duration", "30", "--live", "9100", "--unknown" });
        Assert.That(cfg.LivePort, Is.EqualTo(9100));
    }

    [Test]
    public void FromConfiguration_Null_ReturnsInactive()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(null);
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromConfiguration_Empty_ReturnsInactive()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(Config());
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromConfiguration_TraceOnly()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(Config(("Typhon:Profiler:Trace", "/tmp/out.bin")));
        Assert.That(cfg.TraceFilePath, Is.EqualTo("/tmp/out.bin"));
        Assert.That(cfg.LivePort, Is.EqualTo(-1));
        Assert.That(cfg.IsActive, Is.True);
    }

    [Test]
    public void FromConfiguration_LiveWithPort()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(Config(("Typhon:Profiler:Live", "9300")));
        Assert.That(cfg.LivePort, Is.EqualTo(9300));
    }

    [Test]
    public void FromConfiguration_LiveNonNumeric_UsesDefault()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(Config(("Typhon:Profiler:Live", "yes")));
        Assert.That(cfg.LivePort, Is.EqualTo(ProfilerLaunchConfig.DefaultLivePort));
    }

    [Test]
    public void FromConfiguration_LiveWaitMs()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(Config(
            ("Typhon:Profiler:Live", "9100"),
            ("Typhon:Profiler:LiveWaitMs", "7500")));
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(7500));
    }

    [Test]
    public void FromConfiguration_LiveWaitMs_Negative_StaysZero()
    {
        var cfg = ProfilerLaunchConfig.FromConfiguration(Config(("Typhon:Profiler:LiveWaitMs", "-100")));
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0));
    }

    [Test]
    public void MergedWith_OverrideTraceWinsWhenSet()
    {
        var baseCfg = new ProfilerLaunchConfig { TraceFilePath = "/base.bin" };
        var over = new ProfilerLaunchConfig { TraceFilePath = "/over.bin" };
        Assert.That(baseCfg.MergedWith(over).TraceFilePath, Is.EqualTo("/over.bin"));
    }

    [Test]
    public void MergedWith_BaseRetainedWhenOverrideUnset()
    {
        var baseCfg = new ProfilerLaunchConfig { TraceFilePath = "/base.bin", LivePort = 9100 };
        var over = new ProfilerLaunchConfig();    // all sentinels
        var merged = baseCfg.MergedWith(over);
        Assert.That(merged.TraceFilePath, Is.EqualTo("/base.bin"));
        Assert.That(merged.LivePort, Is.EqualTo(9100));
    }

    [Test]
    public void MergedWith_OverridePortWinsWhenSet()
    {
        var baseCfg = new ProfilerLaunchConfig { LivePort = 9100 };
        var over = new ProfilerLaunchConfig { LivePort = 9200 };
        Assert.That(baseCfg.MergedWith(over).LivePort, Is.EqualTo(9200));
    }

    [Test]
    public void MergedWith_OverrideWaitWinsWhenSet()
    {
        var baseCfg = new ProfilerLaunchConfig { LiveWaitMs = 1000 };
        var over = new ProfilerLaunchConfig { LiveWaitMs = 5000 };
        Assert.That(baseCfg.MergedWith(over).LiveWaitMs, Is.EqualTo(5000));
    }

    [Test]
    public void MergedWith_NullOverride_ReturnsBase()
    {
        var baseCfg = new ProfilerLaunchConfig { LivePort = 9100 };
        Assert.That(baseCfg.MergedWith(null), Is.SameAs(baseCfg));
    }

    [Test]
    public void TypicalLayering_ConfigFirstThenArgsOverride()
    {
        // The standard pattern: typhon.telemetry.json provides defaults, CLI args take precedence —
        // exactly how AntHill.Harness layers its --trace/--live flags through the AddTyphonProfiler hook.
        var fileConfig = ProfilerLaunchConfig.FromConfiguration(Config(
            ("Typhon:Profiler:Trace", "/file.bin"),
            ("Typhon:Profiler:Live", "9100")));
        var cli = ProfilerLaunchConfig.FromArgs(new[] { "--live", "9200" });
        var final = fileConfig.MergedWith(cli);
        Assert.That(final.TraceFilePath, Is.EqualTo("/file.bin"), "trace from config preserved");
        Assert.That(final.LivePort, Is.EqualTo(9200), "port overridden by CLI");
    }

    /// <summary>Builds an in-memory <see cref="IConfiguration"/> from key/value pairs for <c>FromConfiguration</c> tests.</summary>
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }
}
