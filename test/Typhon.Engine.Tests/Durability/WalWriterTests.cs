using NUnit.Framework;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Single-threaded unit tests for <see cref="WalWriter"/>.
/// Uses <see cref="InMemoryWalFileIO"/> for isolation from disk I/O.
/// </summary>
[TestFixture]
public class WalWriterTests : AllocatorTestBase
{
    private const int TestCapacity = 64 * 1024;

    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_writer_test_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    private (WalCommitBuffer buffer, WalWriter writer, WalSegmentManager segMgr) CreateWriterPipeline(
        int bufferCapacity = TestCapacity,
        int groupCommitMs = 5,
        bool useFUA = false,
        IWalFileIO fileIO = null)
    {
        fileIO ??= _fileIO;
        var buffer = new WalCommitBuffer(MemoryAllocator, AllocationResource, bufferCapacity);

        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = groupCommitMs,
            SegmentSize = 1024 * 1024, // 1MB for tests
            PreAllocateSegments = 1,
            StagingBufferSize = 8192, // 8KB staging buffer for tests
            UseFUA = useFUA,
        };

        var segMgr = new WalSegmentManager(fileIO, _walDir, options.SegmentSize, options.PreAllocateSegments, useFUA);
        segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var writer = new WalWriter(buffer, segMgr, fileIO, options, MemoryAllocator, AllocationResource);

