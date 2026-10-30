using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// A checkpoint's page copy reaches the data file only after the WAL is durable through what it can reflect (CK-02): the write, not the fsync, is what a
/// killed process
/// leaves behind. Flushing at the fsync let a hard kill keep half a committed transaction, a spawn's page without the update beside it (#1516, #1422).
/// </summary>
[TestFixture]
internal sealed class CheckpointWriteOrderTests : TestBase<CheckpointWriteOrderTests>
{
    private GatedWalFileIO _wal;

    protected override IWalFileIO CreateWalFileIO() => _wal = new GatedWalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Resources.CheckpointDirtyPageThresholdPercent = 0;
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-02")]
    public void ACommitAfterTheBarrier_IsDurableBeforeAPageThatReflectsItIsWritten()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var a = new CompA(1, 1, 1);
        EntityId existing;
        using (var tx = dbe.CreateQuickTransaction())
        {
            existing = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tx.Commit();
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");

        // In the window the barrier does not cover: published, its page effects in the cache, and held out of the WAL file, as a commit is until its flush.
        using var staged = new ManualResetEventSlim();
        var published = 0L;
        var writesBeforeDurable = 0;
        var writes = 0;
        dbe.CheckpointManager.AfterBarrierProbe = () =>
        {
            if (Volatile.Read(ref published) != 0)
            {
                return;
            }

            _wal.Hold();
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);
            using var tx = uow.CreateTransaction();
            var e = tx.OpenMut(existing);
            var c = e.Read(CompAArch.A);
            c.B = 2;
            e.Set(CompAArch.A, c);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tx.Commit();
            Volatile.Write(ref published, dbe.WalManager.LastPublishedLsn);
            staged.Set();
        };
        dbe.MMF.PageWriteInterceptor = _ =>
        {
            var lsn = Volatile.Read(ref published);
            if (lsn != 0)
            {
                Interlocked.Increment(ref writes);
                if (dbe.WalManager.DurableLsn < lsn)
                {
                    Interlocked.Increment(ref writesBeforeDurable);
                }
            }
        };

