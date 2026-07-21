using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Covers the per-kind suppression deny-list on <see cref="TyphonEvent"/>. Verifies the default seeded state, the runtime mutation API
/// (<see cref="TyphonEvent.SuppressKind"/> / <see cref="TyphonEvent.UnsuppressKind"/>), and the hot-path short-circuit that returns
/// <c>default(T)</c> from suppressed <c>Begin*</c> factories.
/// </summary>
/// <remarks>
/// <b>Why [NonParallelizable]:</b> the suppression array is static global state on <see cref="TyphonEvent"/>. Running this fixture in
/// parallel with any other profiler test could cause cross-test contamination — e.g., this fixture suppresses <c>BTreeInsert</c> briefly,
/// and a concurrent <c>FileExporterIntegrationTests</c> run would see zero BTreeInsert records during that window. Serializing the fixture
/// is the simplest correct answer; the tests themselves run in a few ms total.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class TyphonEventKindSuppressionTests
{
    /// <summary>
    /// Snapshot of the deny-list state we restore in <see cref="TearDown"/>. Covers every kind each test might have touched so the fixture
    /// leaves the profiler in the shipped default when it exits.
    /// </summary>
    private static readonly TraceEventKind[] TrackedKinds =
    [
        TraceEventKind.PageCacheFetch,
        TraceEventKind.PageCacheDiskRead,
        TraceEventKind.PageCacheDiskWrite,
        TraceEventKind.PageCacheAllocatePage,
        TraceEventKind.PageCacheFlush,
        TraceEventKind.PageEvicted,
        TraceEventKind.PageCacheDiskReadCompleted,
        TraceEventKind.PageCacheDiskWriteCompleted,
        TraceEventKind.PageCacheFlushCompleted,
        TraceEventKind.PageCacheBackpressure,
        TraceEventKind.BTreeInsert,
        TraceEventKind.TransactionCommit,
    ];

    [TearDown]
    public void TearDown()
    {
        // Restore the shipped defaults after the 2026-04-30 re-tier: only PageCacheFetch is in the page-cache deny-list; the 9 other
        // page-cache kinds are open and gated solely by their JSON category. Diagnostic-grade page-cache events (Flush/DiskWrite/Evicted
        // and friends) are therefore visible whenever Storage:PageCache:Enabled = true in the config.
        TyphonEvent.SuppressKind(TraceEventKind.PageCacheFetch);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheDiskRead);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheDiskWrite);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheAllocatePage);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheFlush);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageEvicted);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheDiskReadCompleted);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheDiskWriteCompleted);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheFlushCompleted);
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheBackpressure);
        TyphonEvent.UnsuppressKind(TraceEventKind.BTreeInsert);
        TyphonEvent.UnsuppressKind(TraceEventKind.TransactionCommit);
    }

    [Test]
    public void DefaultState_OnlyExtremeKindsSuppressed_OthersOpen()
    {
        Assert.Multiple(() =>
        {
            // PageCacheFetch is the only page-cache kind on the deny-list — it fires on every ChunkAccessor.GetPage in hot loops (millions/sec).
            // The other 9 page-cache kinds fire at I/O frequency (orders of magnitude less) and are gated by Storage:PageCache:Enabled in JSON.
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheFetch), Is.True, "PageCacheFetch default-suppressed (truly extreme)");

            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskRead), Is.False, "PageCacheDiskRead reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskWrite), Is.False, "PageCacheDiskWrite reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheAllocatePage), Is.False, "PageCacheAllocatePage reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheFlush), Is.False, "PageCacheFlush reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageEvicted), Is.False, "PageEvicted reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskReadCompleted), Is.False, "PageCacheDiskReadCompleted reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskWriteCompleted), Is.False, "PageCacheDiskWriteCompleted reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheFlushCompleted), Is.False, "PageCacheFlushCompleted reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheBackpressure), Is.False, "PageCacheBackpressure reachable from JSON");

            // Truly extreme leaves outside the page-cache family stay deny-listed.
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DataMvccChainWalk), Is.True, "DataMvccChainWalk default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DataIndexBTreeSearch), Is.True, "DataIndexBTreeSearch default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DataIndexBTreeNodeCow), Is.True, "DataIndexBTreeNodeCow default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.QueryExecuteIterate), Is.True, "QueryExecuteIterate default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.QueryExecuteFilter), Is.True, "QueryExecuteFilter default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.QueryExecutePagination), Is.True, "QueryExecutePagination default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.EcsQueryMaskAnd), Is.True, "EcsQueryMaskAnd default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.EcsViewProcessEntry), Is.True, "EcsViewProcessEntry default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.EcsViewProcessEntryOr), Is.True, "EcsViewProcessEntryOr default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DurabilityWalFrame), Is.True, "DurabilityWalFrame default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.RuntimeSubscriptionSubscriber), Is.True, "RuntimeSubscriptionSubscriber default-suppressed");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.RuntimeSubscriptionDeltaSerialize), Is.True, "RuntimeSubscriptionDeltaSerialize default-suppressed");

            // UoW state/deadline came OFF the deny-list — these are exactly the events an operator wants when diagnosing slow UoW.Flush.
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DurabilityUowState), Is.False, "DurabilityUowState reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DurabilityUowDeadline), Is.False, "DurabilityUowDeadline reachable from JSON");
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.DurabilityRecoveryRecord), Is.False, "DurabilityRecoveryRecord reachable from JSON (startup-only anyway)");

            // Everything else is open by default.
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeInsert), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeDelete), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.TransactionCommit), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.EcsSpawn), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.ClusterMigration), Is.False);
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.SchedulerChunk), Is.False);
        });
    }

    [Test]
    public void SuppressedPageCacheFetch_BeginFactory_ReturnsDefault()
    {
        // Default state: PageCacheFetch is suppressed. The Begin* factory must short-circuit before Stopwatch / slot acquisition and
        // return default(T), which has SpanId == 0.
        using var scope = TyphonEvent.BeginPageCacheFetch(filePageIndex: 42);
        Assert.That(scope.Header.SpanId, Is.EqualTo(0UL), "Suppressed kind must return default with SpanId=0 — Dispose then no-ops");
    }

    [Test]
    public void UnsuppressedPageCacheFetch_BeginFactory_ReturnsRealSpan()
    {
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheFetch);
        try
        {
            // ProfilerActive is true in the test project's typhon.telemetry.json, and the slot registry has room for one more thread, so
            // the Begin factory should produce a real span with a non-zero SpanId.
            using var scope = TyphonEvent.BeginPageCacheFetch(filePageIndex: 42);
            Assert.That(scope.Header.SpanId, Is.Not.EqualTo(0UL), "Unsuppressed kind must produce a real span");
            Assert.That(scope.FilePageIndex, Is.EqualTo(42));
        }
        finally
        {
            TyphonEvent.SuppressKind(TraceEventKind.PageCacheFetch);
        }
    }

    [Test]
    public void SuppressKind_DeniesArbitraryKindAtRuntime()
    {
        // BTreeInsert is open by default; suppressing it at runtime should short-circuit its Begin factory immediately.
        TyphonEvent.SuppressKind(TraceEventKind.BTreeInsert);
        try
        {
            Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeInsert), Is.True);

            using var scope = TyphonEvent.BeginBTreeInsert();
            Assert.That(scope.Header.SpanId, Is.EqualTo(0UL), "Runtime-suppressed kind must skip emission");
        }
        finally
        {
            TyphonEvent.UnsuppressKind(TraceEventKind.BTreeInsert);
        }

        // Unsuppressing restores the default emission path.
        Assert.That(TyphonEvent.IsKindSuppressed(TraceEventKind.BTreeInsert), Is.False);
        using var openScope = TyphonEvent.BeginBTreeInsert();
        Assert.That(openScope.Header.SpanId, Is.Not.EqualTo(0UL), "Kind open again after UnsuppressKind");
    }

    [Test]
    public void SuppressKind_AcceptsEveryDefinedKind()
    {
        // Flip every defined kind on and off to prove the array indexing covers the full enum range (0..60 plus 200). If this ever throws
        // IndexOutOfRangeException, the deny-list array was sized smaller than the largest enum value.
        TraceEventKind[] allKinds =
        [
            TraceEventKind.TickStart, TraceEventKind.TickEnd,
            TraceEventKind.PhaseStart, TraceEventKind.PhaseEnd,
            TraceEventKind.SystemReady, TraceEventKind.SystemSkipped,
            TraceEventKind.Instant,
            TraceEventKind.SchedulerChunk,
            TraceEventKind.TransactionCommit, TraceEventKind.TransactionRollback, TraceEventKind.TransactionCommitComponent,
            TraceEventKind.EcsSpawn, TraceEventKind.EcsDestroy,
            TraceEventKind.EcsQueryExecute, TraceEventKind.EcsQueryCount, TraceEventKind.EcsQueryAny,
            TraceEventKind.EcsViewRefresh,
            TraceEventKind.BTreeInsert, TraceEventKind.BTreeDelete,
            TraceEventKind.BTreeNodeSplit, TraceEventKind.BTreeNodeMerge,
            TraceEventKind.PageCacheFetch, TraceEventKind.PageCacheDiskRead, TraceEventKind.PageCacheDiskWrite,
            TraceEventKind.PageCacheAllocatePage, TraceEventKind.PageCacheFlush,
            TraceEventKind.PageEvicted,
            TraceEventKind.PageCacheDiskReadCompleted, TraceEventKind.PageCacheDiskWriteCompleted,
            TraceEventKind.PageCacheFlushCompleted,
            TraceEventKind.PageCacheBackpressure,
            TraceEventKind.ClusterMigration,
            TraceEventKind.TransactionPersist,
            TraceEventKind.WalFlush, TraceEventKind.WalSegmentRotate, TraceEventKind.WalWait,
            TraceEventKind.CheckpointCycle, TraceEventKind.CheckpointCollect, TraceEventKind.CheckpointWrite,
            TraceEventKind.CheckpointFsync, TraceEventKind.CheckpointTransition, TraceEventKind.CheckpointRecycle,
            TraceEventKind.StatisticsRebuild,
            TraceEventKind.NamedSpan,
        ];

        foreach (var kind in allKinds)
        {
            var original = TyphonEvent.IsKindSuppressed(kind);
            TyphonEvent.SuppressKind(kind);
            Assert.That(TyphonEvent.IsKindSuppressed(kind), Is.True, $"SuppressKind({kind})");
            TyphonEvent.UnsuppressKind(kind);
            Assert.That(TyphonEvent.IsKindSuppressed(kind), Is.False, $"UnsuppressKind({kind})");

            // Restore original state
            if (original)
            {
                TyphonEvent.SuppressKind(kind);
            }
        }
    }
}
