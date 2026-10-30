using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// The live cache's retention cap (#822): a session stops keeping ticks at a size limit or a free-disk floor, at a tick boundary, stays connected, and can still
/// be saved. A failed cache write ends the same way, and is neither a lost connection nor an unhandled exception on a timer.
/// </summary>
[TestFixture]
public sealed class LiveCacheCapTests
{
    private static CancellationToken Timeout15s => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private string _capturesDir;

    [SetUp]
    public void CreateCapturesDirectory()
    {
        _capturesDir = Path.Combine(Path.GetTempPath(), "typhon-live-cache-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_capturesDir);
    }

    [TearDown]
    public void DeleteCapturesDirectory()
    {
        try { Directory.Delete(_capturesDir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private Task<CaptureHarness> StartAsync(CaptureMode mode, LiveCacheLimits limits) => CaptureHarness.StartAsync(mode, Timeout15s, _capturesDir, limits);

    /// <summary>Everything past the first flushed chunk is over a one-byte limit.</summary>
    private static readonly LiveCacheLimits OneByte = new(1, 0);

    /// <summary>
    /// Sends two ticks in one block and waits for the cache to take them, so the next <c>TickStart</c> finds the session over its limit. One block, because the
    /// flush timer runs free: between two blocks it could cap the session on the second tick.
    /// </summary>
    private static async Task FillTheCacheAsync(CaptureHarness h)
    {
        var block = new List<byte>(CaptureHarness.BuildTick(0, engineTick: 100, detailRecords: 4));
        block.AddRange(CaptureHarness.BuildTick(1, engineTick: 101, detailRecords: 4));
        await h.SendBlockAsync(block.ToArray(), Timeout15s);
        await h.FirstChunk.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Saves, reopens the replay as the Workbench would, and returns the ticks a reader finds in it.</summary>
    private async Task<int> SaveAndReopenAsync(CaptureHarness h, string name)
    {
        var target = Path.Combine(_capturesDir, name);
        Assert.That(await h.Runtime.SaveSessionAsync(target, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)), Is.GreaterThan(0));

        using var replay = TraceSessionRuntime.Start(target, NullLogger.Instance);
        var metadata = await replay.MetadataReady.WaitAsync(TimeSpan.FromSeconds(10));
        return metadata.TickSummaries.Count();
    }

    [Test]
    public async Task AtTheSizeCap_RetentionStopsAtATickBoundary_AndTheSessionStaysConnected()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, OneByte);
        h.Runtime.Arm(100);
        await FillTheCacheAsync(h);
        var recorded = h.Runtime.CaptureState.RecordedTicks;

        await h.SendTickAsync(2, engineTick: 102, detailRecords: 4, ct: Timeout15s);
        await h.WaitForSummariesAsync(2);

        var capped = h.Runtime.CaptureState;
        Assert.That(capped.State, Is.EqualTo(nameof(CaptureRunState.Capped)));
        Assert.That(capped.CapReason, Is.EqualTo(AttachSessionRuntime.CapSize));
        Assert.That(capped.Remaining, Is.Zero, "the open window is cut at the boundary");
        Assert.That(capped.CapTick, Is.EqualTo(101), "the last tick kept");
        Assert.That(capped.RecordedTicks, Is.EqualTo(recorded), "the tick that met the cap is not recorded");
        Assert.That(h.Summaries, Has.Count.EqualTo(2), "the last kept tick reaches the client, whole, and no other does");

        await h.SendTickAsync(3, engineTick: 103, detailRecords: 4, ct: Timeout15s);

        var later = h.Runtime.CaptureState;
        Assert.That(later.BytesRetained, Is.EqualTo(capped.BytesRetained), "nothing is retained past the cap, exempt kinds included");
        Assert.That(later.BytesReceived, Is.GreaterThan(capped.BytesReceived), "the stream is still being read");
        Assert.That(h.Runtime.ConnectionStatus, Is.EqualTo("connected"));
    }

    [Test]
    public async Task BelowTheFreeDiskFloor_RetentionStops()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, new LiveCacheLimits(long.MaxValue, long.MaxValue));
        h.Runtime.Arm(100);
        await FillTheCacheAsync(h);

        await h.SendTickAsync(2, engineTick: 102, detailRecords: 4, ct: Timeout15s);

        Assert.That(h.Runtime.CaptureState.CapReason, Is.EqualTo(AttachSessionRuntime.CapDiskLow));
    }

