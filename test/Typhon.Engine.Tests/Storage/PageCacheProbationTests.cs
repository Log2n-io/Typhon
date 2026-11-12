using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Scan-resistant eviction (I8, PS-25) on a bare page cache: a first load waits on probation until a different operation asks for it again, the probation
/// area's target adapts to the ghost hits, and a walk larger than the cache recycles its own pages instead of the working set.
/// </summary>
[TestFixture]
class PageCacheProbationTests
{
    private const int Slots = 256;
    private const string Marker = "PS-25";

    private ServiceProvider _serviceProvider;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_db".Replace('(', '_').Replace(')', '_').Replace(',', '_').Replace('"', '_');

    private (IServiceScope Scope, PagedMMF Cache, EpochManager Epochs) Open(bool scanResistant = true, int slots = Slots)
    {
        var collection = new ServiceCollection();
        collection
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)slots * PagedMMF.PageSize;
                options.TestMode = true;
                options.ScanResistantEviction = scanResistant;
            });
        _serviceProvider = collection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
        var scope = _serviceProvider.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<PagedMMF>(), scope.ServiceProvider.GetRequiredService<EpochManager>());
    }

    [TearDown]
    public void TearDown() => _serviceProvider?.Dispose();

    /// <summary>One request of <paramref name="page"/> by operation <paramref name="op"/>, its epoch scope closed after; true when it missed.</summary>
    private static bool Request(PagedMMF cache, EpochManager epochs, long op, int page)
    {
        cache.TryGetPageResidency(page, out var resident, out _);
        if (op != 0)
        {
            PagedMMF.EnterOperation(op);
        }

        try
        {
            using var guard = EpochGuard.Enter(epochs);
            Assert.That(cache.RequestPageEpoch(page, epochs.GlobalEpoch, out _), Is.True);
        }
        finally
        {
            if (op != 0)
            {
                PagedMMF.ExitOperation(op);
            }
        }

        return !resident;
    }

    private static bool Resident(PagedMMF cache, int page) => cache.TryGetPageResidency(page, out var resident, out _) && resident;

    /// <summary>
    /// A hot set read by many operations, then one operation's walk four times the cache: the hot set is still resident. On the plain clock the walk
    /// evicts it.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("PS-25")]
    public void AHotSet_SurvivesAWalkLargerThanTheCache()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            var kept = HotSetKeptAcrossAWalk(cache, epochs);
            Assert.That(kept, Is.EqualTo(64), $"{Marker}: every hot page survives the walk");
        }
    }

    [Test]
    [CancelAfter(10_000)]
    [RuleMutant("PS-25")]
    public void Mutant_ThePlainClock_LosesTheHotSet()
    {
        var (scope, cache, epochs) = Open(scanResistant: false);
        using (scope)
        {
            var kept = HotSetKeptAcrossAWalk(cache, epochs);
            RuleMutants.AssertDetects("PS-25", Marker, () => Assert.That(kept, Is.EqualTo(64), $"{Marker}: every hot page survives the walk"));
        }
    }

    private static int HotSetKeptAcrossAWalk(PagedMMF cache, EpochManager epochs)
    {
        const int hot = 64;
        long op = 1;
        for (var round = 0; round < 4; round++)
        {
            for (var p = 0; p < hot; p++)
            {
                Request(cache, epochs, op++, p);
            }
        }

        const long walk = 1_000_000;
        for (var p = 0; p < Slots * 4; p++)
        {
            Request(cache, epochs, walk, 10_000 + p);
        }

        return Enumerable.Range(0, hot).Count(p => Resident(cache, p));
    }

    /// <summary>A first load is on probation; its own operation's next request leaves it there; another operation's promotes it to the clock.</summary>
    [Test]
    [CancelAfter(5_000)]
    public void APageAskedForByAnotherOperation_JoinsTheClock()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            Request(cache, epochs, 10, 5);
            Assert.That(cache.ProbationCount, Is.EqualTo(1), "a first load is on probation");
            Request(cache, epochs, 10, 5);
            Assert.That((cache.ProbationCount, cache.ProbationPromotions), Is.EqualTo((1, 0L)), "its own operation neither promotes nor demotes it");
            Request(cache, epochs, 11, 5);
            Assert.That((cache.ProbationCount, cache.ProbationPromotions), Is.EqualTo((0, 1L)), "another operation promotes it");
        }
    }

    /// <summary>Outside any operation — the checkpoint, a fence, recovery — a load goes to the clock, as it always did.</summary>
    [Test]
    [CancelAfter(5_000)]
    public void ALoadOutsideAnyOperation_GoesToTheClock()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            Request(cache, epochs, 0, 5);
            Assert.That(cache.ProbationCount, Is.Zero);
        }
    }

    /// <summary>A walk re-reading its own pages promotes none of them.</summary>
    [Test]
    [CancelAfter(5_000)]
    public void AWalksOwnRereads_PromoteNothing()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            for (var pass = 0; pass < 3; pass++)
            {
                for (var p = 0; p < Slots / 2; p++)
                {
                    Request(cache, epochs, 77, p);
                }
            }

            Assert.That(cache.ProbationPromotions, Is.Zero);
        }
    }

    /// <summary>
    /// A walk of twice the cache lets most of its pages go; another operation then asks for them: each is a miss on a page probation let go, which
    /// grows the target, and loads straight into the clock.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AMissOnAPageProbationLetGo_GrowsTheTarget_AndJoinsTheClock()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            var start = cache.ProbationTarget;
            for (var p = 0; p < Slots * 2; p++)
            {
                Request(cache, epochs, 1, p);
            }

            var probationBefore = cache.ProbationCount;
            for (var p = 0; p < 32; p++)
            {
                Request(cache, epochs, 2, p);
            }

            Assert.That(cache.ProbationGhostHits, Is.GreaterThanOrEqualTo(32), "the first pages are ghosts of probation");
            Assert.That(cache.ProbationTarget, Is.GreaterThan(start), "each grew the target");
            Assert.That(cache.ProbationCount, Is.LessThanOrEqualTo(probationBefore), "another operation's re-reference loads into the clock, not probation");
        }
    }

    /// <summary>
    /// A working set larger than the clock, read by alternating operations: the clock lets pages go and they come back, a miss on a ghost of the clock
    /// each time, which shrinks the target towards its floor.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AMissOnAPageTheClockLetGo_ShrinksTheTarget()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            // Grow the target first, with a walk and other operations' re-reads of what it let go.
            for (var p = 0; p < Slots * 2; p++)
            {
                Request(cache, epochs, 1, 100_000 + p);
            }

            for (var p = 0; p < Slots; p++)
            {
                Request(cache, epochs, 2, 100_000 + p);
            }

            var grown = cache.ProbationTarget;
            long op = 10;
            for (var round = 0; round < 6; round++)
            {
                for (var p = 0; p < Slots * 3 / 2; p++)
                {
                    Request(cache, epochs, op++, p);
                    Request(cache, epochs, op++, p);
                }
            }

            Assert.That(cache.ClockGhostHits, Is.GreaterThan(0), "the clock's pages came back");
            Assert.That(cache.ProbationTarget, Is.LessThan(grown), "and shrank the target");
        }
    }

    /// <summary>
    /// With no walk, a working set at 90 % of the cache read by changing operations misses no more than on the plain clock, within one percent of the
    /// requests: nothing is reserved for probation.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void WithNoWalk_AWorkingSetNearTheCacheSizeMissesAsOnTheClock()
    {
        int Misses(bool scanResistant)
        {
            var (scope, cache, epochs) = Open(scanResistant);
            using (scope)
            {
                var rand = new Random(3);
                var misses = 0;
                long op = 1;
                for (var i = 0; i < 40_000; i++)
                {
                    if (i % 16 == 0)
                    {
                        op++;
                    }

                    if (Request(cache, epochs, op, rand.Next(Slots * 9 / 10)) && i >= 10_000)
                    {
                        misses++;
                    }
                }

                return misses;
            }
        }

        var clock = Misses(false);
        var adaptive = Misses(true);
        Assert.That(adaptive, Is.LessThanOrEqualTo(clock + 300), $"adaptive {adaptive} misses against the clock's {clock}, over 30,000 requests");
    }

    /// <summary>A walk half the cache wide, run again by a second operation, then by a third: the third finds every page resident.</summary>
    [Test]
    [CancelAfter(10_000)]
    public void ARepeatedWalk_CachesItself()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            for (long op = 1; op <= 2; op++)
            {
                for (var p = 0; p < Slots / 2; p++)
                {
                    Request(cache, epochs, op, p);
                }
            }

            var misses = 0;
            for (var p = 0; p < Slots / 2; p++)
            {
                misses += Request(cache, epochs, 3, p) ? 1 : 0;
            }

            Assert.That(misses, Is.Zero);
        }
    }

    /// <summary>
    /// A probation page a slot reference holds is never taken, however far a walk goes past it (PS-01): the take requeues it. A slot reference, not an
    /// epoch scope: a scope held across the walk would protect every page the walk loads too, and the walk would have nothing to take.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AProtectedProbationPage_IsNeverTaken()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            PagedMMF.EnterOperation(5);
            int slot;
            using (EpochGuard.Enter(epochs))
            {
                Assert.That(cache.RequestPageEpoch(42, epochs.GlobalEpoch, out slot), Is.True);
                cache.IncrementSlotRefCount(slot);
            }

            PagedMMF.ExitOperation(5);
            Assert.That(cache.ProbationCount, Is.EqualTo(1), "on probation");

            var takesBefore = cache.ProbationTakes;
            for (var p = 0; p < Slots * 4; p++)
            {
                Request(cache, epochs, 6, 5_000 + p);
            }

            Assert.That(cache.ProbationTakes - takesBefore, Is.GreaterThan(Slots), "the walk recycled probation pages");
            Assert.That(Resident(cache, 42), Is.True, "held by its slot reference: still resident");
            Assert.That(cache.ProbationTagForTests(slot), Is.Not.Zero, "and still on probation");
            cache.DecrementSlotRefCount(slot);
        }
    }

    /// <summary>
    /// A probation page promoted between the take's pop and its reclaim is a clock page by then: the reclaim checks under the slot's lock and leaves it.
    /// Staged at the reclaim's probe, before its lock: the page is promoted there, once, without touching its epoch.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void APagePromotedDuringTheTake_IsLeftToTheClock()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            for (var p = 0; p < Slots * 2; p++)
            {
                Request(cache, epochs, 1, 10_000 + p);   // the cache is full, probation at its target
            }

            var promoted = -1;
            var promotedPage = -1;
            cache.ReclaimBeforeLockProbe = mem =>
            {
                if (promoted < 0 && cache.ProbationTagForTests(mem) != 0)
                {
                    promoted = mem;
                    promotedPage = cache.FilePageOfSlotForTests(mem);
                    cache.PromoteForTests(mem);
                }
            };
            try
            {
                Request(cache, epochs, 1, 50_000);
            }
            finally
            {
                cache.ReclaimBeforeLockProbe = null;
            }

            Assert.That(promoted, Is.Not.Negative, "the take reached a probation page");
            Assert.That(Resident(cache, promotedPage), Is.True, "promoted on the way: the take left it");
            Assert.That(cache.ProbationTagForTests(promoted), Is.Zero, "a clock page now");
            var tagged = Enumerable.Range(0, Slots).Count(s => cache.ProbationTagForTests(s) != 0);
            Assert.That(cache.ProbationCount, Is.EqualTo(tagged));
        }
    }

    /// <summary>
    /// A walk one and a half times the cache, run four times by one operation — each pass misses on the ghosts the last let go — grows the target to its
    /// ceiling at most: half the cache. (A walk four times the cache would never hit them: its own evictions age them out first.)
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void TheTarget_NeverExceedsHalfTheCache()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                for (var p = 0; p < Slots * 3 / 2; p++)
                {
                    Request(cache, epochs, 1, p);
                }
            }

            Assert.That(cache.ProbationGhostHits, Is.GreaterThan(0), "the second pass missed on probation's ghosts");
            Assert.That(cache.ProbationTarget, Is.InRange(Slots / 16 + 1, Slots / 2), "they grew the target, up to its ceiling");
        }
    }

    /// <summary>With the option off, nothing is ever on probation: the plain clock.</summary>
    [Test]
    [CancelAfter(5_000)]
    public void WithTheOptionOff_NoPageIsOnProbation()
    {
        var (scope, cache, epochs) = Open(scanResistant: false);
        using (scope)
        {
            for (var p = 0; p < Slots * 2; p++)
            {
                Request(cache, epochs, 1 + p % 3, p);
            }

            var tagged = Enumerable.Range(0, Slots).Count(s => cache.ProbationTagForTests(s) != 0);
            Assert.That((cache.ProbationCount, tagged, cache.ProbationTarget), Is.EqualTo((0, 0, 0)));
        }
    }

    /// <summary>
    /// A page its own operation asked for again gets one more lap of the probation queue (CAR's T1 bit); a page loaded beside it and not asked for again
    /// goes at the first.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void APageItsOperationAskedForAgain_GetsOneMoreLap()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            for (var p = 0; p < Slots * 2; p++)
            {
                Request(cache, epochs, 1, 10_000 + p);   // fills the cache with one walk: probation holds every slot
            }

            Assert.That(cache.ProbationCount, Is.EqualTo(Slots));
            Request(cache, epochs, 1, 1);
            Request(cache, epochs, 1, 1);   // its bit
            Request(cache, epochs, 1, 2);
            for (var p = 0; p < Slots + 4; p++)   // one lap of the queue, and a few
            {
                Request(cache, epochs, 1, 20_000 + p);
            }

            Assert.That(cache.ProbationSecondChances, Is.GreaterThan(0));
            Assert.That((Resident(cache, 1), Resident(cache, 2)), Is.EqualTo((true, false)), "the re-read page got a second lap, its neighbour none");
        }
    }

    /// <summary>
    /// A page its own operation asks for again in two laps of the queue — an index's interior node under a walk — joins the clock at its next take; a
    /// page asked for again in only one lap leaves after that lap.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void APageItsOperationKeepsComingBackTo_JoinsTheClock()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            for (var p = 0; p < Slots * 2; p++)
            {
                Request(cache, epochs, 1, 10_000 + p);   // fills the cache with one walk: probation holds every slot
            }

            Request(cache, epochs, 1, 1);
            Request(cache, epochs, 1, 1);   // its bit
            Request(cache, epochs, 1, 2);
            Request(cache, epochs, 1, 2);   // its bit, but no re-read in its second lap
            var next = 20_000;
            for (var p = 0; p < Slots + 4; p++)
            {
                Request(cache, epochs, 1, next++);   // one lap: both go round once more
            }

            Request(cache, epochs, 1, 1);   // asked for again in its second lap
            var promotions = cache.ProbationPromotions;
            for (var p = 0; p < Slots + 4; p++)
            {
                Request(cache, epochs, 1, next++);
            }

            Assert.That(cache.ProbationPromotions, Is.GreaterThan(promotions), "the page re-read in two laps was promoted");
            Assert.That(cache.GetClockSweepCounterForDiagnostic(1), Is.LessThanOrEqualTo(1),
                "into the clock as a fresh load: at 5, a walk's re-read pages outlast the working set");
            Assert.That((Resident(cache, 1), Resident(cache, 2)), Is.EqualTo((true, false)), "it stays; the page re-read in one lap only does not");
            var tagged = Enumerable.Range(0, Slots).Count(s => cache.ProbationTagForTests(s) != 0);
            Assert.That(cache.ProbationCount, Is.EqualTo(tagged));
        }
    }

    /// <summary>
    /// Pass 2, which takes any evictable slot once the clock and the probation queue found none, passes over a probation page's counter: that counter is
    /// its own operation's reference state, not an age. Staged with every slot held by a slot reference, so all three come back empty-handed and the
    /// request times out after pass 2 swept the whole cache.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void PassTwo_NeverAgesAProbationPage()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            var held = new List<int>(Slots);
            void Hold(long op, int page)
            {
                if (op != 0)
                {
                    PagedMMF.EnterOperation(op);
                }

                try
                {
                    using var guard = EpochGuard.Enter(epochs);
                    Assert.That(cache.RequestPageEpoch(page, epochs.GlobalEpoch, out var slot), Is.True);
                    cache.IncrementSlotRefCount(slot);
                    held.Add(slot);
                }
                finally
                {
                    if (op != 0)
                    {
                        PagedMMF.ExitOperation(op);
                    }
                }
            }

            // Loaded on probation, at 1, and held without a request: a request of its own operation would set its bit, which the take then spends.
            Request(cache, epochs, 5, 0);
            var probationSlot = Enumerable.Range(0, Slots).Single(s => cache.ProbationTagForTests(s) != 0);
            cache.IncrementSlotRefCount(probationSlot);
            held.Add(probationSlot);
            for (var p = 1; p < Slots; p++)
            {
                Hold(0, p);   // the clock's, every other slot
            }

            Assert.That((cache.ProbationCount, cache.GetClockSweepCounterForDiagnostic(0)), Is.EqualTo((1, 1)), "premise: one probation page, at its load's 1");

            var saved = TimeoutOptions.Current;
            var timeouts = saved.Clone();
            timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(100);
            TimeoutOptions.Current = timeouts;
            var visits = Interlocked.Read(ref cache.FailedSweepVisitsForTests);
            try
            {
                Assert.That(() => Request(cache, epochs, 0, 10_000), Throws.InstanceOf<PageCacheBackpressureTimeoutException>(), "no slot to take");
            }
            finally
            {
                TimeoutOptions.Current = saved;
            }

            Assert.That(Interlocked.Read(ref cache.FailedSweepVisitsForTests) - visits, Is.GreaterThanOrEqualTo(3L * Slots),
                "premise: a round's two passes ran, pass 2 over every slot");
            Assert.That(cache.GetClockSweepCounterForDiagnostic(0), Is.EqualTo(1), "pass 2 left the probation page's reference state alone");
            Assert.That(cache.ProbationCount, Is.EqualTo(1), "and on probation");

            foreach (var slot in held)
            {
                cache.DecrementSlotRefCount(slot);
            }
        }
    }

    /// <summary>
    /// Several threads of one operation asking for one probation page at once — workers of one lease — advance its reference state one step, not one per
    /// thread: from loaded (1) to its reference bit (2), never past it into the second lap it has not had. Each round loads a fresh page and releases
    /// the threads on it together.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ConcurrentRequestsOfOneOperation_AdvanceAPageOnce()
    {
        const int slots = 1_024;
        const int workers = 8;
        const int rounds = 400;
        const long op = 3;
        var (scope, cache, epochs) = Open(slots: slots);
        using (scope)
        {
            using var barrier = new Barrier(workers + 1);
            var page = -1;
            var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
            {
                for (var r = 0; r < rounds; r++)
                {
                    barrier.SignalAndWait();
                    try
                    {
                        Request(cache, epochs, op, Volatile.Read(ref page));
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue(e);
                    }

                    barrier.SignalAndWait();
                }
            }) { IsBackground = true }).ToArray();
            foreach (var t in threads)
            {
                t.Start();
            }

            var states = new int[6];
            for (var r = 0; r < rounds; r++)
            {
                Volatile.Write(ref page, 100 + r);
                Request(cache, epochs, op, 100 + r);   // loaded on probation: 1
                barrier.SignalAndWait();               // the workers ask for it together
                barrier.SignalAndWait();
                states[cache.GetClockSweepCounterForDiagnostic(100 + r)]++;
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            Assert.That(failures, Is.Empty);
            Assert.That(states[2], Is.EqualTo(rounds), $"every page at its reference bit; by state: {string.Join(", ", states)}");
            Assert.That((cache.ProbationCount, cache.ProbationPromotions), Is.EqualTo((rounds, 0L)), "none promoted: one operation");
        }
    }

    /// <summary>
    /// Operations ending out of order — the first transaction disposed before the second — never leave the thread in one that has ended; nested ones
    /// put back the outer one.
    /// </summary>
    [Test]
    public void OperationsEndingOutOfOrder_LeaveNoEndedOperation()
    {
        PagedMMF.EnterOperation(1);
        PagedMMF.EnterOperation(2);
        PagedMMF.ExitOperation(2);
        Assert.That(PagedMMF.OperationForTests, Is.EqualTo(1), "nested: the outer one is back");
        PagedMMF.EnterOperation(3);
        PagedMMF.ExitOperation(1);
        Assert.That(PagedMMF.OperationForTests, Is.EqualTo(3), "the outer one ended first: the inner one stays");
        PagedMMF.ExitOperation(3);
        Assert.That(PagedMMF.OperationForTests, Is.Zero, "and none is left");
        PagedMMF.ExitOperation(42);
        Assert.That(PagedMMF.OperationForTests, Is.Zero, "an unknown one changes nothing");
    }

    /// <summary>
    /// Many promotions while probation sits under its target fill the queue with stale entries: the compaction removes them and keeps the live ones in
    /// order, and every page still on probation can still be taken: a walk four times the cache leaves none of them.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AfterTheQueueChurns_EveryProbationPageCanStillBeTaken()
    {
        var (scope, cache, epochs) = Open();
        using (scope)
        {
            var live = new List<int>();
            long op = 100;
            for (var i = 0; i < Slots * 8; i++)
            {
                if (i % 64 == 0 && live.Count < 8)
                {
                    live.Add(50_000 + i);
                    Request(cache, epochs, 7, 50_000 + i);   // stays on probation
                }

                Request(cache, epochs, op, i);
                Request(cache, epochs, op + 1, i);   // promoted: a stale entry
                op += 2;
            }

            Assert.That(cache.ProbationQueueCompactions, Is.GreaterThan(0), "the queue filled and was compacted");
            Assert.That(cache.ProbationQueueLength, Is.LessThanOrEqualTo(Slots * 2));
            var tagged = Enumerable.Range(0, Slots).Count(s => cache.ProbationTagForTests(s) != 0);
            Assert.That(cache.ProbationCount, Is.EqualTo(tagged));

            for (var p = 0; p < Slots * 4; p++)
            {
                Request(cache, epochs, 9, 200_000 + p);
            }

            Assert.That(live.Count(p => Resident(cache, p)), Is.Zero, "no probation page lost its entry");
        }
    }

    /// <summary>
    /// The ghost list against a model: a page is a member when its last add is among the last <c>capacity</c> adds and no remove followed. Random adds
    /// and removes over few pages, so homes collide and the backward-shift delete moves entries.
    /// </summary>
    [Test]
    public void TheGhostList_MatchesItsModel()
    {
        const int capacity = 64;
        var list = new ProbationPolicy.GhostList(capacity);
        try
        {
            var lastAdd = new Dictionary<int, (long At, byte Tag)>();
            var rand = new Random(11);
            long adds = 0;
            for (var i = 0; i < 20_000; i++)
            {
                var page = rand.Next(200);
                if (rand.Next(3) != 0)
                {
                    var tag = (byte)rand.Next(256);
                    list.Add(page, tag);
                    lastAdd[page] = (adds++, tag);
                }
                else
                {
                    var member = lastAdd.TryGetValue(page, out var a) && adds - a.At <= capacity;
                    Assert.That(list.TryRemove(page, out var tag), Is.EqualTo(member), $"step {i}: page {page}");
                    if (member)
                    {
                        Assert.That(tag, Is.EqualTo(a.Tag));
                    }

                    lastAdd.Remove(page);
                }

                if (i % 97 == 0)
                {
                    Assert.That(list.Count, Is.EqualTo(lastAdd.Values.Count(a => adds - a.At <= capacity)), $"step {i}: count");
                }
            }
        }
        finally
        {
            list.Dispose();
        }
    }

    /// <summary>
    /// Several threads, operations changing every few requests, a walk among them: the probation count always matches the slots that
    /// carry a probation tag once the threads stop, and no request fails. 1,024 slots: under 8 MiB a cache has no slot reserve (PS-24), and six pinned
    /// threads on 64 slots wait on each other's epochs whatever the policy.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void UnderConcurrency_TheProbationCountMatchesTheSlots()
    {
        const int slots = 1_024;
        var (scope, cache, epochs) = Open(slots: slots);
        using (scope)
        {
            var threads = Enumerable.Range(0, 6).Select(t => Task.Run(() =>
            {
                var rand = new Random(t);
                long op = 1_000 * (t + 1);
                for (var i = 0; i < 20_000; i++)
                {
                    if (t == 0)
                    {
                        Request(cache, epochs, 99, 1_000_000 + i);   // the walk
                        continue;
                    }

                    if (i % 8 == 0)
                    {
                        op++;
                    }

                    Request(cache, epochs, op, rand.Next(3 * slots));
                }
            })).ToArray();
            Task.WaitAll(threads);

            var tagged = 0;
            for (var s = 0; s < slots; s++)
            {
                tagged += cache.ProbationTagForTests(s) != 0 ? 1 : 0;
            }

            Assert.That(cache.ProbationCount, Is.EqualTo(tagged), "the count is the slots on probation");
        }
    }
}
