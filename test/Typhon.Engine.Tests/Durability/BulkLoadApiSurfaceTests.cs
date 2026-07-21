using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// API-surface and lifecycle tests for <see cref="BulkLoadSession"/>. Verifies:
/// <list type="bullet">
///   <item><see cref="DatabaseEngine.BeginBulkLoad"/> returns a non-null session with valid metadata.</item>
///   <item>The exclusive gate prevents two concurrent sessions.</item>
///   <item>The gate is released on <see cref="BulkLoadSession.Dispose"/> so a second session can open.</item>
///   <item>Closed sessions reject Spawn / Destroy / CompleteBulkLoad with <see cref="BulkSessionClosedException"/>.</item>
///   <item>The session emits a <c>BulkBegin</c> chunk on open (LSN captured in <see cref="BulkLoadSession.BulkBeginLsn"/>).</item>
/// </list>
/// </summary>
/// <remarks>Source-of-truth: <c>claude/design/Durability/BulkLoad/01-api.md</c>. Issue
/// <a href="https://github.com/nockawa/Typhon/issues/380">#380</a>.</remarks>
[TestFixture]
internal sealed class BulkLoadApiSurfaceTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _engine;

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }
            const int max = 63;
            const string prefix = "Bla_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }
            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(BulkLoadApiSurfaceTests));
        _dbDir = Path.Combine(root, "db");
        _walDir = Path.Combine(root, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _engine = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _engine.RegisterComponentFromAccessor<CompA>();
        _engine.InitializeArchetypes();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        try { if (Directory.Exists(_walDir)) Directory.Delete(_walDir, true); } catch { }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); } catch { }
    }

    [Test]
    public void BeginBulkLoad_ReturnsNonNullSession()
    {
        using var bulk = _engine.BeginBulkLoad();

        Assert.That(bulk, Is.Not.Null);
        Assert.That(bulk.IsClosed, Is.False);
        Assert.That(bulk.BulkSessionId, Is.GreaterThan(0));
        Assert.That(bulk.BulkBeginLsn, Is.GreaterThan(0), "BulkBegin should be emitted at session open");
        Assert.That(bulk.Options, Is.Not.Null);
    }

    [Test]
    public void BeginBulkLoad_NullOptions_UsesDefaults()
    {
        using var bulk = _engine.BeginBulkLoad(options: null);

        Assert.That(bulk.Options, Is.Not.Null);
        Assert.That(bulk.Options.ProgressBatchSize, Is.EqualTo(10_000));
        Assert.That(bulk.Options.CheckpointTimeout, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(bulk.Options.ProgressReporter, Is.Null);
    }

    [Test]
    public void BeginBulkLoad_TwiceWithoutClosing_Throws()
    {
        using var bulk1 = _engine.BeginBulkLoad();

        var ex = Assert.Throws<BulkSessionAlreadyActiveException>(() => _engine.BeginBulkLoad());
        Assert.That(ex.ActiveBulkSessionId, Is.EqualTo(bulk1.BulkSessionId));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.BulkSessionAlreadyActive));
    }

    [Test]
    public void BeginBulkLoad_AfterDispose_Succeeds()
    {
        var bulk1 = _engine.BeginBulkLoad();
        var firstId = bulk1.BulkSessionId;
        bulk1.Dispose();

        using var bulk2 = _engine.BeginBulkLoad();
        Assert.That(bulk2, Is.Not.Null);
        Assert.That(bulk2.BulkSessionId, Is.GreaterThan(firstId));
    }

    [Test]
    public void Dispose_WithoutComplete_IsSafe_AndIdempotent()
    {
        var bulk = _engine.BeginBulkLoad();

        Assert.DoesNotThrow(() => bulk.Dispose(), "first Dispose must succeed");
        Assert.That(bulk.IsClosed, Is.True);

        Assert.DoesNotThrow(() => bulk.Dispose(), "second Dispose must be a no-op");
    }

    [Test]
    public void CompleteBulkLoad_OnClosedSession_Throws()
    {
        var bulk = _engine.BeginBulkLoad();
        bulk.Dispose();

        var ex = Assert.Throws<BulkSessionClosedException>(() => bulk.CompleteBulkLoad());
        Assert.That(ex.BulkSessionId, Is.EqualTo(bulk.BulkSessionId));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.BulkSessionClosed));
    }

    [Test]
    public void Spawn_ReturnsValidEntityId_AndIncrementsCounter()
    {
        using var bulk = _engine.BeginBulkLoad();

        var id = bulk.Spawn<CompAArch>();
        Assert.That(id, Is.Not.EqualTo(default(EntityId)));
        Assert.That(bulk.EntitiesSpawned, Is.EqualTo(1));
    }

    [Test]
    public void Destroy_OnClosedSession_Throws()
    {
        var bulk = _engine.BeginBulkLoad();
        bulk.Dispose();

        Assert.Throws<BulkSessionClosedException>(() => bulk.Destroy(default));
    }

    [Test]
    public void BulkLoadOptions_HasReasonableDefaults()
    {
        var options = new BulkLoadOptions();

        Assert.That(options.ProgressBatchSize, Is.EqualTo(10_000));
        Assert.That(options.CheckpointTimeout.TotalMinutes, Is.EqualTo(5.0).Within(0.01));
        Assert.That(options.ProgressReporter, Is.Null);
    }
}