    [Test]
    public async Task CaptureEverything_ReachesTheCapTheSameWay()
    {
        await using var h = await StartAsync(CaptureMode.Everything, OneByte);
        await FillTheCacheAsync(h);

        await h.SendTickAsync(2, engineTick: 102, detailRecords: 4, ct: Timeout15s);

        var state = h.Runtime.CaptureState;
        Assert.That(state.State, Is.EqualTo(nameof(CaptureRunState.Capped)));
        Assert.That(state.Mode, Is.EqualTo(nameof(CaptureMode.Everything)));
    }

    [Test]
    public async Task ArmingPastTheCap_IsRefused_AndStoppingIsANoOp()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, OneByte);
        h.Runtime.Arm(100);
        await FillTheCacheAsync(h);
        await h.SendTickAsync(2, engineTick: 102, detailRecords: 4, ct: Timeout15s);

        Assert.That(() => h.Runtime.Arm(5), Throws.InvalidOperationException.With.Message.Contains(AttachSessionRuntime.CapSize));
        Assert.That(h.Runtime.Arm(0).State, Is.EqualTo(nameof(CaptureRunState.Capped)));
    }

    [Test]
    public async Task ACappedSession_CanStillBeSaved()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, OneByte);
        h.Runtime.Arm(100);
        await FillTheCacheAsync(h);
        await h.SendTickAsync(2, engineTick: 102, detailRecords: 4, ct: Timeout15s);
        Assert.That(h.Runtime.CaptureState.State, Is.EqualTo(nameof(CaptureRunState.Capped)));

        Assert.That(await SaveAndReopenAsync(h, "capped.typhon-replay"), Is.EqualTo(2), "the replay holds what the session kept");
    }

    [Test]
    public async Task AFailedCacheWriteOnTheReadPath_CapsTheSession_AndIsNotALostConnection()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, FailingFrom(1));
        h.Runtime.Arm(TraceFileCacheConstants.TickCap * 2);

        // One block: the whole of it is fed under the builder lock, so the flush at the tick cap is the read loop's and not the timer's.
        var block = new List<byte>();
        for (var i = 0; i <= TraceFileCacheConstants.TickCap; i++)
        {
            block.AddRange(CaptureHarness.BuildTick(i, engineTick: (uint)(100 + i), detailRecords: 1));
        }
        await h.SendBlockAsync(block.ToArray(), Timeout15s);

        var state = h.Runtime.CaptureState;
        Assert.That(state.CapReason, Is.EqualTo(AttachSessionRuntime.CapWriteFailed));
        Assert.That(state.CapTick, Is.Zero, "no chunk reached the manifest, whatever tick the walk had reached");
        Assert.That(state.BytesRetained, Is.Zero, "bytes a failed write lost were not retained");
        Assert.That(h.Runtime.ConnectionStatus, Is.EqualTo("connected"), "a full disk is not a lost connection: reconnecting would resume writing to it");

        await h.SendTickAsync(TraceFileCacheConstants.TickCap + 1, engineTick: 300, detailRecords: 1, ct: Timeout15s);
        Assert.That(h.Runtime.ConnectionStatus, Is.EqualTo("connected"));
    }

    [Test]
    public async Task AFailedCacheWriteOnTheFlushTimer_CapsTheSession()
    {
        // An IOException on this timer used to go unhandled, which ends the test host and not merely the test.
        await using var h = await StartAsync(CaptureMode.CherryPick, FailingFrom(1));
        h.Runtime.Arm(100);

        await h.SendTickAsync(0, engineTick: 100, detailRecords: 4, ct: Timeout15s);

        var state = await h.WaitForCaptureStateAsync(s => s.CapReason != null);
        Assert.That(state.CapReason, Is.EqualTo(AttachSessionRuntime.CapWriteFailed));
        Assert.That(h.Runtime.ConnectionStatus, Is.EqualTo("connected"));
    }

    [Test]
    public async Task AfterAFailedWrite_WhatReachedTheManifestCanStillBeSaved()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, FailingFrom(2));
        h.Runtime.Arm(100);
        await h.SendTickAsync(0, engineTick: 100, detailRecords: 4, ct: Timeout15s);
        await h.FirstChunk.WaitAsync(TimeSpan.FromSeconds(10));
        await h.SendTickAsync(1, engineTick: 101, detailRecords: 4, ct: Timeout15s);
        var state = await h.WaitForCaptureStateAsync(s => s.CapReason != null);
        Assert.That(state.CapReason, Is.EqualTo(AttachSessionRuntime.CapWriteFailed));

        Assert.That(await SaveAndReopenAsync(h, "after-failure.typhon-replay"), Is.LessThanOrEqualTo(1), "no tick without its chunk");
    }

    [Test]
    public async Task AFirstWriteFailureAtSaveTime_StillSavesWhatTheManifestHolds()
    {
        await using var h = await StartAsync(CaptureMode.CherryPick, FailingFrom(2));
        h.Runtime.Arm(100);
        await h.SendTickAsync(0, engineTick: 100, detailRecords: 4, ct: Timeout15s);
        await h.FirstChunk.WaitAsync(TimeSpan.FromSeconds(10));

        // Into the builder and not the cache: the disk fills before the next flush, and the save is the first to find out.
        await h.SendTickAsync(1, engineTick: 101, detailRecords: 4, ct: Timeout15s);

        Assert.That(await SaveAndReopenAsync(h, "at-save.typhon-replay"), Is.LessThanOrEqualTo(1));
        Assert.That(h.Runtime.CaptureState.CapReason, Is.EqualTo(AttachSessionRuntime.CapWriteFailed));
    }

    /// <summary>Limits that never cap, over a sink whose appends fail from the <paramref name="failingAppend"/>th one on.</summary>
    private static LiveCacheLimits FailingFrom(int failingAppend) =>
        new LiveCacheLimits(long.MaxValue, 0) { SinkDecorator = sink => new FailingSink(sink, failingAppend) };

    private sealed class FailingSink(ICacheChunkSink inner, int failingAppend) : ICacheChunkSink
    {
        private int _appends;

        public bool SupportsTrailer => inner.SupportsTrailer;

        public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords) =>
            Interlocked.Increment(ref _appends) >= failingAppend ? throw new IOException("There is not enough space on the disk.") : inner.AppendChunk(uncompressedRecords);

        public void WriteTrailer(IReadOnlyList<TickSummary> tickSummaries, in GlobalMetricsFixed globalMetrics, IReadOnlyList<SystemAggregateDuration> systemAggregates,
            IReadOnlyList<ChunkManifestEntry> chunkManifest, IReadOnlyDictionary<int, string> spanNames, ReadOnlySpan<byte> sourceMetadataBytes,
            in CacheHeader headerTemplate, IReadOnlyList<SystemTickSummary> systemTickSummaries, IReadOnlyList<QueueTickSummary> queueTickSummaries,
            IReadOnlyList<PostTickSummary> postTickSummaries, IReadOnlyDictionary<ushort, string> queueIdToName,
            IReadOnlyList<SystemArchetypeTouchSummary> systemArchetypeTouches, IReadOnlyList<EntityLifecycleRun> entityLifecycleRuns) =>
            throw new NotSupportedException("a live sink has no trailer");

        public void Dispose() { }
    }
}
