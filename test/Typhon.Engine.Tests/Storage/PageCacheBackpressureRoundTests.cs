using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1137: under back-pressure an allocation round visits a bounded number of slots, pass 2 covers the cache once per lap on its own cursor, the
/// timeout is honoured only at the end of a lap, and a retry takes a page another thread published meanwhile. A 1 024-slot cache with the
/// budget floor lowered to 16 behaves like a 32 M-slot cache does at the default: 32 rounds of 32 + 32 visits per lap.
/// </summary>
/// <remarks>
/// The cache is filled with fresh pages, each pinned by a slot reference, until no slot is left to evict (the engine's own dirty pages are
/// unevictable anyway: no checkpoint runs here). The strategy is scripted, so nothing waits on a clock.
/// </remarks>
[TestFixture]
class PageCacheBackpressureRoundTests
{
    private const int CachePages = 1024;

    private IServiceProvider _serviceProvider;
    private ScriptedStrategy _strategy;

    private static string CurrentDatabaseName => $"BpRound_{TestContext.CurrentContext.Test.ID}";

    private sealed class ScriptedStrategy : IPageCacheBackpressureStrategy
    {
        /// <summary>Given the call number (from 1), whether to retry; null gives up at once.</summary>
        public Func<int, bool> OnCall;
        public int Calls;
        public int LastDirtyCount;
        public int LastEpochCount;

        public bool OnPressure(ref BackpressureContext ctx, int dirtyPageCount, int epochProtectedCount)
        {
            Calls++;
            LastDirtyCount = dirtyPageCount;
            LastEpochCount = epochProtectedCount;
            return OnCall?.Invoke(Calls) ?? false;
        }

        public void Dispose()
        {
        }
    }

    [SetUp]
    public void Setup()
    {
        _strategy = new ScriptedStrategy();
        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)CachePages * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
                options.BackpressureStrategyFactory = () => _strategy;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>Requests fresh pages, pinning each by a slot reference, until none can be allocated. Returns the pinned slots.</summary>
    private static List<int> FillAndPin(PagedMMF mmf, int firstFilePage)
    {
        var pinned = new List<int>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        for (var fp = firstFilePage; ; fp++)
        {
            try
            {
                Assert.That(mmf.RequestPageEpoch(fp, mmf.EpochManager.GlobalEpoch, out var memPageIndex), Is.True);
                mmf.IncrementSlotRefCount(memPageIndex);
                pinned.Add(memPageIndex);
            }
            catch (PageCacheBackpressureTimeoutException)
            {
                return pinned;
            }
        }
    }

    private static int Request(PagedMMF mmf, int filePageIndex)
    {
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        Assert.That(mmf.RequestPageEpoch(filePageIndex, mmf.EpochManager.GlobalEpoch, out var memPageIndex), Is.True);
        return memPageIndex;
    }

    [Test]
    [CancelAfter(10_000)]
    public void UnderAReducedBudget_EveryRoundIsBounded_AndTheTimeoutWaitsForALap()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var pinned = FillAndPin(mmf, 1000);
        Assert.That(pinned.Count, Is.GreaterThan(CachePages / 2), "precondition: the cache is mostly pinned");

        mmf.SweepBudgetFloor = 16;   // budget = max(16, 1024 / 32) = 32 visits per pass
        _strategy.Calls = 0;
        var wakes = 0;
        mmf.OnBackpressure = () => wakes++;
        var visits = mmf.FailedSweepVisitsForTests;

        Assert.Throws<PageCacheBackpressureTimeoutException>(() => Request(mmf, 5000));