        try
        {
            var cycle = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(15)));
            Assert.That(staged.Wait(TimeSpan.FromSeconds(10)), Is.True, "the cycle never reached its barrier");

            // Time for an out-of-order write to happen, and nothing depends on it in order: the cycle cannot pass its flush until the release.
            Assert.That(_wal.WriterHeld.Wait(TimeSpan.FromSeconds(10)), Is.True, "premise: the WAL writer holds the commit back");
            var patience = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref writes) == 0 && Stopwatch.GetElapsedTime(patience).TotalMilliseconds < 50)
            {
                Thread.Yield();
            }

            _wal.Release();
            cycle.GetAwaiter().GetResult();
        }
        finally
        {
            _wal.Release();
            dbe.MMF.PageWriteInterceptor = null;
            dbe.CheckpointManager.AfterBarrierProbe = null;
        }

        Assert.That(writes, Is.GreaterThan(0), "the staged commit dirtied no page the cycle wrote");
        Assert.That(writesBeforeDurable, Is.Zero, "a page was written before the WAL was durable through what it can reflect");
    }

    private DatabaseEngine OpenWithDirtyPages(int entities, out int[] pages)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var a = new CompA(1, 1, 1);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entities; i++)
            {
                tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            }

            tx.Commit();
        }

        pages = dbe.MMF.CollectDirtyMemPageIndices();
        Assert.That(pages, Is.Not.Empty, "premise: pages to write");
        return dbe;
    }

    /// <summary>
    /// A run's copies wait on the WAL, and a direct save settles their pages meanwhile: the copies are stale, and written they would go over what the save
    /// wrote,
    /// on a page that no longer owes anything, so eviction would lose it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-02")]
    public void ACopyADirectSaveOvertook_IsNotWritten()
    {
        using var dbe = OpenWithDirtyPages(10, out var pages);
        using var pool = new StagingBufferPool(MemoryAllocator, AllocationResource, StagingBufferPool.MinCapacity);
        var writes = 0;
        var writesOfTheSave = 0;
        dbe.MMF.PageWriteInterceptor = _ => Interlocked.Increment(ref writes);
        try
        {
            // One run: the barrier fires once, over copies all taken before it.
            var run = pages.Where(p => dbe.MMF.CapturedGenOf(p) != dbe.MMF.WritebackGenOf(p)).Take(PagedMMF.CaptureRunPages(pool.PoolCapacity)).ToArray();
            Assert.That(run, Is.Not.Empty, "premise: pages owed");
            var staleBefore = Volatile.Read(ref dbe.MMF.CheckpointSkipStaleCopy);
            dbe.MMF.WritePagesForCheckpoint(run, pool, out var written, default, CheckpointWriteFilter.All, () =>
            {
                dbe.MMF.SavePages([.. run]).GetAwaiter().GetResult();
                writesOfTheSave = Volatile.Read(ref writes);
                return true;
            });

            Assert.That(writesOfTheSave, Is.GreaterThan(0), "premise: the save wrote");
            Assert.That(written, Is.Zero);
            Assert.That(Volatile.Read(ref writes), Is.EqualTo(writesOfTheSave), "nothing stale reached the file");
            Assert.That(Volatile.Read(ref dbe.MMF.CheckpointSkipStaleCopy) - staleBefore, Is.EqualTo(run.Length));
        }
        finally
        {
            dbe.MMF.PageWriteInterceptor = null;
        }
    }

    /// <summary>A direct save waits for the run writing: one writer of the file per page, and neither order of two writes of it is safe to leave to the
    /// OS.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-02")]
    public void ADirectSave_WaitsForARunWriting()
    {
        using var dbe = OpenWithDirtyPages(10, out var pages);
        using var pool = new StagingBufferPool(MemoryAllocator, AllocationResource, StagingBufferPool.MinCapacity);
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = 0;
        dbe.MMF.PageWriteInterceptor = _ =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                inWrite.Set();
                release.Wait();
            }
        };
        try
        {
            var run = Task.Run(() => dbe.MMF.WritePagesForCheckpoint([.. pages], pool, out _));
            Assert.That(inWrite.Wait(TimeSpan.FromSeconds(10)), Is.True);
            var entered = new ManualResetEventSlim();
            var save = Task.Run(() =>
            {
                dbe.MMF.EnterDirectSave();
                entered.Set();
                dbe.MMF.ExitDirectSave();
            });

            Assert.That(entered.Wait(50), Is.False, "the save went ahead of the run's writes");
            release.Set();
            Task.WaitAll(run, save);
        }
        finally
        {
            release.Set();
            dbe.MMF.PageWriteInterceptor = null;
        }
    }

    /// <summary>A run finding a direct save in flight stands aside: the save may be writing newer bytes. Its pages stay owed, at the back.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-02")]
    public void ARun_StandsAsideForADirectSaveInFlight()
    {
        using var dbe = OpenWithDirtyPages(10, out var pages);
        using var pool = new StagingBufferPool(MemoryAllocator, AllocationResource, StagingBufferPool.MinCapacity);
        var input = pages.Order().ToArray();

        dbe.MMF.EnterDirectSave();
        dbe.MMF.WritePagesForCheckpoint(pages, pool, out var written);
        dbe.MMF.ExitDirectSave();

        Assert.That(written, Is.Zero);
        Assert.That(pages.Order(), Is.EqualTo(input), "every page still in the array");
        dbe.MMF.WritePagesForCheckpoint(pages, pool, out written);
        Assert.That(written, Is.EqualTo(input.Length), "written once the save ended");
    }

    /// <summary>A pass longer than a run, the barrier refusing the second: the first run is written, and the rest stays at the back, owed, none lost.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-02")]
    public void ABarrierThatRefuses_LeavesItsRunOwed()
    {
        using var dbe = OpenWithDirtyPages(30_000, out var pages);
        Assert.That(pages.Length, Is.GreaterThan(PagedMMF.CheckpointCaptureRunPages), "premise: more than one run");
        using var pool = new StagingBufferPool(MemoryAllocator, AllocationResource, PagedMMF.CheckpointCaptureRunPages * 4);
        var input = pages.Order().ToArray();
        var barriers = 0;

        dbe.MMF.WritePagesForCheckpoint(pages, pool, out var written, default, CheckpointWriteFilter.All, () => ++barriers == 1);

        Assert.That(written, Is.EqualTo(PagedMMF.CheckpointCaptureRunPages));
        Assert.That(pages.Order(), Is.EqualTo(input), "written at the front and skipped at the back, every page once");
    }

    /// <summary>A direct save writes live pages, so it flushes the WAL first, as a checkpoint copy does: the #1516 shape through <c>WriteTickFence</c>'s
    /// own save.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-02")]
    public void ADirectSave_FlushesTheWalBeforeItWrites()
    {
        using var dbe = OpenWithDirtyPages(10, out var pages);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");
        _wal.Hold();
        var a = new CompA(2, 2, 2);
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred))
        {
            using var tx = uow.CreateTransaction();
            tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tx.Commit();
        }

        var lsn = dbe.WalManager.LastPublishedLsn;
        var writesBeforeDurable = 0;
        var writes = 0;
        dbe.MMF.PageWriteInterceptor = _ =>
        {
            Interlocked.Increment(ref writes);
            if (dbe.WalManager.DurableLsn < lsn)
            {
                Interlocked.Increment(ref writesBeforeDurable);
            }
        };
        try
        {
            var dirty = dbe.MMF.CollectDirtyMemPageIndices();
            var save = Task.Run(() => dbe.MMF.SavePages([.. dirty]).GetAwaiter().GetResult());
            Assert.That(_wal.WriterHeld.Wait(TimeSpan.FromSeconds(10)), Is.True, "premise: the WAL writer holds the commit back");
            Assert.That(save.Wait(50), Is.False, "the save went ahead of the WAL");
            _wal.Release();
            save.GetAwaiter().GetResult();
            Assert.That(Volatile.Read(ref writesBeforeDurable), Is.Zero);
        }
        finally
        {
            _wal.Release();
            dbe.MMF.PageWriteInterceptor = null;
        }
    }
}
