using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// AC-O1 (plan 1416 §8.4): the ops loop end to end on logical clocks — points on the schedule, a chain compacted and pruned by retention, a store that
/// fails until the backups go stale and then until health is Failing, a recovery to Ok. At every step the engine's status, its status file, the metric
/// gauge and <see cref="BackupOperations.ReadStatus(string)"/> of the bundle say the same thing (BKP-15: never better than the store and the schedule).
/// </summary>
class BackupOpsLoopTests : SchedulerTestBase<BackupOpsLoopTests>
{
    public override void Setup()
    {
        base.Setup();
        Retention = new BackupRetentionPolicy
        {
            MaxAge = TimeSpan.Zero, MaxCount = 3, MaxTotalBytes = 0, MinKeep = 2, CompactThreshold = 3, KeepLastDrilled = false,
        };
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-15")]
    public void TheLoop_SchedulesCompactsPrunesDegradesFailsAndRecovers_AndEveryViewAgrees()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        var ids = Spawn(dbe, 100);
        using var exporter = new BackupMetricsExporter(dbe);
        var bundle = dbe.MMF.BundleDirectory;

        // The grace, then a full point.
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Full));
        AssertEveryView(dbe, bundle, BackupHealth.Ok, BackupHealthReason.None, "point 1");

        // One incremental per interval; the fourth makes the chain deeper than CompactThreshold, and MaxCount keeps three.
        for (var n = 2; n <= 4; n++)
        {
            Update(dbe, ids.Take(10 * n), n);
            Clock.Advance(Interval);
            Assert.That(Tick(dbe)?.Kind, Is.EqualTo(BackupKind.Incremental), $"point {n}");
            AssertEveryView(dbe, bundle, BackupHealth.Ok, BackupHealthReason.None, $"point {n}");
        }

        var listing = BackupOperations.List(Store, computeRestorable: true);
        Assert.That(listing.RestorablePoints, Is.EqualTo(new uint[] { 2, 3, 4 }), "retention keeps MaxCount points");
        Assert.That(listing.Points.Any(p => p.BackupId == 1), Is.False, "the packs no kept point needs are gone");
        Assert.That(listing.Points.Any(p => p.Kind == BackupPointKind.Compacted), Is.True, "the chain was compacted before its base went");
        Assert.That(listing.Chains.Max(c => c.HeadDepth), Is.LessThanOrEqualTo(3), "no restore reads more packs than CompactThreshold");
        Assert.That(dbe.Backup.Status.RetentionBlocked, Is.EqualTo(BackupRetentionBlock.None));

        // The store goes away: past twice the interval with no point, the backups are stale.
        StoreFs.Fault = (op, _) => op == nameof(IStoreFileSystem.TryLockExclusive) ? new IOException("the share is gone") : null;
        Clock.Advance(Interval * 2 + TimeSpan.FromSeconds(1));
        Assert.That(Tick(dbe), Is.Null);
        AssertEveryView(dbe, bundle, BackupHealth.Degraded, BackupHealthReason.Stale, "stale");

        // Two more failed attempts on the backoff (60 s, 120 s): three in a row are Failing.
        foreach (var wait in new[] { 60, 120 })
        {
            Clock.Advance(TimeSpan.FromSeconds(wait));
            Assert.That(Tick(dbe), Is.Null, "the store still fails");
        }

        Assert.That(dbe.Backup.Status.ConsecutiveFailures, Is.EqualTo(3));
        AssertEveryView(dbe, bundle, BackupHealth.Failing, BackupHealthReason.ConsecutiveFailures, "three failures");

        // The store comes back: the next attempt (240 s later) publishes, and all is well.
        StoreFs.Fault = null;
        Clock.Advance(TimeSpan.FromSeconds(240));
        Assert.That(Tick(dbe), Is.Not.Null);
        AssertEveryView(dbe, bundle, BackupHealth.Ok, BackupHealthReason.None, "recovered");
    }

    /// <summary>The engine's status, its status file, the gauge and the bundle's re-evaluated status: one verdict.</summary>
    private void AssertEveryView(DatabaseEngine dbe, string bundle, BackupHealth health, BackupHealthReason reason, string step)
    {
        var status = dbe.Backup.Status;
        Assert.That((status.Health, status.Reason), Is.EqualTo((health, reason)), $"{step}: engine status ({status.ReasonText})");

        var file = BackupStatusFile.TryRead(bundle);
        Assert.That(file, Is.Not.Null, $"{step}: no status file");
        Assert.That((file.Status.Health, file.Status.Reason), Is.EqualTo((health, reason)), $"{step}: the status file");

        Assert.That(HealthGauge(), Is.EqualTo((int)health), $"{step}: the typhon.backup.health gauge");

        var read = BackupOperations.ReadStatus(bundle, Clock.Utc);
        Assert.That((read.Health, read.Status.Reason, read.EngineLive), Is.EqualTo((health, reason, true)), $"{step}: ReadStatus of the bundle");
    }

    private static int HealthGauge()
    {
        var seen = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BackupMetricsExporter.MeterName && instrument.Name == "typhon.backup.health")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, v, _, _) => seen.Add(v));
        listener.Start();
        listener.RecordObservableInstruments();
        return seen.Single();
    }
}