        Assert.That(_strategy.Calls, Is.EqualTo(CachePages / 32), "a strategy that gives up at once is overruled until pass 2 has seen every slot");
        Assert.That(mmf.FailedSweepVisitsForTests - visits, Is.EqualTo(_strategy.Calls * (32L + 32)), "each round: 32 clock visits + 32 pass-2 visits");
        Assert.That(wakes, Is.EqualTo(_strategy.Calls), "the checkpoint wake and the pins' release run every round, not once per lap");
    }

    [Test]
    [CancelAfter(10_000)]
    public void UnderAReducedBudget_ASlotReleasedWithoutASignal_IsFoundWithinALap()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var pinned = FillAndPin(mmf, 1000);

        mmf.SweepBudgetFloor = 16;
        var target = -1;
        _strategy.Calls = 0;
        _strategy.OnCall = call =>
        {
            if (call == 1)
            {
                // Half a cache ahead of the hand: neither pass reaches it for many rounds. No signal: nothing wakes anyone.
                var ahead = (mmf.ClockHandForTests + CachePages / 2) % CachePages;
                target = pinned.Contains(ahead) ? ahead : pinned[pinned.Count / 2];

                mmf.DecrementSlotRefCount(target);
            }

            return true;
        };

        var memPageIndex = Request(mmf, 5000);

        Assert.That(memPageIndex, Is.EqualTo(target), "the only evictable slot was found");
        Assert.That(_strategy.Calls, Is.InRange(2, CachePages / 32), "after rounds that found nothing, and within one lap");
    }

    /// <summary>
    /// Other waiters move the shared clock hand between this waiter's rounds (staged: 64 slots per round). Were pass 2 to walk the shared
    /// hand, the slots the others swept past would never be its to try, and a slot freed among them would stay unseen lap after lap: with
    /// 1 024 slots and a 128-slot stride, the same quarter is skipped every lap. On its own cursor, pass 2 reaches every slot within a lap.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void PassTwo_ReachesEverySlot_WhateverOtherThreadsDoWithTheHand()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var pinned = FillAndPin(mmf, 1000);
        foreach (var slot in pinned)
        {
            Assert.That(slot, Is.InRange(0, CachePages - 1));
        }

        mmf.SweepBudgetFloor = 16;   // 32 + 32 visits a round
        var target = -1;
        _strategy.Calls = 0;
        _strategy.OnCall = call =>
        {
            var hand = mmf.ClockHandForTests;
            if (call == 1)
            {
                // Ahead of every cursor, at an offset the shared hand's 128-slot stride never lands a pass on.
                target = (hand + 3 * 128 + 10) % CachePages;
                Assume.That(pinned.Contains(target), "the target must be one of the pinned slots");
                mmf.DecrementSlotRefCount(target);
            }

            mmf.ClockHandForTests = hand + 64;   // the other waiters' sweeps
            return call < 40;
        };

        var memPageIndex = Request(mmf, 5000);

        Assert.That(memPageIndex, Is.EqualTo(target));
        Assert.That(_strategy.Calls, Is.LessThanOrEqualTo(CachePages / 32), "within one lap of pass 2");
    }

    /// <summary>
    /// A budget that does not divide the cache (48 into 1 024): the lap's last round is shorter, so the lap visits every slot exactly once and
    /// its census counts each held page once — not the 1 056 visits 22 full rounds would make.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ALapsCensus_CountsEverySlotOnce_WhenTheBudgetDoesNotDivideTheCache()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var pinned = FillAndPin(mmf, 1000);

        mmf.SweepBudgetFloor = 48;   // budget = max(48, 1024 / 32) = 48: 21 full rounds, then 16
        _strategy.Calls = 0;
        var visits = mmf.FailedSweepVisitsForTests;
        using (var guard = EpochGuard.Enter(mmf.EpochManager))
        {
            foreach (var slot in pinned)
            {
                Assert.That(mmf.RequestPageEpoch(mmf.GetFilePageIndex(slot), mmf.EpochManager.GlobalEpoch, out _), Is.True);
            }

            Assert.Throws<PageCacheBackpressureTimeoutException>(() => mmf.RequestPageEpoch(5000, mmf.EpochManager.GlobalEpoch, out _));
        }

        Assert.That(_strategy.Calls, Is.EqualTo(22), "a lap: 21 rounds of 48 pass-2 visits, then one of 16");
        Assert.That(mmf.FailedSweepVisitsForTests - visits, Is.EqualTo(22L * 48 + CachePages), "22 × 48 clock visits + exactly one lap of pass 2");
        Assert.That(_strategy.LastEpochCount, Is.EqualTo(pinned.Count), "each pinned page counted once");
    }

    [Test]
    [CancelAfter(10_000)]
    public void AtTheDefaultBudget_ARoundIsAWholeLap_AsBefore()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var pinned = FillAndPin(mmf, 1000);

        _strategy.Calls = 0;
        var visits = mmf.FailedSweepVisitsForTests;
        using (var guard = EpochGuard.Enter(mmf.EpochManager))
        {
            // Tag every pinned page in this epoch, so the census sees them all held by an epoch as well.
            foreach (var slot in pinned)
            {
                Assert.That(mmf.RequestPageEpoch(mmf.GetFilePageIndex(slot), mmf.EpochManager.GlobalEpoch, out _), Is.True);
            }

            Assert.Throws<PageCacheBackpressureTimeoutException>(() => mmf.RequestPageEpoch(5000, mmf.EpochManager.GlobalEpoch, out _));
        }

        Assert.That(_strategy.Calls, Is.EqualTo(1), "one round, then the timeout");
        Assert.That(mmf.FailedSweepVisitsForTests - visits, Is.EqualTo(3L * CachePages), "the clock twice round, then every slot once");
        Assert.That(_strategy.LastEpochCount, Is.EqualTo(pinned.Count), "the census: every pinned page, tagged in the current epoch");
    }

    [Test]
    [CancelAfter(10_000)]
    public void ARetryRound_TakesAPageAnotherThreadPublishedMeanwhile()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var pinned = FillAndPin(mmf, 1000);

        const int page = 5000;
        var theirs = -1;
        _strategy.Calls = 0;
        _strategy.OnCall = call =>
        {
            if (call == 1)
            {
                // A slot frees up, and another thread brings the same page in with it.
                mmf.DecrementSlotRefCount(pinned[0]);
                var other = new Thread(() => theirs = Request(mmf, page));
                other.Start();
                other.Join();
            }

            // Nothing else frees up: a waiter that kept sweeping would give up on the third round.
            return call < 3;
        };

        var ours = Request(mmf, page);

        Assert.That(theirs, Is.EqualTo(pinned[0]), "precondition: the other thread took the freed slot");
        Assert.That(ours, Is.EqualTo(theirs), "the waiter takes the published slot instead of waiting for one to discard");
        Assert.That(_strategy.Calls, Is.EqualTo(1));
    }
}
