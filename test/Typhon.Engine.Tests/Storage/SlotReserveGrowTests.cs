using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// PS-24 (#1382), the case <c>ChaosStressTests.UltimateStress_AllSubsystems</c> starved on: a segment's grow holds its grow lock while it reads and creates
/// pages, and every allocation of that segment that found no room waits on the lock — publishes among them, their pins the cache's floor. They wait on the
/// lock, not on a page, so they never draw from the reserve; the grow, a younger pin, draws the floor's share for them.
/// </summary>
[TestFixture]
[NonParallelizable] // TimeoutOptions.Current is a static.
class SlotReserveGrowTests
{
    private IServiceProvider _serviceProvider;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_db";

    [SetUp]
    public void Setup()
    {
        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumMemPageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    /// <summary>
    /// A younger pin grows a segment while an older pin holds the cache's floor and the reserve is down to the floor's share; every page the grow asks
    /// for meets back-pressure. The grow draws from that share and completes. Without the inheritance it waited for the timeout.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("PS-24")]
    public void AGrowBehindTheFloor_DrawsItsShare_AndCompletes()
    {
        var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(pmmf.RequestPageEpoch(10, epochManager.GlobalEpoch, out _), Is.True);
        }

        var target = pmmf.ReservedSlotCount;
        var kept = target / PagedMMF.SlotReserveFloorShareDivisor;
        Assert.That(kept, Is.GreaterThan(2), "premise: the floor's share can serve a small grow");

        using var floorPinned = new ManualResetEventSlim();
        using var floorRelease = new ManualResetEventSlim();
        var floor = Task.Run(() =>
        {
            using (EpochGuard.Enter(epochManager))
            {
                floorPinned.Set();
                floorRelease.Wait();
            }
        });
        floorPinned.Wait();
        using (EpochGuard.Enter(epochManager))
        {
        }

        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(300);
        TimeoutOptions.Current = timeouts;
        var young = Environment.CurrentManagedThreadId;
        var draining = true;
        var rounds = 0;
        var growRounds = 0;
        pmmf.ForceBackpressureForTest = () =>
        {
            if (Environment.CurrentManagedThreadId != young)
            {
                return false;
            }

            if (draining)
            {
                return ++rounds <= 2;
            }

            // Only the grow's own allocations meet back-pressure: the staged epoch-held cache, for the pages the grow reads and creates.
            if (PagedMMF.FloorInheritDepth == 0)
            {
                return false;
            }

            growRounds++;
            return true;
        };

        try
        {
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(epochManager.MinActiveEpoch, Is.LessThan(epochManager.CurrentThreadPinnedEpoch), "premise: the other thread's pin is the floor");

                // Made, and filled to one chunk short of its capacity, before the reserve is drawn down: an allocation that finds a slot refills a short
                // reserve, so the grow must be the first to ask for a page once it is at the floor's share.
                var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);
                while (segment.AllocatedChunkCount < segment.ChunkCapacity - 1)
                {
                    segment.AllocateChunk(false);
                }

                for (var i = 0; i < target - kept; i++)
                {
                    rounds = 0;
                    Assert.That(pmmf.RequestPageEpoch(200 + i, epochManager.GlobalEpoch, out _), Is.True);
                }

                Assert.That(pmmf.ReservedSlotCount, Is.EqualTo(kept), "premise: the reserve is down to the floor's share");
                draining = false;
                var length = segment.Length;
                var draws = pmmf.ReserveDraws;
                Exception thrown = null;
                try
                {
                    while (segment.Length == length)
                    {
                        segment.AllocateChunk(false);
                    }
                }
                catch (Exception e)
                {
                    thrown = e;
                }

                Assert.That(thrown, Is.Null, "the grow completed rather than waiting out the back-pressure");
                Assert.That(growRounds, Is.GreaterThan(0), "premise: the grow met back-pressure");
                Assert.That(pmmf.ReserveDraws - draws, Is.GreaterThan(0), "it drew from the floor's share");
            }
        }
        finally
        {
            floorRelease.Set();
            pmmf.ForceBackpressureForTest = null;
            TimeoutOptions.Current = saved;
            floor.Wait();
        }
    }
}
