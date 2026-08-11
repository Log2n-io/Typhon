using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using Typhon.Schema.Definition;

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

    /// <summary>
    /// Reopens with the page cache at its floor, so the cache must evict rather than hold everything it touches.
    /// </summary>
    /// <remarks>
    /// Used to probe whether a code path flushes under memory pressure. A comfortably-sized cache never evicts on a
    /// fixture-sized database, so a probe run only at the default size establishes that nothing was written
    /// <i>voluntarily</i> — which is a much weaker claim than the one being tested.
    /// </remarks>
    protected ServiceProvider ReopenProviderWithMinimumCache() => BuildProvider(minimumCache: true);

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

    /// <summary>
    /// Builds a database whose archetype owns two index segments of different node strides, and closes it cleanly.
    /// </summary>
    /// <remarks>
    /// The default fixture schema declares no indexed field, so an index segment never exists in it and every
    /// <c>IDX</c> check quietly skips — which reads in a report exactly like an index check that passed. This schema
    /// (<c>SpiIdxNamed</c>, from the #661 fixtures) indexes both a <c>String64</c> and an <c>int</c>, so the archetype
    /// carries two roots with different node layouts: enough to exercise the directory and both strides.
    /// </remarks>
    /// <param name="entityCount">How many entities to spawn.</param>
    protected void BuildIndexedDatabase(int entityCount = 40)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SpiIdxNamed>();
            dbe.RegisterComponentFromAccessor<SpiIdxTag>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < entityCount; i++)
                {
                    using var tx = uow.CreateTransaction();
                    tx.Spawn<SpiIdxArch>(
                        SpiIdxArch.Data.Set(new SpiIdxNamed((String64)$"name{i:D4}", i * 7)),
                        SpiIdxArch.Tag.Set(new SpiIdxTag(i)));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();
        }

        CloseEngine();
    }

    /// <summary>
    /// Builds a database whose archetype carries a <b>non-unique</b> index, and closes it cleanly.
    /// </summary>
    /// <remarks>
    /// A non-unique index does not store locations in its leaves — it stores buffer ids, and the entities sharing a key
    /// live in a variable-sized buffer inside the index segment. That is a different value shape from every other
    /// fixture here, and it is the only one that exercises <c>IDX-07</c>. Several entities deliberately share each
    /// <c>Bucket</c> value, so the buffers actually hold more than one element.
    /// </remarks>
    /// <param name="entityCount">How many entities to spawn.</param>
    /// <param name="bucketCount">How many distinct key values to spread them over.</param>
    protected void BuildMultiValueIndexedDatabase(int entityCount = 40, int bucketCount = 5)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AxVerMulti>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < entityCount; i++)
                {
                    using var tx = uow.CreateTransaction();
                    tx.Spawn<AxPureVerMulti>(AxPureVerMulti.P.Set(new AxVerMulti
                    {
                        Key = i,
                        Bucket = i % bucketCount,
                        Weight = i * 1.5f,
                        Tag = i
                    }));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();
        }

        CloseEngine();
    }

    /// <summary>
    /// Builds a database over several flushed units of work, so the WAL holds more than one frame.
    /// </summary>
    /// <remarks>
    /// The single-UoW fixture produces a log with exactly one frame, and a log with one frame cannot express the
    /// distinction <c>WAL-02</c> exists for: every break in it is also the last thing in the file, which is what a
    /// crash mid-append looks like. Several flushes give the check something to be wrong about.
    /// </remarks>
    /// <param name="batches">How many units of work to flush.</param>
    /// <param name="perBatch">Entities per unit of work.</param>
    protected void BuildMultiFrameWalDatabase(int batches = 6, int perBatch = 8)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            // Checkpoint FIRST, then write. A checkpoint advances the replayable window, so anything logged before it
            // is no longer in the log — the obvious ordering (write, then checkpoint) leaves a single frame behind and
            // the fixture cannot express "a break before the tail" at all.
            using (var seed = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = seed.CreateTransaction())
                {
                    tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1, 1, 1)));
                    tx.Commit();
                }

                seed.Flush();
            }

            dbe.ForceCheckpoint();

            var next = 2;
            for (var b = 0; b < batches; b++)
            {
                using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
                for (var i = 0; i < perBatch; i++, next++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(next, next, next);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        CloseEngine();
    }

    private ServiceProvider BuildProvider(bool minimumCache = false)
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
                opts.DatabaseCacheSize = minimumCache
                    ? PagedMMF.MinimumCacheSize
                    : (ulong)PagedMMF.MinimumCacheSize * 4;
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
