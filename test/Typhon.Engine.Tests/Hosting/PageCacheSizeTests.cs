using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Page-cache configuration (topic 4): the shipped default is 256 MiB (not the 2 MiB stress floor); the internal
/// <c>TestMode</c> flag allows a sub-2MiB cache and suppresses the below-recommended-size warning; the discoverable
/// <see cref="TyphonOptions.PageCacheSize"/> knob sets the size; and opening below the 64 MiB recommended minimum logs a
/// startup warning (suppressed in TestMode).
/// </summary>
public class PageCacheSizeTests
{
    private const ulong Mib = 1024UL * 1024UL;

    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(PageCacheSizeTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Test]
    public void Default_CacheSize_Is256MiB()
    {
        Assert.That(new PagedMMFOptions().DatabaseCacheSize, Is.EqualTo(256UL * Mib),
            "the shipped default must be the production 256 MiB, not the 2 MiB test-stress floor");
    }

    [Test]
    public void PublicByteConstants_ExposeTheSizingContract()
    {
        // The public cache-sizing surface is uniformly in bytes — these constants let a consumer discover the constraints in-code.
        Assert.That(PagedMMFOptions.PageSizeBytes, Is.EqualTo(8 * 1024));
        Assert.That(PagedMMFOptions.MinimumCacheSizeBytes, Is.EqualTo(2UL * Mib));
        Assert.That(PagedMMFOptions.DefaultCacheSizeBytes, Is.EqualTo(256UL * Mib));
        Assert.That(new PagedMMFOptions().DatabaseCacheSize, Is.EqualTo(PagedMMFOptions.DefaultCacheSizeBytes));
    }

    [Test]
    public void TestMode_AllowsSubMinimumCacheSize()
    {
        var o = new PagedMMFOptions
        {
            DatabaseName = "cache_db",
            DatabaseDirectory = _dir,
            DatabaseCacheSize = 8192, // one page — a valid multiple but far below the 2 MiB minimum
        };

        Assert.That(o.IsValid, Is.False, "a sub-2MiB cache must fail validation without TestMode");

        o.TestMode = true;
        Assert.That(o.IsValid, Is.True, "TestMode must allow a sub-2MiB cache for eviction-stress tests");
    }

    [Test]
    public void PageCacheSize_Knob_SetsDatabaseCacheSize()
    {
        var opts = new TyphonOptions().PageCacheSize(512UL * Mib);

        var applied = new ManagedPagedMMFOptions();
        opts.StorageConfigurator?.Invoke(applied);

        Assert.That(applied.DatabaseCacheSize, Is.EqualTo(512UL * Mib));
    }

    [Test]
    public void SmallCache_LogsWarning_UnlessTestMode()
    {
        // 8 MiB: a valid size (multiple of the page size, >= 2 MiB floor) but below the 64 MiB recommended minimum.
        var warningsWhenNormal = OpenAndCaptureWarnings(testMode: false);
        Assert.That(warningsWhenNormal.Exists(w => w.Contains("Page cache")), Is.True,
            "opening below the recommended minimum must log a page-cache warning");

        var warningsWhenTestMode = OpenAndCaptureWarnings(testMode: true);
        Assert.That(warningsWhenTestMode.Exists(w => w.Contains("Page cache")), Is.False,
            "TestMode must suppress the page-cache warning");
    }

    private List<string> OpenAndCaptureWarnings(bool testMode)
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(provider));

        var dbFile = Path.Combine(_dir, (testMode ? "tm" : "warn") + ".typhon");
        using (var dbe = DatabaseEngine.Open(dbFile, o => o
                   .ConfigureEngine(e => e.Wal.UseFUA = false)
                   .ConfigureStorage(s => { s.DatabaseCacheSize = 8UL * Mib; s.TestMode = testMode; }),
                   loggerFactory))
        {
            // opening is enough — the warning is emitted during PagedMMF construction
        }

        return provider.Warnings;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Warnings = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
