// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (the struct only carries metadata for the generator).
#pragma warning disable CS0282
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

// ═══════════════════════════════════════════════════════════════════════
// OS thread context-switch (kind 254) — instant-style record produced by
// EtwSchedulingPump on every CSwitchOldThread that closes an ON-CPU slice
// for a Typhon-registered OS thread.
//
// Wire layout: 12 B common header + 13 B payload = 25 B record.
//   Common header timestamp = the slice's START QPC tick (historical).
//   Common header threadSlot = the pump's own slot (it's the producer).
//   Payload field TargetSlotIdx = the slot the slice belongs to (re-projection target).
//
// Stopwatch.GetTimestamp() on Windows IS QueryPerformanceCounter, so the
// ETW data.TimeStampQPC values cross-walk directly into the trace's time
// space without conversion.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// One ON-CPU slice closed for a Typhon-registered thread. Generator emits
/// <c>EmitThreadContextSwitch(long timestamp, byte targetSlotIdx, byte processorNumber, ThreadWaitReason waitReason,
/// byte threadState, bool gettingIdle, uint durationQpc, uint readyTimeQpc)</c> on <c>TyphonEvent</c>.
/// </summary>
[TraceEvent(TraceEventKind.ThreadContextSwitch, Shape = TraceEventShape.Instant, Gate = "RuntimeThreadSchedulingActive")]
internal ref partial struct ThreadContextSwitchEvent
{
    /// <summary>Re-projection target: the slot index of the Typhon thread whose ON-CPU slice this record describes.</summary>
    [BeginParam] public byte TargetSlotIdx;
    /// <summary>Logical CPU index (0-based) the slice ran on. HT siblings count as distinct processors.</summary>
    [BeginParam] public byte ProcessorNumber;
    /// <summary>Why the thread left the CPU at the END of this slice (from <c>CSwitchTraceData.OldThreadWaitReason</c>).</summary>
    [BeginParam] public ThreadWaitReason WaitReason;
    /// <summary>Post-switch <see cref="System.Diagnostics.ThreadState"/> of the leaving thread (raw byte for wire stability).</summary>
    [BeginParam] public byte ThreadState;
    /// <summary>1 when the CPU went to the System Idle thread next (no contender); 0 when a real thread took the core.</summary>
    [BeginParam] public bool GettingIdle;
    /// <summary>Duration of the ON-CPU slice in QPC ticks. Capped at <c>uint.MaxValue</c> (~7 minutes at 10 MHz QPC).</summary>
    [BeginParam] public uint DurationQpc;
    /// <summary>
    /// QPC ticks the thread spent on the ready queue immediately before this slice started — measured from
    /// <c>DispatcherReadyThread</c> to <c>CSwitchNewThread</c>. <c>0</c> = unknown (no Ready event observed
    /// between slices). <c>uint.MaxValue</c> = saturated (the actual queue wait exceeded what the field can hold).
    /// Pure scheduler-pressure indicator: large values mean peers kept the CPU even after this thread became runnable.
    /// </summary>
    [BeginParam] public uint ReadyTimeQpc;
}
