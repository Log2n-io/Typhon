using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Plan 1416 §9: an idle tick (the request poll, the due decision, the health verdict) allocates nothing — the snapshot is rebuilt, and the status file
/// written, only when something changed.
/// </summary>
class BackupSchedulerAllocationTests : SchedulerTestBase<BackupSchedulerAllocationTests>
{
    [Test]
    [CancelAfter(30_000)]
    public void AThousandIdleTicks_AllocateNothing()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(Tick(dbe), Is.Not.Null, "premise: a point, so the idle ticks below have a store and a schedule to judge");
        var scheduler = dbe.BackupScheduler;
        for (var i = 0; i < 10; i++)
        {
            scheduler.TickForTest();   // warm: JIT, and the one republish the first idle tick may make
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            scheduler.TickForTest();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero, $"{allocated} bytes over 1 000 idle ticks");
    }

    /// <summary>
    /// The tick's request poll allocates nothing whatever the bundle path's length: on Windows a path past MAX_PATH made every <c>File.Exists</c>
    /// build an extended-length copy of it. The path is made longer than 260 characters on purpose, whatever the temp directory, and a request
    /// written there is still found.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheRequestPoll_AllocatesNothing_OnABundlePathPastMaxPath()
    {
        var top = Path.Combine(TestStorage.ForFixture(nameof(BackupSchedulerAllocationTests)), Guid.NewGuid().ToString("N"));
        var bundle = top;
        while (Path.Combine(bundle, BackupRequestFile.FileName).Length <= 300)
        {
            bundle = Path.Combine(bundle, new string('b', 40));
        }

        Directory.CreateDirectory(bundle);
        try
        {
            var requests = new BackupRequestFile(bundle);
            for (var i = 0; i < 10; i++)
            {
                Assert.That(requests.TryPoll(out _), Is.False);   // warm
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
            {
                requests.TryPoll(out _);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero, $"{allocated} bytes over 1 000 polls of a {bundle.Length}-character bundle path");

            File.WriteAllText(Path.Combine(bundle, BackupRequestFile.FileName), "{\"formatVersion\":1,\"kind\":\"full\"}");
            Assert.That(requests.TryPoll(out var request), Is.True, "a request at the long path is found");
            Assert.That(request.Kind, Is.EqualTo(BackupRequestKind.Full));
            requests.Delete();
            Assert.That(File.Exists(Path.Combine(bundle, BackupRequestFile.FileName)), Is.False, "and retired");
        }
        finally
        {
            Directory.Delete(top, true);
        }
    }
}

/// <summary>The request poll's path (no engine).</summary>
[TestFixture]
internal sealed class BackupRequestFilePathTests
{
    /// <summary>The poll's path on Windows: a drive path and a UNC path in their extended-length forms, an already extended or device path as it is.</summary>
    [TestCase(@"C:\data\world.typhon\backup.request", @"\\?\C:\data\world.typhon\backup.request")]
    [TestCase(@"\\nas\share\world.typhon\backup.request", @"\\?\UNC\nas\share\world.typhon\backup.request")]
    [TestCase(@"\\?\C:\data\backup.request", @"\\?\C:\data\backup.request")]
    [TestCase(@"\\.\C:\data\backup.request", @"\\.\C:\data\backup.request")]
    public void ThePollsPath_IsExtendedOnWindows(string fullPath, string probe)
    {
        Assume.That(OperatingSystem.IsWindows(), "extended-length paths are Windows'");
        Assert.That(BackupRequestFile.Probe(fullPath), Is.EqualTo(probe));
    }
}
