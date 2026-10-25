using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417 task 2: the Backups view's job registry — the state machine, one job per store, the retention of finished jobs, cancel, and the shutdown's
/// cancel-all. Bodies are gated, never timed.
/// </summary>
[TestFixture]
public sealed class BackupJobRegistryTests
{
    private DateTime _now;
    private BackupJobRegistry _registry;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        _registry = new BackupJobRegistry(NullLogger<BackupJobRegistry>.Instance, () => _now, Timeout.InfiniteTimeSpan);
        _store = Path.Combine(Path.GetTempPath(), "typhon-wb-jobs", Guid.NewGuid().ToString("N"), "store");
    }

    [TearDown]
    public void TearDown()
    {
        _registry.CancelAll(TimeSpan.FromSeconds(5));
        _registry.Dispose();
    }

    private static readonly BackupJobResult WithCreate = new(Create: new BackupCreateResultDto(null, Guid.Empty));

    private BackupJob Started(string kind, string store, Action<BackupJobContext> body)
    {
        var start = _registry.TryStart(new BackupJobSpec(kind, store), body);
        Assert.That(start.Admission, Is.EqualTo(BackupJobAdmission.Started));
        return start.Job;
    }

    private BackupJob StartGated(string store, ManualResetEventSlim gate, Action<BackupJobContext> after = null)
    {
        return Started(BackupJobKinds.Create, store, ctx =>
        {
            gate.Wait(ctx.Token);
            after?.Invoke(ctx);
        });
    }

    private static void Ended(BackupJob job) => Assert.That(job.Completion.Wait(TimeSpan.FromSeconds(10)), Is.True, "the body ended");

    [Test]
    [CancelAfter(10_000)]
    public void AJob_RunsThroughItsStates_AndKeepsItsResult()
    {
        using var gate = new ManualResetEventSlim();
        var seen = new List<string>();
        var job = StartGated(_store, gate, ctx =>
        {
            ctx.Report("capture", 4, 8, "pages");
            ctx.SetResult(WithCreate);
        });
        job.Changed += dto => { lock (seen) { seen.Add(dto.State); } };

        Assert.That(job.Snapshot().State, Is.AnyOf(BackupJobStates.Queued, BackupJobStates.Running));
        gate.Set();
        Ended(job);

        var done = job.Snapshot();
        Assert.That((done.State, done.Phase, done.Completed, done.Total, done.Unit), Is.EqualTo((BackupJobStates.Done, "capture", 8L, 8L, "pages")),
            "done reports its total");
        Assert.That(done.Create, Is.Not.Null, "the result slot is kept");
        Assert.That(done.EndedUtc, Is.EqualTo(_now));
        lock (seen)
        {
            Assert.That(seen[^1], Is.EqualTo(BackupJobStates.Done));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void OneJobPerStore_SpelledAnyWay_AndAnotherStoreRunsBeside()
    {
        using var gate = new ManualResetEventSlim();
        var first = StartGated(_store, gate);

        var spelled = OperatingSystem.IsWindows() ? _store.ToUpperInvariant() + Path.DirectorySeparatorChar : _store + Path.DirectorySeparatorChar;
        var busy = _registry.TryStart(new BackupJobSpec(BackupJobKinds.Verify, spelled), _ => { });
        Assert.That(busy.Admission, Is.EqualTo(BackupJobAdmission.StoreBusy));
        Assert.That(busy.Job, Is.SameAs(first), "the caller attaches to the running job");
        Assert.That(_registry.Running(_store), Is.SameAs(first));

        var other = StartGated(_store + "-other", gate);
        gate.Set();
        Ended(first);
        Ended(other);
        Assert.That(_registry.Running(_store), Is.Null);
        Ended(Started(BackupJobKinds.Verify, _store, _ => { }));   // a finished job no longer holds the store
    }

    [Test]
    [CancelAfter(10_000)]
    public void OneDrillPerWorkbench_WhateverTheStore()
    {
        using var gate = new ManualResetEventSlim();
        var drill = Started(BackupJobKinds.Drill, _store, ctx => gate.Wait(ctx.Token));

        var second = _registry.TryStart(new BackupJobSpec(BackupJobKinds.Drill, _store + "-other"), _ => { });
        Assert.That((second.Admission, second.Job), Is.EqualTo((BackupJobAdmission.DrillBusy, drill)), "checked under the registry's lock, with the start");
        var verify = Started(BackupJobKinds.Verify, _store + "-other", _ => { });
        gate.Set();
        Ended(drill);
        Ended(verify);
        Ended(Started(BackupJobKinds.Drill, _store + "-other", _ => { }));
    }

    // ── WB-12: one claim set for restores and session opens ───────────────────────────────────────────────────────────────────────────────────────

    private string Target => Path.Combine(Path.GetDirectoryName(_store)!, "restored", "world.typhon");

    private BackupJobStart StartRestore(string store, Action<BackupJobContext> body = null) =>
        _registry.TryStart(new BackupJobSpec(BackupJobKinds.Restore, store, Target: Target), body ?? (_ => { }));

    [Test]
    [CancelAfter(10_000)]
    public void AnOpenInProgress_HoldsOffARestoreIntoItsBundle_UntilItsClaimIsReleased()
    {
        var claim = _registry.ClaimOpen(Target + Path.DirectorySeparatorChar, out var restoring);
        Assert.That(claim, Is.Not.Null);
        Assert.That(restoring, Is.Null);

        Assert.That(StartRestore(_store).Admission, Is.EqualTo(BackupJobAdmission.TargetOpening), "the open's claim is seen by the restore's admission");

        claim.Dispose();
        claim.Dispose();
        var start = StartRestore(_store);
        Assert.That(start.Admission, Is.EqualTo(BackupJobAdmission.Started), "released (once, however often disposed)");
        Ended(start.Job);
    }

    [Test]
    [CancelAfter(10_000)]
    public void ARunningRestore_RefusesAnOpenOfItsTarget_AndAnotherRestoreIntoIt()
    {
        using var gate = new ManualResetEventSlim();
        var restore = StartRestore(_store, ctx => gate.Wait(ctx.Token));
        Assert.That(restore.Started, Is.True);

        Assert.That(_registry.ClaimOpen(Target, out var restoring), Is.Null);
        Assert.That(restoring, Is.SameAs(restore.Job));
        Assert.That(StartRestore(_store + "-other").Admission, Is.EqualTo(BackupJobAdmission.TargetBusy));

        gate.Set();
        Ended(restore.Job);
        using var claim = _registry.ClaimOpen(Target, out _);
        Assert.That(claim, Is.Not.Null, "an ended restore claims nothing");
    }

    [Test]
    [CancelAfter(10_000)]
    public void ARestoreIntoABundleASessionHolds_IsRefused_WithTheSession()
    {
        var holder = Guid.NewGuid();
        using var registry = new BackupJobRegistry(NullLogger<BackupJobRegistry>.Instance, () => _now, Timeout.InfiniteTimeSpan,
            bundle => BackupPaths.Same(bundle, Target) ? holder : null);

        var start = registry.TryStart(new BackupJobSpec(BackupJobKinds.Restore, _store, Target: Target), _ => { });

        Assert.That((start.Admission, start.HolderSessionId), Is.EqualTo((BackupJobAdmission.TargetHeld, (Guid?)holder)));
        Assert.That(registry.Running(_store), Is.Null, "nothing started");
    }

    [Test]
    [CancelAfter(10_000)]
    public void AProgressReport_AfterTheTerminalTransition_FromAnotherThread_NotifiesNothing()
    {
        var job = Started(BackupJobKinds.Drill, _store, ctx => ctx.Report("Restore", 1, 10, "pages"));
        Ended(job);
        var states = new List<string>();
        job.Changed += dto => { lock (states) { states.Add(dto.State); } };

        // A drill's progress arrives on the child's stdout reader: one that loses the race to the end must not be the stream's last frame.
        var late = new Thread(() => job.Report("Open", 0, 0, null));
        late.Start();
        late.Join();

        lock (states)
        {
            Assert.That(states, Is.Empty);
        }

        Assert.That(job.Snapshot().State, Is.EqualTo(BackupJobStates.Done));
    }

    [Test]
    [CancelAfter(10_000)]
    public void FinishedJobs_AreKeptAnHour_AndTwentyPerStore()
    {
        for (var i = 0; i < BackupJobRegistry.KeepPerStore + 3; i++)
        {
            Ended(Started(BackupJobKinds.Verify, _store, _ => { }));
        }

        _registry.Evict();
        Assert.That(_registry.List(_store), Has.Count.EqualTo(BackupJobRegistry.KeepPerStore), "the newest twenty");

        _now += BackupJobRegistry.KeepTerminal - TimeSpan.FromSeconds(1);
        _registry.Evict();
        Assert.That(_registry.List(_store), Has.Count.EqualTo(BackupJobRegistry.KeepPerStore), "still within the hour");

        _now += TimeSpan.FromSeconds(1);
        _registry.Evict();
        Assert.That(_registry.List(_store), Is.Empty, "an hour after they ended");
    }

    [Test]
    [CancelAfter(10_000)]
    public void ARunningJob_IsNeverEvicted()
    {
        using var gate = new ManualResetEventSlim();
        var job = StartGated(_store, gate);
        _now += TimeSpan.FromDays(1);
        _registry.Evict();
        Assert.That(_registry.Get(job.Id), Is.SameAs(job));
        gate.Set();
        Ended(job);
    }

    [Test]
    [CancelAfter(10_000)]
    public void Cancel_IsIdempotent_AndTheBodyEndsCancelled()
    {
        using var gate = new ManualResetEventSlim();
        var job = StartGated(_store, gate);

        Assert.That(_registry.Cancel(job.Id), Is.True);
        Assert.That(_registry.Cancel(job.Id), Is.True, "a second cancel is harmless");
        Ended(job);
        Assert.That(job.Snapshot().State, Is.EqualTo(BackupJobStates.Cancelled));
        Assert.That(job.Cancel(), Is.False, "nothing left to cancel");
        Assert.That(_registry.Cancel(Guid.NewGuid()), Is.False, "an unknown job");
    }

    [Test]
    [CancelAfter(10_000)]
    public void CancelAll_ReachesAGatedBody()
    {
        using var gate = new ManualResetEventSlim();
        var a = StartGated(_store, gate);
        var b = StartGated(_store + "-b", gate);

        Assert.That(_registry.CancelAll(TimeSpan.FromSeconds(5)), Is.True, "every body ended within the budget");
        Assert.That((a.Snapshot().State, b.Snapshot().State), Is.EqualTo((BackupJobStates.Cancelled, BackupJobStates.Cancelled)));
    }

    [Test]
    [CancelAfter(10_000)]
    public void WhatABodyThrows_IsClassifiedAsTheCliClassifiesIt()
    {
        BackupJobErrorDto Fails(Exception e)
        {
            var job = Started(BackupJobKinds.Verify, _store, _ => throw e);
            Ended(job);
            Assert.That(job.Snapshot().State, Is.EqualTo(BackupJobStates.Error));
            return job.Snapshot().Error;
        }

        Assert.That(Fails(new BackupChainBrokenException(3, 2, [1], "gone")).Category, Is.EqualTo("store_damaged"));
        Assert.That(Fails(new RestoreRefusedException(RestoreRefusalReason.TargetExists, "t", "exists")).Category, Is.EqualTo("refused"));
        Assert.That(Fails(new BackupPlanChangedException("s", "a", "b")).Category, Is.EqualTo("plan_changed"));
        Assert.That(Fails(new BackupJobException("refused", "held")), Is.EqualTo(new BackupJobErrorDto("refused", "held")));
        var defect = Fails(new ArgumentException("bad"));
        Assert.That((defect.Category, defect.Message), Is.EqualTo(("failed", "ArgumentException: bad")), "a defect names its type");
    }

    [Test]
    [CancelAfter(10_000)]
    public void Progress_IsThrottledAtTheSource_ButTheSnapshotIsCurrent()
    {
        using var gate = new ManualResetEventSlim();
        using var burstDone = new ManualResetEventSlim();
        var notified = 0;
        var job = StartGated(_store, gate, ctx =>
        {
            for (var i = 1; i <= 1000; i++)
            {
                ctx.Report("restore", i, 2000, "pages");
            }

            burstDone.Set();
            ctx.Token.WaitHandle.WaitOne();
        });
        job.Changed += _ => Interlocked.Increment(ref notified);
        gate.Set();
        Assert.That(burstDone.Wait(TimeSpan.FromSeconds(5)), Is.True);

        Assert.That(job.Snapshot().Completed, Is.EqualTo(1000));
        Assert.That(Volatile.Read(ref notified), Is.LessThanOrEqualTo(3), "a burst notifies at most once per 100 ms (plus the phase change)");
        job.Cancel();
        Ended(job);
    }
}
