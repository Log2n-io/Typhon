using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Drives an <see cref="AttachSessionRuntime"/> against a scripted <see cref="MockTcpProfilerServer"/> and collects the
/// tick summaries it produces. The harness for on-demand tick capture (#805).
/// </summary>
/// <remarks>
/// <para>
/// <b>No sleeps.</b> Tick summaries are collected from the runtime's <c>TickSummaryAdded</c> event and awaited through a
/// <see cref="TaskCompletionSource"/>, so tests synchronise on the thing they actually care about rather than on a
/// guessed delay. A test that polls a timer here would be flaky on a loaded CI box and slow everywhere else.
/// </para>
/// <para>
/// <b>Tick timing.</b> The mock Init header declares a 10 MHz timestamp frequency, so 10 stopwatch ticks = 1 µs. Ticks
/// are laid out <see cref="TickSpacing"/> apart with a <see cref="TickBodyTicks"/>-long body, which keeps the arithmetic
/// in the assertions readable.
/// </para>
/// </remarks>
internal sealed class CaptureHarness : IAsyncDisposable
{
    /// <summary>Stopwatch ticks per microsecond, given the mock Init header's 10 MHz frequency.</summary>
    public const long TicksPerUs = 10;

    /// <summary>Stopwatch ticks between consecutive tick starts.</summary>
    public const long TickSpacing = 10_000;

    /// <summary>
    /// Timestamp the first synthesized tick starts at. Must be strictly positive.
    /// </summary>
    /// <remarks>
    /// <c>IncrementalCacheBuilder.FinalizeCurrentTick</c> opens with <c>if (_currentTickFirstTs &lt;= 0) return;</c>, so a
    /// tick whose <c>TickStart</c> carries timestamp 0 is silently discarded and never becomes a <c>TickSummary</c>. A
    /// real engine cannot hit this — <c>Stopwatch.GetTimestamp()</c> is QPC since boot — but a fixture starting its
    /// timeline at zero can, and the resulting off-by-one in the summary list is invisible until an assertion indexes
    /// the wrong tick. Starting at a positive base removes the trap.
    /// </remarks>
    public const long TickTimeBase = 1_000_000;

    /// <summary>A timestamp <paramref name="offset"/> stopwatch ticks into the synthesized timeline.</summary>
    public static long At(long offset) => TickTimeBase + offset;

    /// <summary>Stopwatch ticks from TickStart to TickEnd.</summary>
    public const long TickBodyTicks = 5_000;

    /// <summary>Stopwatch ticks from TickStart to the metronome-wait record that seals the tick.</summary>
    public const long TickSealTicks = 9_000;

    /// <summary>
    /// Exempt records <see cref="BuildTick"/> emits per tick with gauges and seal enabled: <c>TickStart</c>,
    /// <c>TickEnd</c>, <c>PerTickSnapshot</c>, <c>SchedulerOverloadDetector</c>, <c>SchedulerMetronomeWait</c>.
    /// </summary>
    /// <remarks>
    /// An idle tick's <c>EventCount</c> settles on exactly this number, not zero: the builder counts every record it is
    /// fed, and the exempt skeleton is genuinely present in the chunk. Measured, not assumed — see
    /// <c>CaptureFilterTests.Idle_DropsDetailRecords_ButKeepsTheTickSpine</c>.
    /// </remarks>
    public const int ExemptRecordsPerTick = 5;

    private readonly List<TickSummaryDto> _summaries = [];
    private readonly object _gate = new();
    private TaskCompletionSource _summaryArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MockTcpProfilerServer Server { get; private init; }

    public AttachSessionRuntime Runtime { get; private init; }

    public static async Task<CaptureHarness> StartAsync(CaptureMode mode, CancellationToken ct = default)
    {
        var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        var runtime = await AttachSessionRuntime.StartAsync(
            Guid.NewGuid(), $"127.0.0.1:{server.Port}", NullLogger.Instance, ct, mode);

        var harness = new CaptureHarness { Server = server, Runtime = runtime };
        runtime.TickSummaryAdded += harness.OnTickSummary;
        await server.WaitForClientAsync(ct);
        return harness;
    }

