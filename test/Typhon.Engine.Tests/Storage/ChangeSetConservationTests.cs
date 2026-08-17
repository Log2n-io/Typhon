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
/// The mark arithmetic of <see cref="ChangeSet"/>, at the primitive level: a change set takes marks and releases exactly
/// those marks, and nobody else touches them.
/// </summary>
/// <remarks>
/// <para>
/// This fixture used to assert the opposite of several of these — that a release leaves one mark behind for the
/// checkpoint to consume, and that a checkpoint-style <c>DecrementDirty</c> composes with it. That contract could not be
/// made to balance: marks arrive once per unit of work, the checkpoint acks once per cycle, and no fixed arithmetic
/// reconciles the two (#824). The counter is now owner-scoped and the writeback obligation lives in the page's
/// generation pair, so the arithmetic here is simply N in, N out.
/// </para>
/// <para>
/// The concurrency arm below changed shape for the same reason: two owners releasing their own marks at once is the race
/// worth pinning. A thread that decrements marks it never took is no longer a checkpoint — it is a bug, and the DEBUG
/// conservation assert in <c>DecrementDirtyByDelta</c> now says so at the call site.
/// </para>
/// </remarks>
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
        Assert.That(Dc(m), Is.EqualTo(1), "AddByMemPageIndex must take exactly one mark on first registration");
    }

    [Test]
    public void Add_ThenReleaseDirtyMarks_LeavesDC_0()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(2);
        cs.AddByMemPageIndex(m);
        cs.ReleaseDirtyMarks();
        Assert.That(Dc(m), Is.Zero, "one mark taken, one released — the counter is back to baseline");
        Assert.That(_pmmf.HasWritebackDebt(m), Is.True, "and the page still owes a write, which is what keeps it resident");
    }

    [Test]
    public void Add_ThenReset_LeavesDC_0()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(3);
        cs.AddByMemPageIndex(m);
        cs.Reset();
        Assert.That(Dc(m), Is.Zero, "rollback returns every mark it took");
    }

    [Test]
    public void Add_PlusFiveReDirty_LeavesDC_6()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(4);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        Assert.That(Dc(m), Is.EqualTo(6), "Add (1) + 5 × RegisterReDirty (+5) = 6 marks held");
    }

    [Test]
    public void Add_PlusFiveReDirty_ThenReleaseDirtyMarks_LeavesDC_0()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(5);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        cs.ReleaseDirtyMarks();
        Assert.That(Dc(m), Is.Zero, "release drains exactly the 6 marks this change set took, not 5 of them");
    }

    /// <summary>
    /// The full unit-of-work lifecycle: mark, re-dirty, release. The page is clean of marks and still owed — and it is
    /// the write, not the release, that settles it.
    /// </summary>
    [Test]
    public void Release_LeavesNoMarks_AndTheWriteSettlesTheDebt()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(6);
        cs.AddByMemPageIndex(m);
        for (var i = 0; i < 5; i++) cs.RegisterReDirty(m);
        cs.ReleaseDirtyMarks();

        Assert.That(Dc(m), Is.Zero);
        Assert.That(_pmmf.HasWritebackDebt(m), Is.True, "nothing has written the page yet");

        _pmmf.MarkCaptured(m, _pmmf.WritebackGenOf(m));   // what a checkpoint publishes after its fsync

        Assert.That(_pmmf.HasWritebackDebt(m), Is.False, "written and fsynced — now the page may be evicted");
        Assert.That(Dc(m), Is.Zero, "and the write never touched the mark counter, which is not its to touch");
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
        Assert.That(Dc(m), Is.Zero, "rollback reverses every mark regardless of depth");
    }

    /// <summary>
    /// A repeated <c>Add</c> takes no second mark — but it DOES record the modification, because the caller has just
    /// written to the page again.
    /// </summary>
    /// <remarks>
    /// The second half is the half that bites. Deduplicating the mark is a bookkeeping convenience; deduplicating the
    /// writeback obligation is a lost write, because a checkpoint may have captured and settled this page between the two
    /// calls, and the second modification would then never reach disk.
    /// </remarks>
    [Test]
    public void Add_TwiceForSamePage_TakesOneMark_ButRecordsBothModifications()
    {
        using var _ep = EpochGuard.Enter(_em);
        var cs = _pmmf.CreateChangeSet();
        var m = Fetch(8);

        Assert.That(cs.AddByMemPageIndex(m), Is.True, "first add is a fresh registration");
        _pmmf.MarkCaptured(m, _pmmf.WritebackGenOf(m));   // a checkpoint settles the page between the two adds
        Assert.That(_pmmf.HasWritebackDebt(m), Is.False, "precondition: settled");

        Assert.That(cs.AddByMemPageIndex(m), Is.False, "second add finds the page already tracked");

        Assert.That(Dc(m), Is.EqualTo(1), "repeated Add must not take a second mark");
        Assert.That(_pmmf.HasWritebackDebt(m), Is.True, "but the second modification must still be owed, or it is lost");
    }

    /// <summary>
    /// Many owners releasing their own marks on the same page at once converge to zero, and never below it.
    /// </summary>
    /// <remarks>
    /// This replaces an arm that raced <c>ReleaseDirtyMarks</c> against a bare <c>DecrementDirty</c> standing in for the
    /// checkpoint. That composition is no longer legal — a thread releasing marks it never took is over-releasing by
    /// definition, and the DEBUG assert in the primitive now fails the test at the call site rather than letting the
    /// counter absorb it. The race that remains worth pinning is between genuine co-owners.
    /// </remarks>
    [Test]
    [Repeat(10)]
    public void ConcurrentOwners_EachReleasingTheirOwnMarks_ConvergeToZero()
    {
        using var _ep = EpochGuard.Enter(_em);
        const int pageCount = 32;
        const int marksPerPage = 6;
        const int ownersPerPage = 2;
        var memIndices = new int[pageCount];
        var sets = new ChangeSet[pageCount, ownersPerPage];

        for (var i = 0; i < pageCount; i++)
        {
            memIndices[i] = Fetch(100 + i);
            for (var o = 0; o < ownersPerPage; o++)
            {
                var cs = _pmmf.CreateChangeSet();
                cs.AddByMemPageIndex(memIndices[i]);
                for (var j = 0; j < marksPerPage - 1; j++) cs.RegisterReDirty(memIndices[i]);
                sets[i, o] = cs;
            }
        }

        Parallel.For(0, pageCount, i =>
        {
            using var barrier = new ManualResetEventSlim(false);
            var threads = new Thread[ownersPerPage];
            for (var o = 0; o < ownersPerPage; o++)
            {
                var cs = sets[i, o];
                threads[o] = new Thread(() => { barrier.Wait(); cs.ReleaseDirtyMarks(); });
                threads[o].Start();
            }

            barrier.Set();
            foreach (var t in threads) { t.Join(); }
        });

        var bad = new ConcurrentBag<(int memIdx, int dc)>();
        for (var i = 0; i < pageCount; i++)
        {
            var d = Dc(memIndices[i]);
            if (d != 0) bad.Add((memIndices[i], d));
        }

        Assert.That(bad, Is.Empty,
            "every owner released exactly what it took, so the counter must land on 0 under any interleaving. Anomalies: " +
            string.Join(", ", bad.Select(b => $"page {b.memIdx}: DC={b.dc}")));
    }
}
