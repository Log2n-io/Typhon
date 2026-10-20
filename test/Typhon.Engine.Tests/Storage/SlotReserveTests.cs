using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// PS-24 (#1382): the page cache keeps a few slots for an allocation that would otherwise wait with its thread's epoch pinned. Such a waiter holds the
/// cache's floor — nothing tagged after its pin can be evicted — so on an epoch-held cache its wait can only end in a timeout, every other thread's
/// with it; served from the reserve, it finishes and its epoch moves.
/// </summary>
[TestFixture]
[NonParallelizable] // TimeoutOptions.Current is a static.
class SlotReserveTests
{
    private IServiceProvider _serviceProvider;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_db";

    [SetUp]
    public void Setup()
    {
        var pages = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount")
            ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")!
            : PagedMMF.MinimumMemPageCount;

        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)pages * PagedMMF.PageSize;
                options.PagesDebugPattern = true;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 512)]
    [VerifiesRule("PS-24")]
    public void ASmallCache_ReservesNoSlot()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        Assert.That(pmmf.SlotReserveTarget, Is.Zero);
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-24")]
    public void TheReserve_FillsFromServedSlots_NoSweepTakesThem()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        var target = pmmf.SlotReserveTarget;
        Assert.That(target, Is.EqualTo(PagedMMF.MinimumMemPageCount / PagedMMF.SlotReserveDivisor));

        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(pmmf.RequestPageEpoch(10, epochManager.GlobalEpoch, out _), Is.True);
        }

        Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(target), "the first allocation filled the reserve");

        // Every slot the reserve does not hold serves a page: the cache's sweeps never reach a reserved slot.
        for (var i = 0; i < 4 * PagedMMF.MinimumMemPageCount; i++)
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(pmmf.RequestPageEpoch(100 + i, epochManager.GlobalEpoch, out _), Is.True);
            }
        }

        Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(target));
        Assert.That(pmmf.ReserveDraws, Is.Zero, "nothing waited: nothing was drawn");
        Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(PagedMMF.MinimumMemPageCount),
            "a reserved slot counts as free, as the gauges count it: no slot is Allocating");
        Assert.That(pmmf.DescribeEvictionBlockers(), Does.Contain($"reserved {target} of {target}"));
    }

    /// <summary>
    /// The case the reserve exists for: every round finds nothing, and the thread asking holds its pin. It takes a reserved slot at once, the page it
    /// asked for is served whole, and the next allocation that finds a slot refills the reserve. With no reserve the same request times out.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-24")]
    public void APinnedWaiter_TakesAReservedSlot_InsteadOfWaiting()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(pmmf.RequestPageEpoch(10, epochManager.GlobalEpoch, out _), Is.True);
        }

        var target = pmmf.ReservedSlotCount;
        Assert.That(target, Is.GreaterThan(0), "premise: the reserve is full");

        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(500);
        TimeoutOptions.Current = timeouts;
        var requester = Environment.CurrentManagedThreadId;
        try
        {
            pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == requester;
            int memPageIndex;
            var sw = Stopwatch.StartNew();
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(pmmf.RequestPageEpoch(200, epochManager.GlobalEpoch, out memPageIndex), Is.True);
            }

            Assert.That(pmmf.TryGetPageResidency(200, out var resident, out _) && resident, Is.True, "the page asked for is in the cache");
            Assert.That(pmmf.GetPageAccessEpoch(memPageIndex), Is.GreaterThan(0), "and tagged, as a page served by a sweep is");

            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(250), "served without waiting out the back-pressure");
            Assert.That(pmmf.ReserveDraws, Is.EqualTo(1));
            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(PagedMMF.MinimumMemPageCount), "the drawn slot serves its page; none is Allocating");
            Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(target - 1));

            pmmf.ForceBackpressureForTest = null;
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(pmmf.RequestPageEpoch(201, epochManager.GlobalEpoch, out _), Is.True);
            }

            Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(target), "the next allocation that found a slot refilled the reserve");
        }
        finally
        {
            pmmf.ForceBackpressureForTest = null;
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>The same request with no reserve: the wait holds its pin until the timeout, which is what the verifier above tells apart.</summary>
    [Test]
    [CancelAfter(5000)]
    [RuleMutant("PS-24")]
    public void Mutant_WithNoReserve_APinnedWaiterTimesOut()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        pmmf.SetSlotReserveTargetForTests(0);
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(300);
        TimeoutOptions.Current = timeouts;
        var requester = Environment.CurrentManagedThreadId;
        pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == requester;
        try
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(() => pmmf.RequestPageEpoch(200, epochManager.GlobalEpoch, out _), Throws.InstanceOf<PageCacheBackpressureTimeoutException>());
            }

            Assert.That(pmmf.ReserveDraws, Is.Zero);
        }
        finally
        {
            pmmf.ForceBackpressureForTest = null;
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>
    /// A younger pin leaves the reserve's last quarter to the thread whose pin is the cache's floor: that one cannot be served by eviction, since nothing
    /// tagged after its pin is evictable, so a reserve emptied by younger waiters would leave it to a timeout.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-24")]
    public void AYoungerPin_LeavesTheFloorHoldersShareOfTheReserve()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(pmmf.RequestPageEpoch(10, epochManager.GlobalEpoch, out _), Is.True);
        }

        var target = pmmf.ReservedSlotCount;
        var kept = target / PagedMMF.SlotReserveFloorShareDivisor;
        Assert.That(kept, Is.GreaterThan(0), "premise: the reserve keeps a share");

        using var floorPinned = new ManualResetEventSlim();
        using var floorAsks = new ManualResetEventSlim();
        var floorDrew = -1L;
        var floor = Task.Run(() =>
        {
            using (EpochGuard.Enter(epochManager))
            {
                floorPinned.Set();
                floorAsks.Wait();
                var before = pmmf.ReserveDraws;
                Assert.That(pmmf.RequestPageEpoch(500, epochManager.GlobalEpoch, out _), Is.True);
                floorDrew = pmmf.ReserveDraws - before;
            }
        });
        floorPinned.Wait();
        using (EpochGuard.Enter(epochManager))
        {
        }

        var young = Environment.CurrentManagedThreadId;
        var rounds = 0;
        pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId != young || ++rounds <= 2;
        try
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(epochManager.MinActiveEpoch, Is.LessThan(epochManager.GlobalEpoch), "premise: the other thread's pin is the floor");
                for (var i = 0; i < target - kept; i++)
                {
                    rounds = 0;
                    Assert.That(pmmf.RequestPageEpoch(200 + i, epochManager.GlobalEpoch, out _), Is.True);
                }

                Assert.That(pmmf.ReserveDraws, Is.EqualTo(target - kept), "each younger request drew, down to the floor's share");
                rounds = 0;
                Assert.That(pmmf.RequestPageEpoch(300, epochManager.GlobalEpoch, out _), Is.True);
                Assert.That(rounds, Is.GreaterThan(2), "at the share, the younger request waited its rounds out");
                Assert.That(pmmf.ReserveDraws, Is.EqualTo(target - kept), "and drew nothing");
                Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(kept));
            }

            floorAsks.Set();
            floor.Wait();
        }
        finally
        {
            floorAsks.Set();
            pmmf.ForceBackpressureForTest = null;
        }

        Assert.That(floorDrew, Is.EqualTo(1), "the floor-holder drew from its share");
    }

    /// <summary>
    /// A slot that ends a wait serves its waiter. Given to the short reserve instead, the waiter would wait again for the next one, and a pinned
    /// waiter would draw back the very slot it had found.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-24")]
    public void ASlotThatEndsAWait_ServesItsWaiter_NotTheReserve()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(pmmf.RequestPageEpoch(10, epochManager.GlobalEpoch, out _), Is.True);
        }

        var requester = Environment.CurrentManagedThreadId;
        var forcedOnce = false;
        pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == requester && !forcedOnce && (forcedOnce = true);
        try
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(pmmf.RequestPageEpoch(200, epochManager.GlobalEpoch, out _), Is.True);
            }

            var target = pmmf.SlotReserveTarget;
            Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(target - 1), "premise: a draw left the reserve short");

            var rounds = 0;
            pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == requester && ++rounds <= 2;
            PagedMMF.RefreshableWaitDepth++;
            try
            {
                using (EpochGuard.Enter(epochManager))
                {
                    Assert.That(pmmf.RequestPageEpoch(201, epochManager.GlobalEpoch, out _), Is.True);
                }
            }
            finally
            {
                PagedMMF.RefreshableWaitDepth--;
            }

            Assert.That(rounds, Is.EqualTo(3), "the round after the wait found a slot and served the waiter with it");
            Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(target - 1), "the reserve did not take it");
        }
        finally
        {
            pmmf.ForceBackpressureForTest = null;
        }
    }

    /// <summary>
    /// Under the eviction guard (#1382), a slot evicted for the reserve sits out its quarantine first, like one evicted to serve a page: a stale address
    /// into it faults for the whole quarantine, whichever use it goes to.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-24")]
    public void UnderTheEvictionGuard_AReservedSlotSatOutItsQuarantine()
    {
        // The guard protects one cache page at a time, so a cache page must be whole system pages: not on Apple Silicon's 16 KiB pages.
        Assume.That(PagedMMF.PageSize % Environment.SystemPageSize, Is.Zero, "the eviction guard needs cache pages made of whole system pages");
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        var served = new HashSet<int>();
        var file = 0;
        void Serve()
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(pmmf.RequestPageEpoch(file++, epochManager.GlobalEpoch, out var mem), Is.True);
                served.Add(mem);
            }
        }

        // Every slot outside the reserve holds a page: from here a slot is found by evicting one.
        for (var i = 0; i < PagedMMF.MinimumMemPageCount; i++)
        {
            Serve();
        }

        pmmf.EnableEvictionGuardForTests(8);
        var requester = Environment.CurrentManagedThreadId;
        var draw = false;
        pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == requester && draw && !(draw = false);
        // Sampled before each slot an allocation tries: a slot quarantined and handed back within one allocation is seen in between.
        var everQuarantined = new HashSet<int>();
        pmmf.ReclaimBeforeLockProbe = _ => everQuarantined.UnionWith(pmmf.QuarantinedSlotsForTests);
        try
        {
            for (var d = 0; d < 4; d++)
            {
                draw = true;
                Serve();
            }

            Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(pmmf.SlotReserveTarget - 4), "premise: four draws left the reserve short");
            for (var i = 0; i < 64 && pmmf.ReservedSlotCount < pmmf.SlotReserveTarget; i++)
            {
                Serve();
            }
        }
        finally
        {
            pmmf.ForceBackpressureForTest = null;
            pmmf.ReclaimBeforeLockProbe = null;
        }

        Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(pmmf.SlotReserveTarget), "premise: evictions refilled the reserve");
        foreach (var slot in pmmf.ReservedSlotsForTests)
        {
            Assert.That(!served.Contains(slot) || everQuarantined.Contains(slot), Is.True, $"slot {slot} held a page and was reserved unquarantined");
        }
    }

    /// <summary>A wait that moves its epoch does not hold the floor (EP-02): it waits, and leaves the reserve to the waits that do.</summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-24")]
    public void AWaitThatMovesItsEpoch_LeavesTheReserveAlone()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(pmmf.RequestPageEpoch(10, epochManager.GlobalEpoch, out _), Is.True);
        }

        var requester = Environment.CurrentManagedThreadId;
        var rounds = 0;
        var refreshed = PagedMMF.RefreshedWaits;
        pmmf.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == requester && ++rounds <= 2;
        PagedMMF.RefreshableWaitDepth++;
        try
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(pmmf.RequestPageEpoch(200, epochManager.GlobalEpoch, out _), Is.True);
            }
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth--;
            pmmf.ForceBackpressureForTest = null;
        }

        Assert.That(PagedMMF.RefreshedWaits - refreshed, Is.GreaterThan(0), "premise: the wait moved its epoch");
        Assert.That(pmmf.ReserveDraws, Is.Zero);
        Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(pmmf.SlotReserveTarget));
    }
}
