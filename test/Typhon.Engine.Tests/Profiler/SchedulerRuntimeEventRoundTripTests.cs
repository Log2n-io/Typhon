using NUnit.Framework;
using System;
using Typhon.Profiler;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 19 Scheduler & Runtime event codecs added in Phase 4 (#282) +
/// the wire-additive payload extension on the existing <see cref="TraceEventKind.SystemSkipped"/> (kind 12).
/// </summary>
[TestFixture]
public class SchedulerRuntimeEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    // ─────────────────────────────────────────────────────────────────────
    // SystemSkipped wire-additive extension (kind 12)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SystemSkipped_Extended_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[InstantEventCodec.SystemSkippedSize];
        InstantEventCodec.WriteSystemSkipped(buf, ThreadSlot, StartTs,
            systemIndex: 42, skipReason: 3, wouldBeChunkCount: 16, successorsUnblocked: 5, out _);

        // Existing Decode reads only the first 3 bytes (legacy compat); confirm it still works.
        var d = InstantEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.SystemSkipped));
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.Timestamp, Is.EqualTo(StartTs));
        Assert.That(d.P1, Is.EqualTo(42));    // systemIdx
        Assert.That(d.P2, Is.EqualTo(3));     // skipReason
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:System (kinds 146-149)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SchedulerSystemStartExecution_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerSystemEventCodec.StartExecutionSize];
        SchedulerSystemEventCodec.WriteStartExecution(buf, ThreadSlot, StartTs, sysIdx: 42);
        var d = SchedulerSystemEventCodec.DecodeStartExecution(buf);
        Assert.That(d.SysIdx, Is.EqualTo(42));
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.Timestamp, Is.EqualTo(StartTs));
    }

    [TestCase((ushort)5, (byte)0, 1234u)]
    [TestCase((ushort)999, (byte)2, 0u)]
    public void SchedulerSystemCompletion_RoundTrip(ushort sysIdx, byte reason, uint durationUs)
    {
        Span<byte> buf = stackalloc byte[SchedulerSystemEventCodec.CompletionSize];
        SchedulerSystemEventCodec.WriteCompletion(buf, ThreadSlot, StartTs, sysIdx, reason, durationUs);
        var d = SchedulerSystemEventCodec.DecodeCompletion(buf);
        Assert.That(d.SysIdx, Is.EqualTo(sysIdx));
        Assert.That(d.Reason, Is.EqualTo(reason));
        Assert.That(d.DurationUs, Is.EqualTo(durationUs));
    }

    [Test]
    public void SchedulerSystemQueueWait_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerSystemEventCodec.QueueWaitSize];
        SchedulerSystemEventCodec.WriteQueueWait(buf, ThreadSlot, StartTs, sysIdx: 7, queueWaitUs: 500);
        var d = SchedulerSystemEventCodec.DecodeQueueWait(buf);
        Assert.That(d.SysIdx, Is.EqualTo(7));
        Assert.That(d.QueueWaitUs, Is.EqualTo(500));
    }

    [Test]
    public void SchedulerSystemSingleThreaded_RoundTrip()
    {
        var ev = new SchedulerSystemSingleThreadedEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SysIdx = 12,
            IsParallelQuery = 1,
            ChunkCount = 4,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SchedulerSystemEventCodec.DecodeSingleThreaded(buf);
        Assert.That(d.SysIdx, Is.EqualTo(12));
        Assert.That(d.IsParallelQuery, Is.EqualTo(1));
        Assert.That(d.ChunkCount, Is.EqualTo(4));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:Worker (kinds 150-152)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SchedulerWorkerIdle_RoundTrip()
    {
        var ev = new SchedulerWorkerIdleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WorkerId = 3,
            SpinCount = 150,
            IdleUs = 1500,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SchedulerWorkerEventCodec.DecodeIdle(buf);
        Assert.That(d.WorkerId, Is.EqualTo(3));
        Assert.That(d.SpinCount, Is.EqualTo(150));
        Assert.That(d.IdleUs, Is.EqualTo(1500));
    }

    [Test]
    public void SchedulerWorkerWake_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerWorkerEventCodec.WakeSize];
        SchedulerWorkerEventCodec.WriteWake(buf, ThreadSlot, StartTs, workerId: 2, delayUs: 1234);
        var d = SchedulerWorkerEventCodec.DecodeWake(buf);
        Assert.That(d.WorkerId, Is.EqualTo(2));
        Assert.That(d.DelayUs, Is.EqualTo(1234));
    }

    [TestCase((byte)0)]  // signal
    [TestCase((byte)1)]  // shutdown
    public void SchedulerWorkerBetweenTick_RoundTrip(byte wakeReason)
    {
        var ev = new SchedulerWorkerBetweenTickEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WorkerId = 1,
            WaitUs = 16000,
            WakeReason = wakeReason,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SchedulerWorkerEventCodec.DecodeBetweenTick(buf);
        Assert.That(d.WorkerId, Is.EqualTo(1));
        Assert.That(d.WaitUs, Is.EqualTo(16000));
        Assert.That(d.WakeReason, Is.EqualTo(wakeReason));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:Dispense (kind 153)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SchedulerDispense_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerDispenseEventCodec.Size];
        SchedulerDispenseEventCodec.Write(buf, ThreadSlot, StartTs, sysIdx: 9, chunkIdx: 17, workerId: 4);
        var d = SchedulerDispenseEventCodec.Decode(buf);
        Assert.That(d.SysIdx, Is.EqualTo(9));
        Assert.That(d.ChunkIdx, Is.EqualTo(17));
        Assert.That(d.WorkerId, Is.EqualTo(4));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:Dependency (kinds 154-155)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SchedulerDependencyReady_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerDependencyEventCodec.ReadySize];
        SchedulerDependencyEventCodec.WriteReady(buf, ThreadSlot, StartTs, fromSysIdx: 5, toSysIdx: 10, fanOut: 3, predRemain: 0);
        var d = SchedulerDependencyEventCodec.DecodeReady(buf);
        Assert.That(d.FromSysIdx, Is.EqualTo(5));
        Assert.That(d.ToSysIdx, Is.EqualTo(10));
        Assert.That(d.FanOut, Is.EqualTo(3));
        Assert.That(d.PredRemain, Is.EqualTo(0));
    }

    [Test]
    public void SchedulerDependencyFanOut_RoundTrip()
    {
        var ev = new SchedulerDependencyFanOutEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            CompletingSysIdx = 5,
            SuccCount = 8,
            SkippedCount = 2,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SchedulerDependencyEventCodec.DecodeFanOut(buf);
        Assert.That(d.CompletingSysIdx, Is.EqualTo(5));
        Assert.That(d.SuccCount, Is.EqualTo(8));
        Assert.That(d.SkippedCount, Is.EqualTo(2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:Overload (kinds 156-158)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SchedulerOverloadLevelChange_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerOverloadEventCodec.LevelChangeSize];
        SchedulerOverloadEventCodec.WriteLevelChange(buf, ThreadSlot, StartTs,
            prevLvl: 0, newLvl: 1, ratio: 1.25f, queueDepth: 100, oldMul: 1, newMul: 2);
        var d = SchedulerOverloadEventCodec.DecodeLevelChange(buf);
        Assert.That(d.PrevLvl, Is.EqualTo(0));
        Assert.That(d.NewLvl, Is.EqualTo(1));
        Assert.That(d.Ratio, Is.EqualTo(1.25f));
        Assert.That(d.QueueDepth, Is.EqualTo(100));
        Assert.That(d.OldMul, Is.EqualTo(1));
        Assert.That(d.NewMul, Is.EqualTo(2));
    }

    [TestCase((byte)0)]  // runIfFalse
    [TestCase((byte)1)]  // throttled
    [TestCase((byte)2)]  // shed
    public void SchedulerOverloadSystemShed_RoundTrip(byte decision)
    {
        Span<byte> buf = stackalloc byte[SchedulerOverloadEventCodec.SystemShedSize];
        SchedulerOverloadEventCodec.WriteSystemShed(buf, ThreadSlot, StartTs, sysIdx: 7, level: 1, divisor: 4, decision);
        var d = SchedulerOverloadEventCodec.DecodeSystemShed(buf);
        Assert.That(d.SysIdx, Is.EqualTo(7));
        Assert.That(d.Level, Is.EqualTo(1));
        Assert.That(d.Divisor, Is.EqualTo(4));
        Assert.That(d.Decision, Is.EqualTo(decision));
    }

    [Test]
    public void SchedulerOverloadTickMultiplier_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[SchedulerOverloadEventCodec.TickMultiplierSize];
        SchedulerOverloadEventCodec.WriteTickMultiplier(buf, ThreadSlot, StartTs, tick: 1000, multiplier: 2, intervalTicks: 8);
        var d = SchedulerOverloadEventCodec.DecodeTickMultiplier(buf);
        Assert.That(d.Tick, Is.EqualTo(1000));
        Assert.That(d.Multiplier, Is.EqualTo(2));
        Assert.That(d.IntervalTicks, Is.EqualTo(8));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:Graph (kinds 159-160)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void SchedulerGraphBuild_RoundTrip()
    {
        var ev = new SchedulerGraphBuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SysCount = 10,
            EdgeCount = 20,
            TopoLen = 10,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SchedulerGraphEventCodec.DecodeBuild(buf);
        Assert.That(d.SysCount, Is.EqualTo(10));
        Assert.That(d.EdgeCount, Is.EqualTo(20));
        Assert.That(d.TopoLen, Is.EqualTo(10));
    }

    [Test]
    public void SchedulerGraphRebuild_RoundTrip()
    {
        var ev = new SchedulerGraphRebuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            OldSysCount = 10,
            NewSysCount = 12,
            Reason = 1,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = SchedulerGraphEventCodec.DecodeRebuild(buf);
        Assert.That(d.OldSysCount, Is.EqualTo(10));
        Assert.That(d.NewSysCount, Is.EqualTo(12));
        Assert.That(d.Reason, Is.EqualTo(1));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Runtime (kinds 161-164)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void RuntimePhaseUoWCreate_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[RuntimeEventCodec.UoWCreateSize];
        RuntimeEventCodec.WriteUoWCreate(buf, ThreadSlot, StartTs, tick: 12345);
        var d = RuntimeEventCodec.DecodeUoWCreate(buf);
        Assert.That(d.Tick, Is.EqualTo(12345));
    }

    [Test]
    public void RuntimePhaseUoWFlush_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[RuntimeEventCodec.UoWFlushSize];
        RuntimeEventCodec.WriteUoWFlush(buf, ThreadSlot, StartTs, tick: 12345, changeCount: 42);
        var d = RuntimeEventCodec.DecodeUoWFlush(buf);
        Assert.That(d.Tick, Is.EqualTo(12345));
        Assert.That(d.ChangeCount, Is.EqualTo(42));
    }

    [Test]
    public void RuntimeTransactionLifecycle_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[RuntimeEventCodec.ComputeSizeLifecycle(false)];
        RuntimeEventCodec.EncodeLifecycle(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            sysIdx: 7, txDurUs: 500, success: 1, out _);
        var d = RuntimeEventCodec.DecodeLifecycle(buf);
        Assert.That(d.SysIdx, Is.EqualTo(7));
        Assert.That(d.TxDurUs, Is.EqualTo(500));
        Assert.That(d.Success, Is.EqualTo(1));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scheduler:SystemArchetype (kind 245) — Workbench Data Flow module (#327)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((ushort)0,   (ushort)0,    0,       0)]
    [TestCase((ushort)1,   (ushort)100,  1,       1)]
    [TestCase((ushort)42,  (ushort)256,  12_400,  16)]
    [TestCase((ushort)999, (ushort)4095, 1_000_000, ushort.MaxValue)]
    public void SchedulerSystemArchetype_RoundTrip(ushort sysIdx, ushort archetypeId, int entityCount, int chunkCount)
    {
        var ev = new SchedulerSystemArchetypeEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SystemIndex = sysIdx,
            ArchetypeId = archetypeId,
            EntityCount = entityCount,
            ChunkCount = chunkCount,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);

        var d = SchedulerSystemArchetypeEventCodec.Decode(buf);
        Assert.That(d.SystemIndex, Is.EqualTo(sysIdx));
        Assert.That(d.ArchetypeId, Is.EqualTo(archetypeId));
        Assert.That(d.EntityCount, Is.EqualTo(entityCount));
        Assert.That(d.ChunkCount, Is.EqualTo(chunkCount));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.StartTimestamp, Is.EqualTo(StartTs));
        Assert.That(d.SpanId, Is.EqualTo(SpanId));
        Assert.That(d.ParentSpanId, Is.EqualTo(ParentSpanId));
        Assert.That(d.HasTraceContext, Is.False);
    }

    [Test]
    public void RuntimeSubscriptionOutputExecute_RoundTrip()
    {
        var ev = new RuntimeSubscriptionOutputExecuteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tick = 5000,
            Level = 1,
            ClientCount = 50,
            ViewsRefreshed = 10,
            DeltasPushed = 1234,
            OverflowCount = 3,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = RuntimeEventCodec.DecodeOutputExecute(buf);
        Assert.That(d.Tick, Is.EqualTo(5000));
        Assert.That(d.Level, Is.EqualTo(1));
        Assert.That(d.ClientCount, Is.EqualTo(50));
        Assert.That(d.ViewsRefreshed, Is.EqualTo(10));
        Assert.That(d.DeltasPushed, Is.EqualTo(1234));
        Assert.That(d.OverflowCount, Is.EqualTo(3));
    }
}
