using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Spectre.Console;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Shell.Tests;

[Component("Typhon.Shell.Test.LedgerEntry", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct LedgerEntry
{
    [Field] public int Value;
    [Field] public int Padding;
}

[Archetype]
internal class LedgerArch : Archetype<LedgerArch>
{
    public static readonly Comp<LedgerEntry> Entry = Register<LedgerEntry>();
}

/// <summary>
/// A real closed database whose WAL is archived into a real store (#1418): a full point, then one entity per transaction, the archiver driven on the
/// test's thread with a logical clock, every LSN and every instant the test needs to aim a restore at recorded.
/// </summary>
internal sealed class ArchivedStoreKit : IDisposable
{
    public const string DatabaseName = "Ledger";
    private const uint SegmentSize = 256 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly DateTime Epoch = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private ArchivedStoreKit()
    {
        Root = Path.Combine(Path.GetTempPath(), "typhon-shell-archive", Guid.NewGuid().ToString("N"));
        DbDir = Path.Combine(Root, "db");
        Store = Path.Combine(Root, "store");
        Directory.CreateDirectory(DbDir);
    }

    public string Root { get; }

    public string DbDir { get; }

    public string Store { get; }

    public Guid Timeline { get; private set; }

    /// <summary>The LSN of each commit's TxCommit, in order: the i-th commit spawned the (i+1)-th entity.</summary>
    public List<long> CommitLsns { get; } = [];

    /// <summary>The point the archive continues: its <c>P</c>.</summary>
    public long PointLsn { get; private set; }

    /// <summary>The archive's end once the session closed.</summary>
    public long ArchivedThroughLsn { get; private set; }

    /// <summary>The logical instant of each scheduler tick that sampled the WAL.</summary>
    public List<DateTime> TickTimes { get; } = [];

    /// <summary>Builds the store: a full point, <paramref name="commits"/> commits archived with a sample every <paramref name="perTick"/>.</summary>
    public static ArchivedStoreKit Build(int commits = 12, int perTick = 3, bool segmentPerTick = false)
    {
        var kit = new ArchivedStoreKit();
        using var provider = kit.Provider(kit.DbDir, archived: true);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<LedgerEntry>();
        dbe.InitializeArchetypes();
        kit.Timeline = dbe.TimelineId;
        var archiver = dbe.CreateWalArchiver(dbe.ArchiveConfiguration, kit.Store, null, startWorker: false);
        var seconds = 0;
        kit.Tick(archiver, seconds);
        var capture = dbe.BackupCoordinator.CaptureAsync(new FileSystemBackupStore(kit.Store), BackupKind.Full, CancellationToken.None);
        Assert.That(capture.Wait(Budget), Is.True, "the capture did not finish");
        kit.PointLsn = capture.Result.PointLsn;
        for (var i = 0; i < commits; i++)
        {
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                t.Spawn<LedgerArch>(LedgerArch.Entry.Set(new LedgerEntry { Value = i }));
                t.Commit();
            }

            kit.CommitLsns.Add(dbe.WalManager.LastPublishedLsn);
            if ((i + 1) % perTick == 0)
            {
                if (segmentPerTick)
                {
                    Switch(dbe);
                }

                kit.Tick(archiver, seconds += 10);
            }
        }

        Switch(dbe);
        kit.Tick(archiver, seconds += 10);
        kit.Tick(archiver, seconds + 1);
        kit.ArchivedThroughLsn = archiver.ArchivedThroughLsn;
        Assert.That(kit.ArchivedThroughLsn, Is.GreaterThanOrEqualTo(kit.CommitLsns[^1]), $"premise: every commit is archived ({archiver.State.LastError})");
        return kit;
    }

    private static void Switch(DatabaseEngine dbe)
    {
        var ticket = dbe.WalManager.RequestSegmentSwitch();
        var clock = Stopwatch.StartNew();
        while (dbe.WalManager.SwitchesServed < ticket)
        {
            Assert.That(clock.Elapsed, Is.LessThan(Budget), "the WAL writer did not serve the segment switch");
            Thread.Yield();
        }
    }

    private void Tick(WalArchiver archiver, int seconds)
    {
        var utc = Epoch.AddSeconds(seconds);
        TickTimes.Add(utc);
        archiver.Step(utc, (long)(seconds * (double)Stopwatch.Frequency), default);
        archiver.ArchivePending(default);
    }

    /// <summary>How many entities the bundle restored in <paramref name="directory"/> holds.</summary>
    public int CountRestored(string directory)
    {
        using var provider = Provider(directory, archived: false);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<LedgerEntry>();
        dbe.InitializeArchetypes();
        using var t = dbe.CreateQuickTransaction();
        return t.Query<LedgerArch>().Execute().Count();
    }

    /// <summary>The number of commits at or below <paramref name="lsn"/>.</summary>
    public int CommitsThrough(long lsn) => CommitLsns.Count(l => l <= lsn);

    private ServiceProvider Provider(string directory, bool archived)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = DatabaseName;
                o.DatabaseDirectory = directory;
                o.DatabaseCacheSize = 16UL * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(o =>
            {
                o.Wal = new WalWriterOptions { UseFUA = false, SegmentSize = SegmentSize, InitialSegmentSize = SegmentSize, PreAllocateSegments = 1 };
                o.Backup = archived
                    ? new BackupOptions
                    {
                        StorePath = Store,
                        Interval = TimeSpan.Zero,
                        Retention = null,
                        Archive = new ArchiveOptions { MaxSegmentAge = TimeSpan.FromSeconds(5), BacklogLimitBytes = 64L * SegmentSize },
                        ManualTickForTest = true,
                        ArchiverByTest = true,
                    }
                    : null;
            });
        return services.BuildServiceProvider();
    }

    /// <summary>Runs a verb against a plain console and a JSON writer, as <c>BackupCommandTests</c> does.</summary>
    public static (int Code, string Text, string Json) Run(Func<IAnsiConsole, TextWriter, int> verb)
    {
        var text = new StringWriter();
        var json = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(text),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 4000;
        var code = verb(console, json);
        return (code, text.ToString(), json.ToString());
    }

    public static JsonElement Json((int Code, string Text, string Json) run)
    {
        Assert.That(run.Json, Is.Not.Empty, "nothing was written as JSON: " + run.Text);
        return JsonDocument.Parse(run.Json).RootElement;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}
