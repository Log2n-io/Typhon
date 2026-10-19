using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Process-wide switches for the data-file write chokepoint, read once from the environment: two ops escape hatches, also the same-binary A/B of their
/// cost. <c>static readonly</c>, so the JIT folds them in Tier 1. The engine logs a warning at open when either is set.
/// </summary>
/// <remarks>
/// <para>
/// <c>TYPHON_DATAFILE_TRACKING=0</c> turns the change map off for the process. The gate and copy-before-write stay, so online backup still works, but
/// the lineage is never trusted and every point is full: a wrong setting costs I/O, never correctness.
/// </para>
/// <para>
/// <c>TYPHON_DATAFILE_GATE=0</c> also skips the write gate and the copy-before-write hook, for the day the gate itself misbehaves in production. A
/// freeze can then no longer exclude in-flight writes, so online capture is refused (<c>BackupAbortReason.TrackingDisabled</c>); offline capture of
/// a closed database still works.
/// </para>
/// </remarks>
internal static class DataFileWriteTracking
{
    /// <summary>Whether the change map records writes.</summary>
    internal static readonly bool MapEnabled = Environment.GetEnvironmentVariable("TYPHON_DATAFILE_TRACKING") != "0";

    /// <summary>Whether data-file writes pass the write gate and the copy-before-write hook.</summary>
    internal static readonly bool GateEnabled = Environment.GetEnvironmentVariable("TYPHON_DATAFILE_GATE") != "0";
}
