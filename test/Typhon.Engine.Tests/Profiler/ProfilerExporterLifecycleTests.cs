using NUnit.Framework;
using Typhon.Engine.Internals;
using System;
using System.Diagnostics;
using System.IO;
using Typhon.Engine.Tests.Profiler.TestInfra;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>An exporter belongs to one profiler session: <c>Stop</c> disposes it and detaches it (#1065).</summary>
[TestFixture]
[NonParallelizable] // the profiler is static
public sealed class ProfilerExporterLifecycleTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = $"ExporterLifecycle-{Guid.NewGuid():N}" });
        TyphonProfiler.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        TyphonProfiler.ResetForTests();
        _registry.Dispose();
    }

    private static ProfilerSessionMetadata Metadata() => new(systems: [], archetypes: [], componentTypes: [], workerCount: 0, baseTickRate: 100f,
        startTimestamp: Stopwatch.GetTimestamp(), stopwatchFrequency: Stopwatch.Frequency, startedUtc: DateTime.UtcNow);

    private void Session(IProfilerExporter exporter)
    {
        TyphonProfiler.AttachExporter(exporter);
        TyphonProfiler.Start(_registry.Profiler, Metadata());
        TyphonProfiler.Stop();
    }

    [Test]
    [CancelAfter(15_000)]
    public void Stop_DetachesItsExporters()
    {
        var abandoned = TyphonProfiler.ExporterThreadsAbandoned;
        var first = new InMemoryExporter("first");
        Session(first);

        Assert.That(TyphonProfiler.DetachExporter(first), Is.False, "the session took it with it");
        Assert.That(() => Session(new InMemoryExporter("second")), Throws.Nothing, "the first exporter is not started again, disposed");
        Assert.That(TyphonProfiler.ExporterThreadsAbandoned, Is.EqualTo(abandoned));
    }

    [Test]
    [CancelAfter(15_000)]
    public void SecondSession_DoesNotTruncateTheFirstTrace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typhon-exporter-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.typhon-trace");
            Session(new FileExporter(first, _registry.Profiler));
            var size = new FileInfo(first).Length;
            Assert.That(size, Is.GreaterThan(0));

            Session(new FileExporter(Path.Combine(directory, "second.typhon-trace"), _registry.Profiler));

            Assert.That(new FileInfo(first).Length, Is.EqualTo(size));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnExporterDisposedBeforeStart_DoesNotCostTheOthersTheirTrace()
    {
        var disposed = new InMemoryExporter("disposed");
        disposed.Dispose();
        var live = new InMemoryExporter("live");
        TyphonProfiler.AttachExporter(disposed);
        TyphonProfiler.AttachExporter(live);

        TyphonProfiler.Start(_registry.Profiler, Metadata());
        Assert.That(() => TyphonProfiler.Stop(), Throws.Nothing);
        Assert.That((live.Initialized, live.Flushed > 0), Is.EqualTo((1, true)), "the live exporter's session ran to its end");
    }

    [Test]
    [CancelAfter(15_000)]
    public void StartThenImmediateStop_Repeated_DoesNotKillTheHost()
    {
        // Repeated to land a Stop inside the exporter threads' start-up window. Each session used to allocate a fresh 128 MiB spillover pool on the
        // large-object heap: 100 of them drove this host to several GiB before the GC caught up. Only the first may allocate now.
        Session(new InMemoryExporter("session-0"));
        var allocated = SpilloverRingPool.RingsAllocated;
        for (var i = 1; i < 100; i++)
        {
            Session(new InMemoryExporter($"session-{i}"));
        }

        Assert.That(SpilloverRingPool.RingsAllocated, Is.EqualTo(allocated), "Stop keeps the spillover pool for the next Start");
    }
}
