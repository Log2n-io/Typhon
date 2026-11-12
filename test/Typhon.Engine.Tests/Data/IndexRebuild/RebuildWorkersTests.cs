using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The index rebuild's worker threads (#1588, design §9, RB-09): each runs in its own epoch scope, the coordinator may hold none, the first failure is the
/// one rethrown once every thread has exited, and the batch points are the workers' own.
/// </summary>
[TestFixture]
internal sealed class RebuildWorkersTests
{
    private ServiceProvider _provider;
    private EpochManager _epochs;

    [SetUp]
    public void Setup()
    {
        _provider = new ServiceCollection().AddLogging().AddResourceRegistry().AddMemoryAllocator().AddEpochManager().BuildServiceProvider();
        _epochs = _provider.GetRequiredService<EpochManager>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private RebuildEnvironment Environment(Action<ChangeSet, DatabaseEngine.RecoveryPass> batchPoint = null, List<Exception> others = null) => new()
    {
        Epochs = _epochs,
        CreateChangeSet = () => null,
        BatchPoint = batchPoint,
        OtherFailure = ex => others?.Add(ex),
    };

    [Test]
    [VerifiesRule("RB-09")]
    public void ACoordinatorHoldingAScope_StartsNoWorker()
    {
        var ran = 0;
        var depth = _epochs.EnterScope();
        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                RebuildWorkers.Run(Environment(), DatabaseEngine.RecoveryPass.IndexBuild, 2, _ => Interlocked.Increment(ref ran)));
            Assert.That(thrown.Message, Does.Contain("holds an epoch scope"));
        }
        finally
        {
            _epochs.ExitScope(depth);
        }

