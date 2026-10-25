using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The schedule's arithmetic (plan 1416 DL-18/19/20, BKP-15), pure: anchored on the monotonic clock from the newest point of the engine's timeline, a
/// doubling backoff capped at a quarter of the interval, and the two mistakes it exists to avoid shown failing its checks.
/// </summary>
[TestFixture]
internal sealed class ScheduleStateTests
{
    private const long Frequency = 1_000;   // a millisecond clock
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly Guid OldTimeline = Guid.Parse("71e71e00-0000-0000-0000-0000000000bb");

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

    [Test]
    public void TheBackoff_DoublesFromAMinute_UpToAQuarterOfTheInterval_AndDeferralsDoNotGrow()
    {
        var s = new ScheduleState(Hour, Frequency);
        s.Initialize(Now, 0, Now);
        long mono = 0;
        var delays = new List<long>();
        for (var i = 0; i < 6; i++)
        {
            s.Failed(BackupScheduler.FailureClass.Counted, mono);
            delays.Add(s.DueAt() - mono);
            mono = s.DueAt();
        }

        Assert.That(delays, Is.EqualTo(new[] { 60_000L, 120_000, 240_000, 480_000, 900_000, 900_000 }));
        s.Failed(BackupScheduler.FailureClass.Deferred, mono);
        Assert.That(s.DueAt() - mono, Is.EqualTo(60_000), "a deferral waits the base delay, whatever came before");
        s.Succeeded(mono);
        Assert.That(s.DueAt(), Is.EqualTo(mono + Ms(Hour)));
        s.Failed(BackupScheduler.FailureClass.Permanent, mono);
        Assert.That(s.DueAt() - mono, Is.EqualTo(Ms(Hour) / 4));
    }

    [Test]
    public void WithoutASchedule_NothingIsEverDue_OrRetried()
    {
        var s = new ScheduleState(TimeSpan.Zero, Frequency);
        s.Initialize(Now, 0, DateTime.MinValue);
        s.Failed(BackupScheduler.FailureClass.Counted, 0);
        Assert.That(s.IsDue(long.MaxValue - 1), Is.False);
        Assert.That(s.NextDueUtc(Now, 0), Is.EqualTo(DateTime.MinValue));
    }

    [Test]
    [VerifiesRule("BKP-15")]
    public void AWallClockSetBack_DelaysAPointByAtMostOneInterval() => AssertDueWithinAnInterval((utc, mono, s) => s.IsDue(mono));

    /// <summary>The check can fail: a schedule judged on the wall clock stops for a day when the clock is set back a day.</summary>
    [Test]
    [RuleMutant("BKP-15")]
    public void Mutant_AWallClockSchedule_IsReported() =>
        RuleMutants.AssertDetects("BKP-15", "BKP-15: due", () => AssertDueWithinAnInterval((utc, _, _) => utc - Now >= Hour));

    [Test]
    [VerifiesRule("BKP-15")]
    public void ARestoredTimeline_IsDueAtTheGrace() => AssertRestoredTimelineDueAtOnce(BackupScheduler.NewestOnTimeline);

    /// <summary>The check can fail: counting the old timeline's newest point makes a just-restored database wait an interval for its first point.</summary>
    [Test]
    [RuleMutant("BKP-15")]
    public void Mutant_IgnoringTheTimeline_IsReported() =>
        RuleMutants.AssertDetects("BKP-15", "BKP-15: a restored", () =>
            AssertRestoredTimelineDueAtOnce((view, restorable, _) => ChainResolver.Resolve(view, restorable[^1])));

    /// <summary>A point at <c>Now</c>; the wall clock then set back a day; an interval of monotonic time later, the next point must be due.</summary>
    private static void AssertDueWithinAnInterval(Func<DateTime, long, ScheduleState, bool> isDue)
    {
        var s = new ScheduleState(Hour, Frequency);
        s.Initialize(Now, 0, DateTime.MinValue);
        s.Succeeded(0);
        var utc = Now - TimeSpan.FromDays(1);
        var mono = Ms(Hour);
        Assert.That(isDue(utc + Hour, mono, s), Is.True, "BKP-15: due an interval after the last point, whatever the wall clock did");
    }

    /// <summary>The store holds only the old timeline's points, the newest fresh; the engine is on a new one: its first point is due at once.</summary>
    private static void AssertRestoredTimelineDueAtOnce(Func<StoreView, List<uint>, Guid, ResolvedChain> pick)
    {
        var view = new StoreView
        {
            Paths = new StorePaths("/s"),
            Identity = new StoreIdentityFile { FormatVersion = 1, StoreId = Store, DatabaseId = Db },
            Catalog = new StoreCatalogFile { Packs = [Full(1, OldTimeline, Now.AddDays(-3)), Full(2, OldTimeline, Now)], Unreadable = [] },
            Temps = [],
        };
        var newest = pick(view, ChainResolver.RestorablePoints(view), NewTimeline);
        var s = new ScheduleState(Hour, Frequency);
        s.Initialize(Now, 0, BackupOperations.Summarize(newest?.Point)?.TailEndUtc ?? DateTime.MinValue);
        Assert.That(s.IsDue(1), Is.True, "BKP-15: a restored timeline's first point is due at once (after the grace)");
    }

    private static readonly Guid NewTimeline = Guid.Parse("71e71e00-0000-0000-0000-0000000000cc");
    private static readonly Guid Db = Guid.Parse("d0d0d0d0-0000-0000-0000-000000000001");
    private static readonly Guid Store = Guid.Parse("5a5a5a5a-0000-0000-0000-000000000001");

    private static CatalogPack Full(uint id, Guid timeline, DateTime tailEnd) => new()
    {
        File = PackFormat.FileName(id, BackupKind.Full),
        BackupId = id,
        Kind = BackupKind.Full,
        BaseId = id,
        ChainId = Guid.NewGuid(),
        DatabaseId = Db,
        StoreId = Store,
        TimelineId = timeline,
        ImagePages = 10,
        CheckpointLsn = id,
        PointLsn = id,
        TailEndUtc = tailEnd,
    };
}
