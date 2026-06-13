using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// A1.5 (design 08 §1; rules AP-01/AP-02/AP-03): the commit pipeline appends a transaction's records to the WAL BEFORE making any of its changes
/// visible. Two behavioural checks back the in-engine Debug guard (<c>Transaction.PublishPreparedComponents</c> asserts the Append phase ran):
/// <list type="bullet">
/// <item><see cref="VisibleValue_ImpliesItsRecordsWereAppended"/> — once a committed value is observable, the WAL appended watermark already covers it
/// (visible ⟹ appended), the engine-level statement of AP-01.</item>
/// <item><see cref="ConcurrentReader_NeverObservesValueAheadOfItsAppend"/> — a reader thread polling a value that a writer thread advances 1..N never
/// observes the V-th committed value before V commits' worth of records have been appended. This is the targeted interleaving test the plan calls for.</item>
/// </list>
/// Both run on the real WAL pipeline (disk segments + commit buffer), so <c>DurabilityLog.LastAppendedLsn</c> is the genuine post-Append watermark.
/// </summary>
[TestFixture]
internal sealed class AppendBeforePublishTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

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
            const string prefix = "Abp_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(AppendBeforePublishTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
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
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        var testRoot = Directory.GetParent(_dbDir)?.FullName;
        try
        {
            if (testRoot != null && Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        Archetype<CompAArch>.Touch();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// AP-01 at the engine level: after a committed update is observable to a fresh reader, the WAL appended watermark has advanced past where it was
    /// before the commit — the value could not have become visible before its records were appended.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    public void VisibleValue_ImpliesItsRecordsWereAppended()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = OpenEngine(scope);

        EntityId id;
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            id = tx.Spawn<CompAArch>(CompAArch.A.Set(in CompASeed));
            tx.Commit();
            uow.Flush();
        }

        long appendedBefore = dbe.DurabilityLog.LastAppendedLsn;

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            ref var w = ref tx.OpenMut(id).Write(CompAArch.A);
            w = new CompA(42);
            tx.Commit();
            uow.Flush();
        }

        // The committed value is now visible to a fresh snapshot...
        using (var rtx = dbe.CreateQuickTransaction())
        {
            var v = rtx.Open(id).Read(CompAArch.A);
            Assert.That(v.A, Is.EqualTo(42), "the committed update must be visible");
        }

        // ...and its records were appended before it became visible (AP-01).
        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.GreaterThan(appendedBefore),
            "AP-01: a visible committed value must already have its records appended to the WAL");
    }

    /// <summary>
    /// AP-01 under concurrency: a writer advances one entity's value 1..N while a reader thread polls it with fresh snapshots. Each time the reader
    /// observes a new value V, at least V commits' worth of records must already be appended (LastAppendedLsn advanced by ≥ V from the baseline). Because
    /// every commit appends ≥ 1 record and the writer is sequential, observing value V before V appends would be an Append-after-publish violation.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    public void ConcurrentReader_NeverObservesValueAheadOfItsAppend()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = OpenEngine(scope);

        EntityId id;
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            id = tx.Spawn<CompAArch>(CompAArch.A.Set(in CompASeed)); // value 0
            tx.Commit();
            uow.Flush();
        }

        long baseAppended = dbe.DurabilityLog.LastAppendedLsn;
        const int n = 200;
        Exception readerError = null;
        var stop = false;

        var reader = Task.Run(() =>
        {
            try
            {
                int lastSeen = 0;
                while (!Volatile.Read(ref stop) && lastSeen < n)
                {
                    int v;
                    using (var rtx = dbe.CreateQuickTransaction())
                    {
                        v = rtx.Open(id).Read(CompAArch.A).A;
                    }

                    if (v > lastSeen)
                    {
                        long appended = dbe.DurabilityLog.LastAppendedLsn - baseAppended;
                        Assert.That(appended, Is.GreaterThanOrEqualTo(v),
                            $"AP-01: observed value {v} but only {appended} record(s) had been appended since baseline — visibility outran the Append");
                        lastSeen = v;
                    }
                }
            }
            catch (Exception ex)
            {
                readerError = ex;
            }
        });

        // Writer: advance the value 1..N. Deferred so there is no per-commit fsync wait (fast), but each commit still appends its record.
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred))
        {
            for (int i = 1; i <= n; i++)
            {
                using var tx = uow.CreateTransaction();
                ref var w = ref tx.OpenMut(id).Write(CompAArch.A);
                w = new CompA(i);
                tx.Commit();
            }

            uow.Flush();
        }

        Volatile.Write(ref stop, true);
        reader.Wait(TimeSpan.FromSeconds(10));

        if (readerError != null)
        {
            throw readerError;
        }
    }

    private static readonly CompA CompASeed = new(0);
}
