using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Plan 1416 §4.4 (DL-21): the backup's gauges are exported as gauges and its totals as counters on the <c>Typhon.Backup</c> meter, read from the published
/// status; the resource tree's <see cref="IMetricSource"/> carries the counters and the point's duration.
/// </summary>
class BackupMetricsTests : SchedulerTestBase<BackupMetricsTests>
{
    [Test]
    [CancelAfter(30_000)]
    public void TheMeter_ExportsGaugesAsGauges_AndTotalsAsCounters()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 50);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null);

        using var exporter = new BackupMetricsExporter(dbe);
        var seen = new Dictionary<string, (Instrument Instrument, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BackupMetricsExporter.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((i, v, _, _) => seen[i.Name] = (i, v));
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => seen[i.Name] = (i, v));
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => seen[i.Name] = (i, v));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.That(seen["typhon.backup.health"].Value, Is.EqualTo((int)BackupHealth.Ok));
        Assert.That(seen["typhon.backup.chain_depth"].Value, Is.EqualTo(1));
        Assert.That(seen["typhon.backup.restorable_points"].Value, Is.EqualTo(1));
        Assert.That(seen["typhon.backup.store_bytes"].Value, Is.GreaterThan(0));
        Assert.That(seen["typhon.backup.last_bytes"].Value, Is.GreaterThan(0));
        Assert.That(seen["typhon.backup.rpo_seconds"].Value, Is.GreaterThanOrEqualTo(0), "without archiving, the age of the newest point's tail");
        Assert.That(seen["typhon.backup.points_total"].Value, Is.EqualTo(1));
        Assert.That(seen.ContainsKey("typhon.backup.last_drill_ok"), Is.False, "no drill: no measurement, not a false zero");
        Assert.That(seen.ContainsKey("typhon.backup.archive.lag_seconds"), Is.False, "archiving off: no measurement");

        foreach (var (name, (instrument, _)) in seen)
        {
            var counter = name.EndsWith("_total", StringComparison.Ordinal);
            Assert.That(instrument.GetType().Name, Does.StartWith(counter ? "ObservableCounter" : "ObservableGauge"), $"{name} is exported as the wrong kind");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void TheMetricSource_CarriesTheCountersAndTheDuration()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 50);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null);

        var writer = new CapturingWriter();
        ((IMetricSource)dbe.BackupScheduler).ReadMetrics(writer);

        Assert.That(writer.Throughput["Points"], Is.EqualTo(1));
        Assert.That(writer.Throughput["Failures"], Is.Zero);
        Assert.That(writer.Durations["BackupPoint"].Last, Is.GreaterThanOrEqualTo(0));
        Assert.That(writer.Durations["BackupPoint"].Max, Is.EqualTo(writer.Durations["BackupPoint"].Last));
    }

    private sealed class CapturingWriter : IMetricWriter
    {
        public Dictionary<string, long> Throughput { get; } = [];

        public Dictionary<string, (long Last, long Avg, long Max)> Durations { get; } = [];

        public void WriteMemory(long allocatedBytes, long peakBytes)
        {
        }

        public void WriteCapacity(long current, long maximum)
        {
        }

        public void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes)
        {
        }

        public void WriteThroughput(string name, long count) => Throughput[name] = count;

        public void WriteDuration(string name, long lastUs, long avgUs, long maxUs) => Durations[name] = (lastUs, avgUs, maxUs);
    }
}