        return (buffer, writer, segMgr);
    }

    #region Lifecycle

    [Test]
    public void Start_SetsIsRunning()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();
            SpinWait.SpinUntil(() => writer.IsRunning, 2000);

            Assert.That(writer.IsRunning, Is.True);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    public void Start_Idempotent()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();
            writer.Start(); // Should not throw or create second thread
            SpinWait.SpinUntil(() => writer.IsRunning, 2000);

            Assert.That(writer.IsRunning, Is.True);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Dispose_StopsThread()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        writer.Start();
        SpinWait.SpinUntil(() => writer.IsRunning, 2000);

        writer.Dispose(); // Join() inside blocks until the writer thread exits

        Assert.That(writer.IsRunning, Is.False);

        buffer.Dispose();
        segMgr.Dispose();
    }

    #endregion

    #region Basic Drain

    [Test]
    [CancelAfter(5000)]
    public void WriterDrainsPublishedFrames()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce a frame
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True);

            // Write some data
            claim.DataSpan.Fill(0xAA);
            buffer.Publish(ref claim);

            // Wait for writer to drain
            SpinWait.SpinUntil(() => writer.TotalBytesWritten > 0, 2000);

            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.TotalFlushes, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void DurableLsn_AdvancesAfterDrain()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce a frame with 1 record
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xBB);
            buffer.Publish(ref claim);

            // Wait for drain
            SpinWait.SpinUntil(() => writer.DurableLsn > 0, 2000);

            Assert.That(writer.DurableLsn, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void MultipleFrames_AllDrained()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce multiple frames
            for (int i = 0; i < 5; i++)
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
                var claim = buffer.TryClaim(128, 2, ref ctx);
                claim.DataSpan.Fill((byte)(i + 1));
                buffer.Publish(ref claim);
            }

            // 5 frames × 2 records each = 10 total records → LSNs 1-10
            SpinWait.SpinUntil(() => writer.DurableLsn >= 10, 2000);

            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(10));
            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region WaitForDurable

    [Test]
    [CancelAfter(5000)]
    public void WaitForDurable_AlreadyDurable_ReturnsImmediately()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce and wait for drain
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xCC);
            buffer.Publish(ref claim);

            SpinWait.SpinUntil(() => writer.DurableLsn >= 1, 2000);

            // Should return immediately since already durable
            var waitCtx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
            writer.WaitForDurable(1, ref waitCtx);

            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region Properties

    [Test]
    public void InitialState_PropertiesAreDefault()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            Assert.That(writer.DurableLsn, Is.EqualTo(0));
            Assert.That(writer.IsRunning, Is.False);
            Assert.That(writer.HasFatalError, Is.False);
            Assert.That(writer.TotalBytesWritten, Is.EqualTo(0));
            Assert.That(writer.TotalFlushes, Is.EqualTo(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region RequestFlush

    [Test]
    [CancelAfter(5000)]
    public void RequestFlush_TriggersFlush()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline(groupCommitMs: 10_000); // Very long interval to prove RequestFlush bypasses it
        try
        {
            writer.Start();

            // Produce a frame
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xDD);
            buffer.Publish(ref claim);

            // Wait for drain (writer thread picks up data from buffer)
            SpinWait.SpinUntil(() => writer.DurableLsn >= 1, 2000);

            // Request explicit flush — Signal() wakes the writer immediately
            writer.RequestFlush();
            SpinWait.SpinUntil(() => writer.TotalFlushes > 0, 2000);

            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.TotalFlushes, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region Shutdown drain (#580)

    /// <summary>
    /// WP-03 regression (#580): the shutdown drain must WRITE a final batch that exceeds <c>StagingBufferSize</c>, not silently discard it.
    /// </summary>
    /// <remarks>
    /// <c>DrainRemaining</c> used to run the staging-buffer copy under an <c>if (bytesToWrite &lt;= _stagingBufferSize)</c> with no <c>else</c>, while
    /// <c>CompleteDrain</c> + <c>AdvanceDurable</c> ran unconditionally underneath it. An oversized final batch — a large last commit, or a fat fence batch at
    /// Dispose — therefore reached neither <c>WriteAligned</c> nor disk, yet the durable watermark advanced over it. Recovery then starts after the watermark
    /// and never looks for the missing records: silent data loss on an *orderly* shutdown. Pre-fix this test sees <c>TotalBytesWritten == 0</c>.
    /// </remarks>
    [Test]
    [CancelAfter(5000)]
    public void DrainRemaining_BatchLargerThanStagingBuffer_IsWrittenNotDiscarded()
    {
        // StagingBufferSize is 8 KB here (see CreateWriterPipeline); claim comfortably past it while staying inside the commit-buffer half.
        const int OversizedFrame = 16 * 1024;

        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            // Deliberately do NOT Start() — the shutdown drain is the path under test, and the writer thread would otherwise consume the batch in WriterLoop
            // (which has always handled the oversized case correctly). This mirrors a frame published in the window before the loop's shutdown break.
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(OversizedFrame, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True, "precondition: the oversized frame must fit the commit buffer");
            claim.DataSpan.Fill(0xBE);
            buffer.Publish(ref claim);

            writer.DrainRemaining();

            Assert.Multiple(() =>
            {
                Assert.That(writer.TotalBytesWritten, Is.GreaterThanOrEqualTo(OversizedFrame),
                    "the oversized final batch must reach WriteAligned — 0 here is the #580 silent drop");
                Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(1),
                    "the durable watermark may only advance over bytes that were actually written");
            });
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>Companion to the above: a final batch that FITS the staging buffer still drains through the original path.</summary>
    [Test]
    [CancelAfter(5000)]
    public void DrainRemaining_BatchWithinStagingBuffer_IsWritten()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(256, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True);
            claim.DataSpan.Fill(0xAB);
            buffer.Publish(ref claim);

            writer.DrainRemaining();

            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region WaitForDurable wake-up (#1529)

    /// <summary>
    /// Several committers wait at once, each for a different LSN, while the durable watermark advances one LSN at a time. Every waiter must return as soon
    /// as its LSN is durable: the old <c>ManualResetEventSlim.Wait(1)</c> + <c>Reset()</c> loop let one waiter's <c>Reset</c> clear the signal another
    /// needed, and the 1 ms timeout then slept a whole 15.6 ms Windows timer quantum.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("WP-17")]
    [Category("Sensitive")] // wall-clock wake-latency bound — runs in the gate's serial quiet pass
    public void WaitForDurable_ConcurrentWaiters_EachWakesAsSoonAsItsLsnIsDurable()
    {
        const int waiterCount = 8;
        const int rounds = 100;
        const int lsnCount = waiterCount * rounds;
        var maxLatency = TimeSpan.FromMilliseconds(10);

        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            // Writer thread not started: the test is the only thing advancing the watermark (SeedDurableLsn → AdvanceDurable, the writer's own path).
            var advancedAt = new long[lsnCount + 1];
            var wokeAt = new long[lsnCount + 1];
            var armed = new int[lsnCount + 1];
            var failures = new Exception[waiterCount];
            var threads = new Thread[waiterCount];
            for (var w = 0; w < waiterCount; w++)
            {
                var waiter = w;
                threads[w] = new Thread(() =>
                {
                    try
                    {
                        for (var r = 0; r < rounds; r++)
                        {
                            var lsn = r * waiterCount + waiter + 1;
                            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
                            Volatile.Write(ref armed[lsn], 1);
                            writer.WaitForDurable(lsn, ref ctx);
                            wokeAt[lsn] = Stopwatch.GetTimestamp();
                        }
                    }
                    catch (Exception ex)
                    {
                        failures[waiter] = ex;
                    }
                }) { IsBackground = true };
                threads[w].Start();
            }

            for (var lsn = 1; lsn <= lsnCount; lsn++)
            {
                var spin = new SpinWait();
                while (Volatile.Read(ref armed[lsn]) == 0)
                {
                    spin.SpinOnce(-1);
                }

                // Let the waiter reach its blocking wait, and give the previous round's wakers time to loop back: the window the Reset raced in.
                var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20_000;
                while (Stopwatch.GetTimestamp() < until)
                {
                    Thread.SpinWait(20);
                }

                advancedAt[lsn] = Stopwatch.GetTimestamp();
                writer.SeedDurableLsn(lsn);
            }

            foreach (var t in threads)
            {
                Assert.That(t.Join(TimeSpan.FromSeconds(3)), Is.True, "a waiter never returned");
            }

            Assert.That(failures, Is.All.Null);
            var worst = TimeSpan.Zero;
            var worstLsn = 0;
            for (var lsn = 1; lsn <= lsnCount; lsn++)
            {
                var latency = Stopwatch.GetElapsedTime(advancedAt[lsn], wokeAt[lsn]);
                if (latency > worst)
                {
                    worst = latency;
                    worstLsn = lsn;
                }
            }

            Assert.That(worst, Is.LessThan(maxLatency), $"LSN {worstLsn} woke {worst.TotalMilliseconds:F2} ms after it became durable");
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>
    /// A waiter whose LSN never becomes durable gives up at its own deadline, not at the next backstop slice: the wait slice is clamped to the deadline.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("WP-17")]
    [Category("Sensitive")] // wall-clock deadline bound
    public void WaitForDurable_NeverDurable_TimesOutAtItsDeadline()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(20));
            var start = Stopwatch.GetTimestamp();
            Assert.Throws<WalBackPressureTimeoutException>(() => writer.WaitForDurable(1, ref ctx));
            Assert.That(Stopwatch.GetElapsedTime(start), Is.LessThan(TimeSpan.FromMilliseconds(80)));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>
    /// A waiter blocked on an LSN the writer then fails to write wakes on the failure, not on the next backstop slice, and throws the write failure.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("WP-17")]
    [Category("Sensitive")] // wall-clock wake bound, below the 100 ms backstop slice
    public void WaitForDurable_WriterFails_WakesTheBlockedWaiterAtOnce()
    {
        using var io = new FailingRecordWriteFileIO(_fileIO);
        var (buffer, writer, segMgr) = CreateWriterPipeline(fileIO: io);
        try
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xEE);
            buffer.Publish(ref claim);

            var waiter = BlockedWaiter.Start(writer, 1, WaitContext.FromTimeout(TimeSpan.FromSeconds(3)));
            var firedAt = Stopwatch.GetTimestamp();
            writer.Start();

            var (error, wokeAt) = waiter.Join();
            Assert.That(error, Is.InstanceOf<WalWriteException>());
            Assert.That(Stopwatch.GetElapsedTime(firedAt, wokeAt), Is.LessThan(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>A waiter whose token is cancelled wakes on the cancellation, not on the next backstop slice.</summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("WP-17")]
    [Category("Sensitive")] // wall-clock wake bound, below the 100 ms backstop slice
    public void WaitForDurable_TokenCancelled_WakesTheBlockedWaiterAtOnce()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        using var cts = new CancellationTokenSource();
        try
        {
            var waiter = BlockedWaiter.Start(writer, 1, WaitContext.FromTimeout(TimeSpan.FromSeconds(3), cts.Token));
            var firedAt = Stopwatch.GetTimestamp();
            cts.Cancel();

            var (error, wokeAt) = waiter.Join();
            Assert.That(error, Is.InstanceOf<WalBackPressureTimeoutException>());
            Assert.That(Stopwatch.GetElapsedTime(firedAt, wokeAt), Is.LessThan(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>
    /// Disposing the writer wakes a waiter whose LSN can no longer become durable, which throws <see cref="ObjectDisposedException"/> instead of sleeping
    /// to its deadline.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("WP-17")]
    public void WaitForDurable_WriterDisposed_WakesTheBlockedWaiter()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            var waiter = BlockedWaiter.Start(writer, 1, WaitContext.FromTimeout(TimeSpan.FromSeconds(3)));
            writer.Dispose();

            var (error, _) = waiter.Join();
            Assert.That(error, Is.InstanceOf<ObjectDisposedException>());
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>A thread blocked in <see cref="WalWriter.WaitForDurable"/>: <see cref="Start"/> returns once it is inside the slow wait.</summary>
    private sealed class BlockedWaiter
    {
        private readonly Thread _thread;
        private Exception _error;
        private long _wokeAt;

        private BlockedWaiter(WalWriter writer, long lsn, WaitContext ctx)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    var c = ctx;
                    writer.WaitForDurable(lsn, ref c);
                }
                catch (Exception ex)
                {
                    _error = ex;
                }

                _wokeAt = Stopwatch.GetTimestamp();
            }) { IsBackground = true };
        }

        public static BlockedWaiter Start(WalWriter writer, long lsn, WaitContext ctx)
        {
            var waiter = new BlockedWaiter(writer, lsn, ctx);
            waiter._thread.Start();
            var spin = new SpinWait();
            while (writer.DurabilityWaitersForTest == 0)
            {
                spin.SpinOnce(-1);
            }

            // Registered; give it the few instructions to its Monitor.Wait, so the event under test lands on a sleeping waiter.
            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1_000;
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }

            return waiter;
        }

        public (Exception Error, long WokeAt) Join()
        {
            Assert.That(_thread.Join(TimeSpan.FromSeconds(3)), Is.True, "the waiter never returned");
            return (_error, _wokeAt);
        }
    }

    /// <summary>An in-memory WAL backend whose record writes fail (segment headers pass): the writer's fatal-error path, on demand.</summary>
    private sealed class FailingRecordWriteFileIO(InMemoryWalFileIO inner) : IWalFileIO
    {
        public SafeFileHandle OpenSegment(string path, bool withFUA) => inner.OpenSegment(path, withFUA);

        public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data)
        {
            if (offset >= WalSegmentHeader.SizeInBytes)
            {
                throw new IOException("simulated WAL write failure");
            }

            inner.WriteAligned(handle, offset, data);
        }

        public void FlushBuffers(SafeFileHandle handle) => inner.FlushBuffers(handle);

        public void PreAllocate(SafeFileHandle handle, long size) => inner.PreAllocate(handle, size);

        public void ReadAligned(SafeFileHandle handle, long offset, Span<byte> buffer) => inner.ReadAligned(handle, offset, buffer);

        public SafeFileHandle OpenSegmentForRead(string path) => inner.OpenSegmentForRead(path);

        public bool Exists(string path) => inner.Exists(path);

        public void Delete(string path) => inner.Delete(path);

        public IReadOnlyList<string> EnumerateSegmentPaths(string directory) => inner.EnumerateSegmentPaths(directory);

        public void Dispose()
        {
        }
    }

    #endregion
}
