using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Verifies the DirtyCounter (DC) conservation property of <see cref="ChangeSet"/> after the #385 fix that replaced the racing
/// <c>DecrementDirtyToMin(p, 1)</c> cap with exact per-page <see cref="PagedMMF.DecrementDirty"/> calls. Every
/// <see cref="ChangeSet.AddByMemPageIndex"/> + <see cref="ChangeSet.RegisterReDirty"/> issues exactly one IncrementDirty;
/// <see cref="ChangeSet.ReleaseExcessDirtyMarks"/> drains <c>(count - 1)</c> and <see cref="ChangeSet.Reset"/> drains
/// <c>count</c> per page. The final DC after a checkpoint-style ack should be exactly 0 — same as the pre-fix outcome under
/// sequential composition, but now race-free.
/// </summary>
[TestFixture]
class ChangeSetConservationTests
{
    private IServiceProvider _serviceProvider;
    private IServiceScope _scope;
    private PagedMMF _pmmf;
    private EpochManager _em;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"changesetconserv_{Guid.NewGuid():N}";
                o.DatabaseCacheSize = 128 * PagedMMF.PageSize;
                o.PagesDebugPattern = false;
                o.TestMode = true;
            });
        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();

        _em = _serviceProvider.GetRequiredService<EpochManager>();
        _scope = _serviceProvider.CreateScope();
        _pmmf = _scope.ServiceProvider.GetRequiredService<PagedMMF>();
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
    }

    /// <summary>Fetch a page into the cache + return its memPageIndex. Each test passes a unique file-page id.</summary>
    private int Fetch(int filePageIndex)
    {
        _pmmf.RequestPageEpoch(filePageIndex, _em.GlobalEpoch, out var memIdx);
        return memIdx;
    }

    private int Dc(int memIdx) => _pmmf.GetPageInfoForDiagnostic(memIdx).DirtyCounter;

    [Test]
    public void Add_SetsDirtyCounterTo1()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(1);
        Assert.That(cs.AddByMemPageIndex(m), Is.True);
        Assert.That(Dc(m), Is.EqualTo(1), "AddByMemPageIndex must IncrementDirty exactly once on first registration");
    }

    [Test]
    public void Add_ThenReleaseExcessDirtyMarks_LeavesDC_1()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(2);
        cs.AddByMemPageIndex(m);
        cs.ReleaseExcessDirtyMarks();
        Assert.That(Dc(m), Is.EqualTo(1),
            "After one mark + Release: count was 1, excess (count-1)=0 decrements issued, DC stays at 1");
    }

    [Test]
    public void Add_ThenReset_LeavesDC_0()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(3);
        cs.AddByMemPageIndex(m);
        cs.Reset();
        Assert.That(Dc(m), Is.EqualTo(0),
            "Reset must fully undo every mark — DC returns to its pre-UoW baseline (rollback semantics)");
    }

    [Test]
    public void Add_PlusFiveReDirty_LeavesDC_6()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(4);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        Assert.That(Dc(m), Is.EqualTo(6), "Add (1) + 5 × RegisterReDirty (+5) = DC 6 (each issues one IncrementDirty)");
    }

    [Test]
    public void Add_PlusFiveReDirty_ThenReleaseExcessDirtyMarks_LeavesDC_1()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(5);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        cs.ReleaseExcessDirtyMarks();
        Assert.That(Dc(m), Is.EqualTo(1),
            "Release must drain exactly (count - 1) = 5 IncrementDirty calls, leaving 1 mark for the next checkpoint ack");
    }

    [Test]
    public void Add_PlusFiveReDirty_ThenReleaseAndCheckpointAck_LeavesDC_0()
    {
        // Models the full WAL-mode lifecycle: a UoW touches a page N+1 times, commits (ReleaseExcessDirtyMarks), then the
        // background checkpoint writes the page and acks (one DecrementDirty). Final DC must be 0 — the page is clean and
        // evictable.
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(6);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        cs.ReleaseExcessDirtyMarks();    // UoW commit
        _pmmf.DecrementDirty(m);         // Checkpoint write ack (single decrement)
        Assert.That(Dc(m), Is.EqualTo(0),
            "Full UoW + checkpoint composition: 6 increments + 5 release decrements + 1 checkpoint decrement = 0");
    }

    [Test]
    public void Add_PlusFiveReDirty_ThenReset_LeavesDC_0()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(7);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        cs.Reset();
        Assert.That(Dc(m), Is.EqualTo(0),
            "Rollback must decrement count times, fully reversing every increment regardless of mark depth");
    }

    [Test]
    public void Add_TwiceForSamePage_SecondCallReturnsFalse_DC_StaysAt_1()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(8);
        Assert.That(cs.AddByMemPageIndex(m), Is.True, "First add returns true (fresh registration)");
        Assert.That(cs.AddByMemPageIndex(m), Is.False, "Second add returns false (page already tracked)");
        Assert.That(Dc(m), Is.EqualTo(1), "Repeated Add calls must NOT additionally increment DC");
    }

    /// <summary>
    /// The whole point of the #385 fix: ReleaseExcessDirtyMarks and DecrementDirty must commute under concurrency. Hammer the
    /// race between many threads to confirm DC never goes below 0 and ends at the expected value.
    /// </summary>
    [Test]
    [Repeat(10)]
    public void Release_AndConcurrentCheckpointAck_NeverOverDecrements()
    {
        using var _ep = EpochGuard.Enter(_em);
        const int pageCount = 32;
        const int marksPerPage = 6;
        var changeSets = new ChangeSet[pageCount];
        var memIndices = new int[pageCount];

        for (var i = 0; i < pageCount; i++)
        {
            memIndices[i] = Fetch(100 + i);
            var cs = _pmmf.CreateChangeSet();
            cs.AddByMemPageIndex(memIndices[i]);
            for (var j = 0; j < marksPerPage - 1; j++) cs.RegisterReDirty(memIndices[i]);
            changeSets[i] = cs;
        }

        // Each page is at DC = marksPerPage (6). Concurrently fire ReleaseExcessDirtyMarks (which decrements 5 times) AND
        // a single DecrementDirty (modelling checkpoint ack). Final DC must be exactly 0; never negative under any
        // interleaving.
        Parallel.For(0, pageCount, i =>
        {
            using var barrier = new ManualResetEventSlim(false);
            var t1 = new Thread(() => { barrier.Wait(); changeSets[i].ReleaseExcessDirtyMarks(); });
            var t2 = new Thread(() => { barrier.Wait(); _pmmf.DecrementDirty(memIndices[i]); });
            t1.Start(); t2.Start();
            Thread.Sleep(1);
            barrier.Set();
            t1.Join(); t2.Join();
        });

        var bad = new ConcurrentBag<(int memIdx, int dc)>();
        for (var i = 0; i < pageCount; i++)
        {
            var d = Dc(memIndices[i]);
            if (d != 0) bad.Add((memIndices[i], d));
        }
        Assert.That(bad, Is.Empty,
            "Concurrent Release + Decrement must converge to DC=0 for every page. Anomalies: " +
            string.Join(", ", bad.Select(b => $"page {b.memIdx}: DC={b.dc}")));
    }
}
