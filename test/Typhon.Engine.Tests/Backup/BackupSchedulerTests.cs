using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>A logical clock: the scheduler's monotonic and wall clocks, both starting from now and moved only by the test.</summary>
internal sealed class LogicalClock
{
    public long Mono { get; private set; } = Stopwatch.GetTimestamp();

    public DateTime Utc { get; set; } = DateTime.UtcNow;

    public void Advance(TimeSpan span)
    {
        Mono += (long)(span.TotalSeconds * Stopwatch.Frequency);
        Utc += span;
    }

    /// <summary>Moves the monotonic clock only (the wall clock was set elsewhere).</summary>
    public void AdvanceMono(TimeSpan span) => Mono += (long)(span.TotalSeconds * Stopwatch.Frequency);
}

/// <summary>Engine fixtures whose scheduler runs on a logical clock, its ticks driven by the test (no thread).</summary>
abstract class SchedulerTestBase<T> : BackupTestBase<T> where T : SchedulerTestBase<T>
{
    protected string Store;
    protected TimeSpan Interval;
    protected BackupRetentionPolicy Retention;
    protected bool AcceptRequests;
    protected HookedFileSystem StoreFs;
    protected LogicalClock Clock;
    protected Action<BackupScheduler> BeforeStart;

    public override void Setup()
    {
        Store = Path.Combine(TestStorage.ForFixture(typeof(T).Name), Guid.NewGuid().ToString("N"));
        Interval = TimeSpan.FromHours(1);
        Retention = null;
        AcceptRequests = true;
        StoreFs = new HookedFileSystem(StoreFileSystem.Instance);
        Clock = new LogicalClock();
        BeforeStart = null;
        base.Setup();
    }

    public override void TearDown()
    {
        base.TearDown();
        try
        {
            Directory.Delete(Store, true);
        }
        catch (IOException)
        {
        }
    }

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Backup = new BackupOptions
        {
            StorePath = Store, Interval = Interval, Retention = Retention, AcceptRequests = AcceptRequests, ManualTickForTest = true,
        };
    }

    /// <summary>Opens the engine with its scheduler on the logical clock and the faultable store file system.</summary>
    protected DatabaseEngine OpenOnClock(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        var scheduler = dbe.BackupScheduler;
        scheduler.UtcNow = () => Clock.Utc;
        scheduler.MonoNow = () => Clock.Mono;
        scheduler.StoreFileSystem = StoreFs;
        dbe.BackupCoordinator.WallClock = () => Clock.Utc;
        BeforeStart?.Invoke(scheduler);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>One tick; when it started a job, waits for the job to end (retention included) and returns its point, else null.</summary>
    protected static BackupPointInfo? Tick(DatabaseEngine dbe)
    {
        var scheduler = dbe.BackupScheduler;
        var before = scheduler.LastJob;
        using var ended = new SemaphoreSlim(0);
        scheduler.JobEndedProbe = () => ended.Release();
        try
        {
            scheduler.TickForTest();
            var job = scheduler.LastJob;
            if (job == null || ReferenceEquals(job, before))
            {
                return null;
            }

            Assert.That(ended.Wait(Budget), Is.True, "the job did not end");
            scheduler.TickForTest();   // the tick after a job reads the store and republishes
            return job.IsCompletedSuccessfully ? job.Result : default(BackupPointInfo?);
        }
        finally
        {
            scheduler.JobEndedProbe = null;
        }
    }

    /// <summary>One tick that must start no job.</summary>
    protected static void TickIdle(DatabaseEngine dbe, string why)
    {
        var before = dbe.BackupScheduler.LastJob;
        dbe.BackupScheduler.TickForTest();
        Assert.That(dbe.BackupScheduler.LastJob, Is.SameAs(before), why);
    }
}