        Assert.That(ran, Is.Zero, "no worker ran");
    }

    [Test]
    public void EachWorker_RunsInItsOwnScope_AndItsBatchPointsAreCounted()
    {
        var depths = new int[4];
        var threads = new Thread[4];
        var passes = new List<DatabaseEngine.RecoveryPass>();
        var batchPoints = RebuildWorkers.Run(Environment((_, pass) =>
        {
            lock (passes)
            {
                passes.Add(pass);
            }
        }), DatabaseEngine.RecoveryPass.IndexStage, 4, worker =>
        {
            depths[worker.Index] = _epochs.CurrentThreadDepth;
            threads[worker.Index] = Thread.CurrentThread;
            for (var i = 0; i <= worker.Index; i++)
            {
                worker.BatchPoint();
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(depths, Is.All.EqualTo(1), "each worker inside one scope of its own");
            Assert.That(batchPoints, Is.EqualTo(1 + 2 + 3 + 4), "the batch points of every worker");
            Assert.That(passes, Has.Count.EqualTo(10).And.All.EqualTo(DatabaseEngine.RecoveryPass.IndexStage), "each made with the phase's pass");
            Assert.That(_epochs.CurrentThreadDepth, Is.Zero, "the coordinator still holds no scope");
            Assert.That(threads, Has.None.Matches<Thread>(t => t.IsAlive), "every thread exited");
        });
    }

    [Test]
    public void TheFirstFailure_IsRethrown_TheOthersStopOrAreReported()
    {
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var others = new List<Exception>();
        var failed = new ManualResetEventSlim();
        var stopped = 0;
        var threads = new Thread[3];
        var thrown = Assert.Throws<InvalidOperationException>(() => RebuildWorkers.Run(Environment(others: others), DatabaseEngine.RecoveryPass.IndexBuild, 3,
            worker =>
            {
                threads[worker.Index] = Thread.CurrentThread;
                switch (worker.Index)
                {
                    case 0:
                        failed.Set();
                        throw first;
                    case 1:
                        failed.Wait();
                        SpinUntilAborted(worker);
                        throw second;   // fails after the first: reported, not rethrown
                    default:
                        failed.Wait();
                        try
                        {
                            SpinUntilAborted(worker);
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref stopped);
                            throw;
                        }

                        break;
                }
            }));

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(first), "the first failure is the one rethrown");
            Assert.That(others, Is.EqualTo(new Exception[] { second }), "a later failure is reported");
            Assert.That(stopped, Is.EqualTo(1), "a worker still running stopped at its next check");
            Assert.That(threads, Has.None.Matches<Thread>(t => t.IsAlive), "every thread exited");
        });
    }

    /// <summary>
    /// A worker's ChangeSet is saved by the coordinator after the join, never by the worker as it ends: a save writes its pages from live memory, checksum
    /// stamped first, and the workers of a phase write the same pages, so a save beside a running sibling put a page on disk that failed its CRC on the
    /// next open (3 opens in 48 under load). Here the first worker dirties a page and exits; the second, still running, sees nothing written yet.
    /// </summary>
    [Test]
    [VerifiesRule("RB-09")]
    public void AWorkersChangeSet_IsSavedAfterEveryWorkerHasExited() => WithDatabase(SaveAfterJoin);

    /// <summary>
    /// A failed phase saves nothing and gives every worker's dirty marks back (CK-08), the marks of a worker that ended before the failure included: the
    /// open fails and the next one rebuilds.
    /// </summary>
    [Test]
    [VerifiesRule("RB-09")]
    public void AFailedPhase_SavesNothing_AndReleasesEveryWorkersMarks() => WithDatabase((mmf, epochs) =>
    {
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 8, 64);
        var marksBefore = DirtyMarks(mmf);
        var written = mmf.DataFilePagesWritten;
        var firstDone = new ManualResetEventSlim();
        Thread first = null;
        var marked = 0;
        var failure = new InvalidOperationException("second worker");

        var thrown = Assert.Throws<InvalidOperationException>(() => RebuildWorkers.Run(
            new RebuildEnvironment { Epochs = epochs, CreateChangeSet = () => mmf.CreateChangeSet() }, DatabaseEngine.RecoveryPass.IndexBuild, 2, worker =>
            {
                if (worker.Index == 0)
                {
                    first = Thread.CurrentThread;
                    DirtyOneChunk(segment, worker.ChangeSet);
                    marked = worker.ChangeSet.MarkedPageCount;
                    firstDone.Set();
                    return;
                }

                firstDone.Wait();
                Assert.That(first.Join(TimeSpan.FromSeconds(10)), Is.True, "premise: the first worker exited");
                throw failure;
            }));

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(marked, Is.GreaterThan(0), "premise: the first worker marked pages");
            Assert.That(DirtyMarks(mmf), Is.EqualTo(marksBefore), "every mark given back");
            Assert.That(mmf.DataFilePagesWritten - written, Is.Zero, "nothing saved");
        });
    });

    /// <summary>A progress report that throws is logged as another failure and not called again; the phase, and the open, go on.</summary>
    [Test]
    public void AThrowingProgressReport_IsReported_AndThePhaseCompletes()
    {
        var others = new List<Exception>();
        var reported = new ManualResetEventSlim();
        var reports = 0;
        var failure = new InvalidOperationException("report");
        var previous = RebuildWorkers.ProgressCadenceForTest;
        RebuildWorkers.ProgressCadenceForTest = TimeSpan.FromMilliseconds(1);
        try
        {
            RebuildWorkers.Run(Environment(others: others), DatabaseEngine.RecoveryPass.IndexBuild, 1, _ =>
                {
                    Assert.That(reported.Wait(TimeSpan.FromSeconds(10)), Is.True, "premise: a report ran while the worker waited");
                },
                () =>
                {
                    Interlocked.Increment(ref reports);
                    reported.Set();
                    throw failure;
                });
        }
        finally
        {
            RebuildWorkers.ProgressCadenceForTest = previous;
        }

        Assert.Multiple(() =>
        {
            Assert.That(others, Is.EqualTo(new Exception[] { failure }), "the report's failure is logged");
            Assert.That(reports, Is.EqualTo(1), "not called again");
        });
    }

    private static void WithDatabase(Action<ManagedPagedMMF, EpochManager> test)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"typhon-rebuildworkers-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        var services = new ServiceCollection();
        services.AddLogging().AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = "saveafterjoin";
                options.DatabaseDirectory = directory;
                options.DatabaseCacheSize = (ulong)(1_024 * PagedMMF.PageSize);
            });
        try
        {
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            test(scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>(), scope.ServiceProvider.GetRequiredService<EpochManager>());
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // a temp directory; the OS cleans it if a handle outlived the test
            }
        }
    }

    // Every page's dirty counter, summed: a mark added to a page already dirty shows here, not in a count of dirty pages.
    private static long DirtyMarks(ManagedPagedMMF mmf)
    {
        var sum = 0L;
        for (var i = 0; i < mmf.PageTraceSlotCount; i++)
        {
            sum += mmf.DirtyCounterOf(i);
        }

        return sum;
    }

    private static unsafe void DirtyOneChunk(ChunkBasedSegment<PersistentStore> segment, ChangeSet changeSet)
    {
        var accessor = segment.CreateChunkAccessor(changeSet);
        try
        {
            var chunk = segment.AllocateChunk(changeSet, ref accessor);
            *(long*)accessor.GetChunkAddress(chunk, dirty: true) = 42;
            accessor.CommitChanges();
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static void SaveAfterJoin(ManagedPagedMMF mmf, EpochManager epochs)
    {
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 8, 64);
        var written = mmf.DataFilePagesWritten;
        var firstDone = new ManualResetEventSlim();
        Thread first = null;
        var writtenWhileRunning = -1L;

        RebuildWorkers.Run(new RebuildEnvironment { Epochs = epochs, CreateChangeSet = () => mmf.CreateChangeSet() }, DatabaseEngine.RecoveryPass.IndexBuild,
            2, worker =>
            {
                if (worker.Index == 0)
                {
                    first = Thread.CurrentThread;
                    DirtyOneChunk(segment, worker.ChangeSet);
                    firstDone.Set();
                    return;
                }

                firstDone.Wait();
                Assert.That(first.Join(TimeSpan.FromSeconds(10)), Is.True, "premise: the first worker exited");
                writtenWhileRunning = mmf.DataFilePagesWritten - written;
            });

        Assert.Multiple(() =>
        {
            Assert.That(writtenWhileRunning, Is.Zero, "nothing saved while a worker still runs");
            Assert.That(mmf.DataFilePagesWritten - written, Is.GreaterThan(0), "the first worker's pages saved once the phase ended");
        });
    }

    // Waits for the abort the first failure raises: ThrowIfAborted throws once it is seen.
    private static void SpinUntilAborted(RebuildWorker worker)
    {
        var wait = new SpinWait();
        try
        {
            while (true)
            {
                worker.ThrowIfAborted();
                wait.SpinOnce(-1);
            }
        }
        catch (OperationCanceledException) when (worker.Index == 1)
        {
            // worker 1 goes on to fail on its own
        }
    }
}
