using JetBrains.Annotations;

namespace Typhon.Profiler;

/// <summary>
/// Reason the CLR triggered a garbage collection. Values match <c>GCStart_V2.Reason</c> from the
/// <c>Microsoft-Windows-DotNETRuntime</c> EventSource — do not renumber.
/// </summary>
[PublicAPI]
public enum GcReason : byte
{
    /// <summary>Small-object-heap allocation budget was exhausted — the most common cause.</summary>
    SmallObjectHeapAllocation = 0,

    /// <summary>Explicitly induced (e.g. <c>GC.Collect()</c>), blocking.</summary>
    Induced = 1,

    /// <summary>The OS signalled low physical memory.</summary>
    LowMemory = 2,

    /// <summary>No specific reason recorded.</summary>
    Empty = 3,

    /// <summary>Large-object-heap allocation budget was exhausted.</summary>
    LargeObjectHeapAllocation = 4,

    /// <summary>Out of space in the small object heap segment.</summary>
    OutOfSpaceSmallObjectHeap = 5,

    /// <summary>Out of space in the large object heap segment.</summary>
    OutOfSpaceLargeObjectHeap = 6,

    /// <summary>Induced but not forced to be blocking (allows a background collection).</summary>
    InducedNotForced = 7,
}

/// <summary>
/// Type classification of a garbage collection. Values match <c>GCStart_V2.Type</c> from the
/// <c>Microsoft-Windows-DotNETRuntime</c> EventSource — do not renumber.
/// </summary>
[PublicAPI]
public enum GcType : byte
{
    /// <summary>Blocking GC that ran entirely outside any background GC window.</summary>
    BlockingOutsideBackground = 0,

    /// <summary>Background (concurrent) GC.</summary>
    Background = 1,

    /// <summary>Blocking GC that ran while a background GC was active.</summary>
    BlockingDuringBackground = 2,
}

/// <summary>
/// Reason the CLR suspended the execution engine. Values match <c>GCSuspendEEBegin_V1.Reason</c> from the
/// <c>Microsoft-Windows-DotNETRuntime</c> EventSource — do not renumber.
/// </summary>
[PublicAPI]
public enum GcSuspendReason : byte
{
    /// <summary>Suspension for a reason not covered by the other values.</summary>
    Other = 0,

    /// <summary>Suspended to perform a garbage collection.</summary>
    ForGC = 1,

    /// <summary>Suspended for an AppDomain shutdown.</summary>
    ForAppDomainShutdown = 2,

    /// <summary>Suspended for code pitching (JIT code eviction).</summary>
    ForCodePitching = 3,

    /// <summary>Suspended for process shutdown.</summary>
    ForShutdown = 4,

    /// <summary>Suspended for the debugger.</summary>
    ForDebugger = 5,

    /// <summary>Suspended in preparation for a garbage collection.</summary>
    ForGCPrep = 6,

    /// <summary>Suspended for a debugger sweep.</summary>
    ForDebuggerSweep = 7,
}