/// <summary>
/// BKP-15's schedule (plan 1416 DL-18/19/20/30) on logical clocks: the grace, the interval, the backoff, the failures that only defer, a restart that
/// neither doubles nor skips a point, a restored timeline whose first point is due at once, and a wall clock set back a day.
/// </summary>
class BackupSchedulerTests : SchedulerTestBase<BackupSchedulerTests>
{
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-15")]
    public void AfterTheGrace_APoint_ThenOneEveryInterval()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 50);

        TickIdle(dbe, "within the grace");
        Assert.That(dbe.Backup.Status.Reason, Is.EqualTo(BackupHealthReason.FirstPointPending));
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Full), "no point on this timeline: due at once after the grace");
        Assert.That(dbe.Backup.Status.Health, Is.EqualTo(BackupHealth.Ok));

        Clock.Advance(TimeSpan.FromMinutes(59));
        TickIdle(dbe, "the interval has not elapsed");
        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Incremental));
        Assert.That(dbe.Backup.Status.PointsTotal, Is.EqualTo(2));
    }

    /// <summary>
    /// DL-20: counted failures back off from a minute, doubling up to a quarter of the interval; three make health Failing; a success resets.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void Failures_BackOff_AndThreeAreFailing_AndASuccessResets()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 50);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null, "premise: a restorable point, so the failures below are what health reports");
        StoreFs.Fault = (op, _) => op == nameof(IStoreFileSystem.TryLockExclusive) ? new IOException("the share is gone") : null;

        Clock.Advance(Interval);
        Assert.That(Tick(dbe), Is.Null);
        foreach (var (wait, failures) in new[] { (60, 2), (120, 3) })
        {
            Clock.Advance(TimeSpan.FromSeconds(wait - 1));
            TickIdle(dbe, $"within the {wait} s backoff");
            Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(Tick(dbe), Is.Null, "the store still fails");
            Assert.That(dbe.Backup.Status.ConsecutiveFailures, Is.EqualTo(failures));
        }

        Assert.That((dbe.Backup.Status.Health, dbe.Backup.Status.Reason), Is.EqualTo((BackupHealth.Failing, BackupHealthReason.ConsecutiveFailures)));
        StoreFs.Fault = null;
        Clock.Advance(TimeSpan.FromSeconds(239));
        TickIdle(dbe, "within the 240 s backoff");
        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(Tick(dbe), Is.Not.Null);
        Assert.That((dbe.Backup.Status.Health, dbe.Backup.Status.ConsecutiveFailures), Is.EqualTo((BackupHealth.Ok, 0)));
    }

    /// <summary>DL-20: another writer holding the store defers the point; it is not a failure.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void AHeldStore_DefersThePoint_AndCountsNoFailure()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 50);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null);

        using (BackupStore.OpenForMaintenance(Store, "prune"))
        {
            Clock.Advance(Interval);
            Assert.That(Tick(dbe), Is.Null);
            Assert.That((dbe.Backup.Status.ConsecutiveFailures, dbe.Backup.Status.FailuresTotal), Is.EqualTo((0, 0L)), "a held store is not a failure");
        }

        Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.That(Tick(dbe), Is.Not.Null, "retried a minute later");
    }

    /// <summary>DL-19: a restart reads the last success from the store: a point ten minutes old is due fifty minutes later, not at the grace.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-15")]
    public void ARestart_NeitherDoublesNorSkipsAPoint()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = OpenOnClock(scope);
            Spawn(dbe, 50);
            Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.That(Tick(dbe), Is.Not.Null);
        }

        Clock = new LogicalClock { Utc = DateTime.UtcNow.AddMinutes(10) };
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = OpenOnClock(scope);
            Clock.Advance(TimeSpan.FromSeconds(31));
            TickIdle(dbe, "the store's newest point is ten minutes old");
            Assert.That(dbe.Backup.Status.Health, Is.EqualTo(BackupHealth.Ok), "the restart reads its last success from the store");
            Clock.Advance(TimeSpan.FromMinutes(48));
            TickIdle(dbe, "48.5 minutes into an interval that began ten minutes before the restart");
            Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Incremental), "due an interval after the point, and the chain continues");
        }
    }

    /// <summary>DL-19: after a restore (a new timeline), the old timeline's points do not count: the first point is due at the grace, full.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-15")]
    public void ANewTimeline_TakesItsFirstPointAfterTheGrace()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = OpenOnClock(scope);
            Spawn(dbe, 50);
            Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.That(Tick(dbe), Is.Not.Null);
            dbe.AdoptTimeline(Guid.NewGuid(), dbe.TimelineId, 0, TimelineReason.Restore, keepLineage: false);   // a restored bundle's first open
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = OpenOnClock(scope);
            TickIdle(dbe, "within the grace");
            Assert.That(dbe.Backup.Status.Reason, Is.EqualTo(BackupHealthReason.FirstPointPending), "the old timeline's point is not this one's");
            Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Full));
        }
    }

    /// <summary>DL-19: the schedule runs on the monotonic clock: a wall clock set back a day delays nothing.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-15")]
    public void AWallClockSetBackADay_DelaysNothing()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 50);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null);

        Clock.Utc -= TimeSpan.FromDays(1);
        Clock.AdvanceMono(Interval);
        Assert.That(Tick(dbe), Is.Not.Null, "an interval of monotonic time later, the point is due whatever the wall clock says");
    }

    /// <summary>
    /// The archiver seam (impl/1418 §4.5, plan 1416 §6): stepped on every tick, its request for a point honoured within the grace, its condition judged as
    /// health (Stalled degrades, Diverged fails).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheArchiver_IsSteppedEveryTick_ItsRequestTakesAPoint_AndItsConditionIsHealth()
    {
        var archiver = new FakeArchiver();
        BeforeStart = s => s.ArchiverFactory = () => archiver;
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 20);

        TickIdle(dbe, "within the grace, nothing asked");
        Assert.That(archiver.Steps, Is.EqualTo(1));
        archiver.Immediate = true;
        Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Full), "the archiver's request is honoured at once");
        Assert.That(archiver.Immediate, Is.False, "consumed once");

        archiver.State = archiver.State with { Condition = WalArchiveCondition.Stalled };
        TickIdle(dbe, "");
        Assert.That((dbe.Backup.Status.Health, dbe.Backup.Status.Reason), Is.EqualTo((BackupHealth.Degraded, BackupHealthReason.ArchiveLag)));
        Assert.That(dbe.Backup.Status.Archive.State, Is.EqualTo(BackupArchiveState.Stalled));
        archiver.State = archiver.State with { Condition = WalArchiveCondition.Diverged };
        TickIdle(dbe, "");
        Assert.That((dbe.Backup.Status.Health, dbe.Backup.Status.Reason), Is.EqualTo((BackupHealth.Failing, BackupHealthReason.ArchiveFailed)));
    }

    private sealed class FakeArchiver : IWalArchiver
    {
        public int Steps;
        public bool Immediate;

        public WalArchiveState State { get; set; } = new(WalArchiveCondition.Archiving, Guid.Empty, 0, 0, 0, 0, 0, 0, 0, ArchiveGapReason.None,
            DateTime.MinValue, null);

        public void Step(DateTime utcNow, long monoNow, CancellationToken ct) => Steps++;

        public bool ConsumeImmediatePointRequest()
        {
            var immediate = Immediate;
            Immediate = false;
            return immediate;
        }

        public void Stop(TimeSpan budget)
        {
        }

        public void Dispose()
        {
        }
    }
}
