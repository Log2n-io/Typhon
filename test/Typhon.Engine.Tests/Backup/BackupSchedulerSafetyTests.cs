using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// BKP-16: the ops loop never endangers the engine and writes only to its store. A store failing in every way never escapes a tick and never stops the
/// engine committing; a <c>backup.request</c> selects auto or full and nothing else; a close waits for the tick at most one tick and leaves a parked one
/// behind.
/// </summary>
class BackupSchedulerSafetyTests : SchedulerTestBase<BackupSchedulerSafetyTests>
{
    private const string Marker = "BKP-16: a tick threw";
    private bool _manualTicks = true;

    public override void Setup()
    {
        _manualTicks = true;
        base.Setup();
    }

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Backup = new BackupOptions
        {
            StorePath = Store, Interval = Interval, Retention = Retention, AcceptRequests = AcceptRequests, ManualTickForTest = _manualTicks,
        };
    }

    public enum FaultKind
    {
        Io,
        Access,
        Defect,
    }

    [TestCase(FaultKind.Io)]
    [TestCase(FaultKind.Access)]
    [TestCase(FaultKind.Defect)]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-16")]
    public void AStoreFailingAtEveryOperation_NeverEscapesATick_AndTheEngineKeepsCommitting(FaultKind kind)
    {
        Func<Exception> fault = kind switch
        {
            FaultKind.Io => () => new IOException("the share is gone"),
            FaultKind.Access => () => new UnauthorizedAccessException("access denied"),
            _ => () => new InvalidOperationException("a defect below"),
        };
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        StoreFs.Fault = (_, _) => fault();

        AssertTicksNeverThrow(() =>
        {
            for (var i = 0; i < 5; i++)
            {
                Clock.Advance(TimeSpan.FromMinutes(20));
                Tick(dbe);
            }
        });

        Assert.That(dbe.Backup.Status.PointsTotal, Is.Zero);
        Spawn(dbe, 20);   // the engine still commits
        StoreFs.Fault = null;
        Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.That(Tick(dbe), Is.Not.Null, "once the store answers, the next attempt succeeds");
    }

    /// <summary>The check can fail: a tick that rethrows.</summary>
    [Test]
    [RuleMutant("BKP-16")]
    public void Mutant_ATickThatRethrows_IsReported() =>
        RuleMutants.AssertDetects("BKP-16", Marker, () => AssertTicksNeverThrow(() => throw new IOException("rethrown")));

    /// <summary>A request names the kind (here full) and nothing else counts: a path in it is ignored, the point goes to the configured store.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-16")]
    public void ARequest_SelectsOnlyTheKind_AndThePointGoesToTheConfiguredStore()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 20);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null);

        var elsewhere = Path.Combine(Path.GetDirectoryName(Store), Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var path = elsewhere.Replace("\\", "\\\\");
        WriteRequest(dbe, $$"""{"formatVersion":1,"requestId":"{{id}}","kind":"full","requestedBy":"test","storePath":"{{path}}"}""");
        var point = Tick(dbe);

        Assert.That(point?.Kind, Is.EqualTo(BackupKind.Full), "the kind is honoured: full although an incremental would do");
        Assert.That(point?.PackPath, Does.StartWith(Store));
        Assert.That(Directory.Exists(elsewhere), Is.False, "nothing is written anywhere else");
        Assert.That(dbe.Backup.Status.LastRequestId, Is.EqualTo(id), "the request's id is echoed");
        Assert.That(File.Exists(Path.Combine(dbe.MMF.BundleDirectory, "backup.request")), Is.False, "retired when its job started");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnUnreadableRequest_IsAnAutoRequest()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 20);
        WriteRequest(dbe, "{ not json");
        Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Full), "honoured within the grace: the first point, full");
        Assert.That(dbe.Backup.Status.LastRequestId, Is.EqualTo(Guid.Empty));
    }

    [Test]
    [CancelAfter(30_000)]
    public void WithRequestsRefused_ARequestIsLeftAlone()
    {
        AcceptRequests = false;
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        WriteRequest(dbe, """{"formatVersion":1,"kind":"full"}""");
        TickIdle(dbe, "requests are refused");
        Assert.That(File.Exists(Path.Combine(dbe.MMF.BundleDirectory, "backup.request")), Is.True, "left for whoever wrote it");
    }

    /// <summary>
    /// The tick does no store I/O: the store's facts are read off it. A refresh parked on a dead share leaves the archiver's step and the status
    /// heartbeat running, and the close does not wait for it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-16")]
    public void AStoreReadParkedOnADeadShare_NeverStallsTheTick()
    {
        var archiver = new StepArchiver();
        BeforeStart = s => s.ArchiverFactory = () => archiver;
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        StoreFs.BeforeAny = (op, path) =>
        {
            if (op == nameof(IStoreFileSystem.FileExists) && path.EndsWith("store.json", StringComparison.Ordinal))
            {
                parked.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            }
        };

        try
        {
            using var scope = ServiceProvider.CreateScope();
            var dbe = OpenOnClock(scope);
            var scheduler = dbe.BackupScheduler;
            TickWithin(scheduler);
            Assert.That(parked.Wait(Budget), Is.True, "premise: the first tick's refresh reads the store and parks");
            foreach (var condition in new[] { WalArchiveCondition.Stalled, WalArchiveCondition.Released })
            {
                archiver.State = archiver.State with { Condition = condition };
                Clock.Advance(BackupScheduler.TickPeriod);
                TickWithin(scheduler);
                Assert.That(dbe.Backup.Status.Archive.State, Is.EqualTo((BackupArchiveState)condition), "the heartbeat follows the archiver");
            }

            Assert.That(archiver.Steps, Is.EqualTo(3), "the archiver is stepped every tick");
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>One tick, which must return while a store read is parked.</summary>
    private static void TickWithin(BackupScheduler scheduler)
    {
        var tick = Task.Run(() => scheduler.TickForTest(awaitRefresh: false));
        Assert.That(tick.Wait(Budget), Is.True, "the tick stalled behind the store");
    }

    /// <summary>A step parked inside the tick (a defect below it): the close waits one tick for the scheduler, then leaves it behind, detached.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-16")]
    public void AClose_WaitsAtMostOneTickForAParkedScheduler()
    {
        _manualTicks = false;
        Interval = TimeSpan.Zero;
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var archiver = new StepArchiver
        {
            OnStep = () =>
            {
                parked.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            },
        };
        BeforeStart = s => s.ArchiverFactory = () => archiver;

        BackupScheduler scheduler;
        var close = Stopwatch.StartNew();
        try
        {
            using (var scope = ServiceProvider.CreateScope())
            {
                var dbe = OpenOnClock(scope);
                scheduler = dbe.BackupScheduler;
                Assert.That(parked.Wait(Budget), Is.True, "premise: the first tick parks in the archiver's step");
                close.Restart();
            }

            close.Stop();
        }
        finally
        {
            release.Set();
        }

        Assert.That(scheduler.Detached, Is.True);
        Assert.That(close.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6)), "the close waited one tick for the scheduler, not for the store");
    }

    private static void AssertTicksNeverThrow(Action ticks)
    {
        try
        {
            ticks();
        }
        catch (Exception e) when (e is not AssertionException)
        {
            Assert.Fail($"{Marker}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void WriteRequest(DatabaseEngine dbe, string json) =>
        File.WriteAllText(Path.Combine(dbe.MMF.BundleDirectory, "backup.request"), json, Encoding.UTF8);

    /// <summary>An archiver whose step a test can park, and whose state it sets.</summary>
    private sealed class StepArchiver : IWalArchiver
    {
        public int Steps;

        public Action OnStep { get; init; }

        public WalArchiveState State { get; set; } = new(WalArchiveCondition.Archiving, Guid.Empty, 0, 0, 0, 0, 0, 0, 0, ArchiveGapReason.None,
            DateTime.MinValue, null);

        public void Step(DateTime utcNow, long monoNow, CancellationToken ct)
        {
            Steps++;
            OnStep?.Invoke();
        }

        public bool ConsumeImmediatePointRequest() => false;

        public void Stop(TimeSpan budget)
        {
        }

        public void Dispose()
        {
        }
    }
}
