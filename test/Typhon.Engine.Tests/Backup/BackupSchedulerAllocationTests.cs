using System;
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
}
