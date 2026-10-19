using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Process-wide switches for the data-file write chokepoint, read once from the environment. <c>static readonly</c>, so the JIT folds them in
/// Tier 1 and an A/B of their cost runs on one binary.
/// </summary>
/// <remarks>
/// <para>
/// <c>TYPHON_DATAFILE_TRACKING=0</c> turns the change map off for the process: an ops kill-switch. A backup then never trusts the map, so every point
/// is full; a wrong setting costs I/O, never correctness.
/// </para>
/// <para>
/// <c>TYPHON_DATAFILE_GATE=0</c> also skips the write gate and the copy-before-write hook. It exists to measure their cost and nothing else: with it
/// set, a freeze cannot exclude in-flight writes, so online backup refuses to capture.
/// </para>
/// </remarks>
internal static class DataFileWriteTracking
{
    /// <summary>Whether the change map records writes.</summary>
    internal static readonly bool MapEnabled = Environment.GetEnvironmentVariable("TYPHON_DATAFILE_TRACKING") != "0";

    /// <summary>Whether data-file writes pass the write gate and the copy-before-write hook.</summary>
    internal static readonly bool GateEnabled = Environment.GetEnvironmentVariable("TYPHON_DATAFILE_GATE") != "0";
}