    private void OnTickSummary(TickSummaryDto summary)
    {
        lock (_gate)
        {
            _summaries.Add(summary);
            _summaryArrived.TrySetResult();
            _summaryArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Snapshot of the tick summaries finalized so far.</summary>
    public IReadOnlyList<TickSummaryDto> Summaries
    {
        get
        {
            lock (_gate)
            {
                return [.. _summaries];
            }
        }
    }

    /// <summary>
    /// Send one tick's worth of records as a single Block frame.
    /// </summary>
    /// <param name="tickIndex">Zero-based index used to lay the tick out in time.</param>
    /// <param name="engineTick">The absolute tick number the engine reports inside its gauge record.</param>
    /// <param name="detailRecords">How many filterable detail records (SchedulerChunk) to include.</param>
    /// <param name="includeGauges">Whether to emit the <c>PerTickSnapshot</c>. Off models an engine running without gauges.</param>
    /// <param name="includeSeal">Whether to emit the trailing <c>SchedulerMetronomeWait</c> that extends the tick.</param>
    public Task SendTickAsync(int tickIndex, uint engineTick, int detailRecords = 0, bool includeGauges = true, bool includeSeal = true,
        CancellationToken ct = default)
        => SendBlockAsync(BuildTick(tickIndex, engineTick, detailRecords, includeGauges, includeSeal), ct);

    /// <summary>
    /// Send one Block frame and return only once the runtime has actually consumed it.
    /// </summary>
    /// <remarks>
    /// <b>This barrier is load-bearing, not defensive.</b> <see cref="MockTcpProfilerServer.SendBlockAsync"/> completes
    /// when the bytes reach the socket, which says nothing about whether the read loop has processed them. Any test that
    /// sends records and then calls <see cref="AttachSessionRuntime.Arm"/> is asserting on an ordering between the two,
    /// so without this barrier the arm can land before or after the records it is meant to follow — and the test then
    /// passes or fails on scheduler luck. Synchronising on the runtime's own received-bytes counter makes the ordering
    /// exact.
    /// </remarks>
    public async Task SendBlockAsync(byte[] records, CancellationToken ct = default)
    {
        var before = Runtime.CaptureState.BytesReceived;
        await Server.SendBlockAsync(records, ct);
        var target = before + records.Length;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Runtime.CaptureState.BytesReceived < target)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Runtime did not consume the block: expected {target} bytes received, saw {Runtime.CaptureState.BytesReceived}.");
            }
            await Task.Delay(2, ct);
        }
    }

    /// <summary>Build one tick's records without sending them, so a test can choose its own block boundaries.</summary>
    public static byte[] BuildTick(int tickIndex, uint engineTick, int detailRecords = 0, bool includeGauges = true, bool includeSeal = true)
    {
        var start = At(tickIndex * TickSpacing);
        var parts = new List<byte[]> { MockRecordFactory.TickStart(start) };

        for (var i = 0; i < detailRecords; i++)
        {
            parts.Add(MockRecordFactory.SchedulerChunk(start + 100 + i * 10, durationTicks: 50, systemIndex: (ushort)(i % 8)));
        }

        parts.Add(MockRecordFactory.TickEnd(start + TickBodyTicks, overloadLevel: 0, tickMultiplier: 1));
        if (includeGauges)
        {
            parts.Add(MockRecordFactory.PerTickSnapshot(start + TickBodyTicks + 10, engineTick));
        }
        parts.Add(MockRecordFactory.OverloadDetector(start + TickBodyTicks + 20, tick: engineTick, consecutiveOverrun: 1, consecutiveUnderrun: 2));
        if (includeSeal)
        {
            parts.Add(MockRecordFactory.MetronomeWait(start + TickSealTicks, durationTicks: 200));
        }

        return MockRecordFactory.Concat([.. parts]);
    }

    /// <summary>
    /// Await until at least <paramref name="count"/> tick summaries have been finalized. Throws on timeout so a hung
    /// expectation fails loudly instead of hanging the suite.
    /// </summary>
    public async Task WaitForSummariesAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            Task next;
            lock (_gate)
            {
                if (_summaries.Count >= count)
                {
                    return;
                }
                next = _summaryArrived.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Expected {count} tick summaries, saw {Summaries.Count} within the timeout.");
            }
            try
            {
                await next.WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Expected {count} tick summaries, saw {Summaries.Count} within the timeout.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Runtime.TickSummaryAdded -= OnTickSummary;
        Runtime.Dispose();
        await Server.DisposeAsync();
    }
}
