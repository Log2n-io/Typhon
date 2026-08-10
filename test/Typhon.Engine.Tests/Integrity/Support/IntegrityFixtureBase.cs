using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// Builds a real database through the normal engine path, closes it, and hands the tests a bundle to read as bytes.
/// </summary>
/// <remarks>
/// The separation is the point of the feature and therefore the point of the fixture: a test that scanned through a live
/// engine would be testing something else. Every derived fixture gets a database of its own, named after the running
/// test, so a corruption in one can never leak into another.
/// </remarks>
internal abstract class IntegrityFixtureBase
{
    private string _root;
    private ServiceProvider _serviceProvider;

    /// <summary>Database name derived from the running test, sanitised and length-capped to the 63-char limit.</summary>
    protected static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "Tdk_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }

            return prefix + name;
        }
    }

    /// <summary>Directory holding the bundle.</summary>
    protected string DbDir => Path.Combine(_root, "db");

    /// <summary>Absolute path to the <c>.typhon</c> bundle under test.</summary>
    protected string BundlePath => Path.Combine(DbDir, $"{CurrentDatabaseName}.typhon");

    /// <summary>The bundle's own <c>wal/</c> directory — inside the bundle, as in a real deployment.</summary>
    protected string WalDir => Path.Combine(BundlePath, "wal");

    /// <summary>The live provider, or <c>null</c> once <see cref="CloseEngine"/> has run.</summary>
    protected ServiceProvider Provider => _serviceProvider;

    [SetUp]
    public void SetUpFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", GetType().Name, CurrentDatabaseName);
        Directory.CreateDirectory(DbDir);
        // The WAL goes INSIDE the bundle, which is where a real one lives — `typhon ui` and the engine both produce
        // `<name>.typhon/{data,wal/}`. Putting it beside the bundle instead (as the older integrity fixture does) leaves
        // the scanner looking at a bundle with no log, so any damage that also disturbs the checkpoint watermark draws a
        // second, spurious finding about a missing WAL. A damage fixture has to start from a realistic shape or its
        // findings are about the fixture.
        Directory.CreateDirectory(WalDir);

        _serviceProvider = BuildProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDownFixture()
    {
        CloseEngine();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup; a leaked handle must not fail an otherwise-green test
        }
    }

    /// <summary>
    /// Disposes the engine so the bundle can be read — and written — as bytes.
    /// </summary>
    /// <remarks>
    /// Mandatory before any damage: the data file is held with <c>FileShare.Read</c> while an engine is open, so a
    /// corruption attempted against a live database fails with a sharing violation rather than doing nothing quietly.
    /// </remarks>
    protected void CloseEngine()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }

    /// <summary>Builds a provider over the same on-disk database, for reopen-after-damage tests.</summary>
    protected ServiceProvider ReopenProvider() => BuildProvider();

    /// <summary>Builds a small, fully-committed database and closes it cleanly.</summary>
    protected void BuildHealthyDatabase(int entityCount = 64)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < entityCount; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();
        }

        CloseEngine();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = DbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = WalDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1
                };
            });

        return services.BuildServiceProvider();
    }
}
