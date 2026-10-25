using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// BKP-15's verdict (plan 1416 §3.5): every row of the health table reachable alone, the worst condition winning, and — seeded over random inputs — health
/// never better than the store and the schedule are: never Ok without a point on this timeline, after three failures, or past twice the interval.
/// </summary>
[TestFixture]
[Seeded]
internal sealed class BackupHealthEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A healthy hourly engine: a point ten minutes old, a store with points, a recent passed drill.</summary>
    private static HealthInputs Healthy() => new()
    {
        Configured = true,
        Interval = TimeSpan.FromHours(1),
        DrillWindow = TimeSpan.FromDays(1),
        StartedUtc = Now.AddDays(-2),
        StoreKnown = true,
        RestorablePoints = 5,
        LastSuccessUtc = Now.AddMinutes(-10),
        LastSuccessCheckpointLsn = 1000,
        AttemptEnded = true,
        LastDrillUtc = Now.AddHours(-3),
        AnyDrillPassed = true,
    };

    private static object[] Row(string name, Func<HealthInputs, HealthInputs> change, BackupHealth health, BackupHealthReason reason) =>
        [name, change, health, reason];

    private static readonly object[] Rows =
    [
        Row("not configured", i => i with { Configured = false }, BackupHealth.Off, BackupHealthReason.NotConfigured),
        Row("no restorable point", i => i with { RestorablePoints = 0 }, BackupHealth.Failing, BackupHealthReason.NoRestorablePoint),
        Row("3 failures", i => i with { ConsecutiveFailures = 3 }, BackupHealth.Failing, BackupHealthReason.ConsecutiveFailures),
        Row("store down > interval", i => i with { StoreFailingSinceUtc = Now.AddHours(-2) }, BackupHealth.Failing, BackupHealthReason.StoreUnreachable),
        Row("capture refused", i => i with { CaptureRefused = true }, BackupHealth.Failing, BackupHealthReason.CaptureRefused),
        Row("archive diverged", i => i with { ArchiveCondition = WalArchiveCondition.Diverged }, BackupHealth.Failing, BackupHealthReason.ArchiveFailed),
        Row("retention broke a kept point", i => i with { KeptPointBroken = true }, BackupHealth.Failing, BackupHealthReason.KeptPointBroken),
        Row("stale", i => i with { LastSuccessUtc = Now.AddHours(-2.1) }, BackupHealth.Degraded, BackupHealthReason.Stale),
        Row("archive released", i => i with { ArchiveCondition = WalArchiveCondition.Released }, BackupHealth.Degraded, BackupHealthReason.ArchiveGap),
        Row("uncovered gap", i => i with { ArchiveCondition = WalArchiveCondition.Archiving, ArchiveLastGapToLsn = 1000 },
            BackupHealth.Degraded, BackupHealthReason.ArchiveGap),
        Row("archive stalled", i => i with { ArchiveCondition = WalArchiveCondition.Stalled }, BackupHealth.Degraded, BackupHealthReason.ArchiveLag),
        Row("archive lag",
            i => i with { ArchiveCondition = WalArchiveCondition.Archiving, ArchiveMaxSegmentAge = TimeSpan.FromSeconds(30), ArchiveLagSeconds = 61 },
            BackupHealth.Degraded, BackupHealthReason.ArchiveLag),
        Row("drill failed, none passed", i => i with { LastDrillFailed = true, AnyDrillPassed = false }, BackupHealth.Degraded, BackupHealthReason.DrillFailed),
        Row("a kept chain's pack damaged", i => i with { KeptPointDamaged = true }, BackupHealth.Degraded, BackupHealthReason.KeptPointDamaged),
        Row("no point on this timeline", i => i with { LastSuccessUtc = DateTime.MinValue, StartedUtc = Now.AddMinutes(-1) },
            BackupHealth.Warning, BackupHealthReason.FirstPointPending),
        Row("suspect allocated", i => i with { LastPointSuspectAllocated = true }, BackupHealth.Warning, BackupHealthReason.LastPointSuspect),
        Row("retention failed", i => i with { RetentionFailed = true }, BackupHealth.Warning, BackupHealthReason.RetentionFailed),
        Row("retention blocked", i => i with { RetentionBlocked = BackupRetentionBlock.MinKeep }, BackupHealth.Warning, BackupHealthReason.RetentionBlocked),
        Row("drill stale", i => i with { LastDrillUtc = Now.AddDays(-2) }, BackupHealth.Warning, BackupHealthReason.DrillStale),
        Row("never drilled", i => i with { LastDrillUtc = DateTime.MinValue, AnyDrillPassed = false }, BackupHealth.Warning, BackupHealthReason.DrillStale),
        Row("drill failed, older passed", i => i with { LastDrillFailed = true }, BackupHealth.Warning, BackupHealthReason.DrillFailedOlderOk),
        Row("healthy", i => i, BackupHealth.Ok, BackupHealthReason.None),
    ];

    [TestCaseSource(nameof(Rows))]
    [VerifiesRule("BKP-15")]
    public void EachRow_IsReachedAlone(string what, Func<HealthInputs, HealthInputs> condition, BackupHealth health, BackupHealthReason reason)
    {
        var inputs = condition(Healthy());
        Assert.That(BackupHealthEvaluator.Evaluate(inputs, Now), Is.EqualTo((health, reason)), what);
        Assert.That(BackupHealthEvaluator.Explain(inputs, reason, Now), Is.Not.Empty);
    }

    [Test]
    public void TheAgeRules_AreOffWithoutASchedule_AndADrillWindowOfZeroMonitorsNothing()
    {
        var unscheduled = Healthy() with { Interval = TimeSpan.Zero, LastSuccessUtc = Now.AddDays(-30), StoreFailingSinceUtc = Now.AddDays(-1) };
        Assert.That(BackupHealthEvaluator.Evaluate(unscheduled, Now).Health, Is.EqualTo(BackupHealth.Ok));
        var unmonitored = Healthy() with { DrillWindow = TimeSpan.Zero, LastDrillUtc = DateTime.MinValue };
        Assert.That(BackupHealthEvaluator.Evaluate(unmonitored, Now).Health, Is.EqualTo(BackupHealth.Ok));
        var restored = Healthy() with { LastSuccessUtc = DateTime.MinValue, StartedUtc = Now.AddHours(-3) };
        Assert.That(BackupHealthEvaluator.Evaluate(restored, Now), Is.EqualTo((BackupHealth.Degraded, BackupHealthReason.Stale)),
            "a timeline with no point for twice the interval since start is stale, not merely pending");
    }

    [Test]
    public void TheHealthCheck_MapsOffAndOkHealthy_WarningAndDegradedDegraded_FailingUnhealthy()
    {
        Assert.That(BackupHealthCheck.Map(BackupHealth.Off), Is.EqualTo(HealthStatus.Healthy));
        Assert.That(BackupHealthCheck.Map(BackupHealth.Ok), Is.EqualTo(HealthStatus.Healthy));
        Assert.That(BackupHealthCheck.Map(BackupHealth.Warning), Is.EqualTo(HealthStatus.Degraded));
        Assert.That(BackupHealthCheck.Map(BackupHealth.Degraded), Is.EqualTo(HealthStatus.Degraded));
        Assert.That(BackupHealthCheck.Map(BackupHealth.Failing), Is.EqualTo(HealthStatus.Unhealthy));
    }

    /// <summary>The worst condition wins, and a covered gap is no gap.</summary>
    [Test]
    public void TheWorstConditionWins()
    {
        var both = Healthy() with { ConsecutiveFailures = 5, LastSuccessUtc = Now.AddDays(-1), RetentionFailed = true };
        Assert.That(BackupHealthEvaluator.Evaluate(both, Now).Reason, Is.EqualTo(BackupHealthReason.ConsecutiveFailures));
        var covered = Healthy() with { ArchiveCondition = WalArchiveCondition.Archiving, ArchiveLastGapToLsn = 999 };
        Assert.That(BackupHealthEvaluator.Evaluate(covered, Now).Health, Is.EqualTo(BackupHealth.Ok), "a point with C past the gap covers it");
    }

    /// <summary>Seeded: over random inputs the verdict is never better than what holds.</summary>
    [Test]
    [VerifiesRule("BKP-15")]
    public void RandomInputs_NeverReportBetterThanWhatHolds() => AssertNeverBetter(BackupHealthEvaluator.Evaluate);

    /// <summary>The property can fail: an evaluator that forgets the "no point on this timeline" rule says Ok for a just-restored database.</summary>
    [Test]
    [RuleMutant("BKP-15")]
    public void Mutant_IgnoringThePointlessTimeline_IsReported() =>
        RuleMutants.AssertDetects("BKP-15", "BKP-15: health", () => AssertNeverBetter((in HealthInputs i, DateTime now) =>
        {
            var copy = i with { LastSuccessUtc = i.LastSuccessUtc == DateTime.MinValue ? now : i.LastSuccessUtc };
            return BackupHealthEvaluator.Evaluate(copy, now);
        }));

    private delegate (BackupHealth Health, BackupHealthReason Reason) Evaluator(in HealthInputs inputs, DateTime utcNow);

    private static void AssertNeverBetter(Evaluator evaluate)
    {
        var random = TestSeed.Random();
        for (var n = 0; n < 5_000; n++)
        {
            var i = new HealthInputs
            {
                Configured = true,
                Interval = random.Next(3) == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(random.Next(1, 240)),
                DrillWindow = random.Next(2) == 0 ? TimeSpan.Zero : TimeSpan.FromHours(random.Next(1, 48)),
                StartedUtc = Now.AddMinutes(-random.Next(0, 2000)),
                StoreKnown = random.Next(5) != 0,
                RestorablePoints = random.Next(4),
                LastSuccessUtc = random.Next(3) == 0 ? DateTime.MinValue : Now.AddMinutes(-random.Next(0, 1000)),
                AttemptEnded = random.Next(2) == 0,
                ConsecutiveFailures = random.Next(5),
                StoreFailingSinceUtc = random.Next(3) == 0 ? Now.AddMinutes(-random.Next(0, 600)) : DateTime.MinValue,
                LastDrillUtc = random.Next(3) == 0 ? DateTime.MinValue : Now.AddHours(-random.Next(0, 100)),
                LastDrillFailed = random.Next(4) == 0,
                AnyDrillPassed = random.Next(2) == 0,
                KeptPointBroken = random.Next(8) == 0,
                KeptPointDamaged = random.Next(8) == 0,
            };
            var (health, _) = evaluate(i, Now);
            var must = new List<(string, BackupHealth)>();
            if (i.StoreKnown && i.RestorablePoints == 0 && i.AttemptEnded)
            {
                must.Add(("no restorable point", BackupHealth.Failing));
            }

            if (i.ConsecutiveFailures >= 3)
            {
                must.Add(("3 failures", BackupHealth.Failing));
            }

            if (i.LastSuccessUtc == DateTime.MinValue)
            {
                must.Add(("no point on this timeline", BackupHealth.Warning));
            }

            if (i.KeptPointBroken)
            {
                must.Add(("a kept point broken by retention", BackupHealth.Failing));
            }

            if (i.KeptPointDamaged)
            {
                must.Add(("a kept chain's pack damaged", BackupHealth.Degraded));
            }

            if (i.Interval > TimeSpan.Zero && i.LastSuccessUtc != DateTime.MinValue && Now - i.LastSuccessUtc > i.Interval * 2)
            {
                must.Add(("stale", BackupHealth.Degraded));
            }

            foreach (var (why, atLeast) in must)
            {
                Assert.That(health, Is.GreaterThanOrEqualTo(atLeast), $"BKP-15: health {health} although {why} ({i})");
            }
        }
    }
}
