using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Why a tick ended the way it did. Carried by <see cref="TickOutcome.Reason"/>.
/// </summary>
[PublicAPI]
public enum TickOutcomeReason : byte
{
    /// <summary>
    /// The tick completed as its policy promises. Note this includes a tick under <see cref="SystemExceptionPolicy.Isolate"/> in which a system threw and its
    /// branch was skipped — fault isolation is that policy's contract, so such a tick did not fail. Per-system detail lives in
    /// <see cref="SkipReason.Exception"/> in the telemetry ring.
    /// </summary>
    Success = 0,

    /// <summary>
    /// A system threw under <see cref="SystemExceptionPolicy.AbortTickAndStop"/> and the rest of the tick was cancelled. The runtime is now terminal.
    /// </summary>
    SystemException = 1,

    /// <summary>
    /// A system on an engine-internal track (the Fence DAG) threw. This is an engine fault rather than a user-system failure, and it is never reported as a
    /// tick abort — the fence is the work that must complete, so there is no "rest of the tick" left to cancel.
    /// </summary>
    FenceFailure = 2
}

/// <summary>
/// Outcome of the most recently completed tick, exposed by <see cref="TyphonRuntime.LastTickOutcome"/> and passed to <see cref="TyphonRuntime.OnTickAborted"/>.
/// </summary>
/// <remarks>
/// This is a <b>tick-level</b> verdict, not a system-level one, and it is refreshed on every tick under every policy — a stale outcome can never be mistaken
/// for a fresh one. Overload shedding never produces a non-<see cref="Succeeded"/> outcome: a shed system is skipped by design, not by failure. Degradation is
/// read from the overload level; failure is read from here. The two are orthogonal.
/// </remarks>
[PublicAPI]
public readonly struct TickOutcome
{
    /// <summary>The tick this outcome describes.</summary>
    public long TickNumber { get; }

    /// <summary>Why the tick ended the way it did.</summary>
    public TickOutcomeReason Reason { get; }

    /// <summary>Index of the first system to fail, or <c>-1</c> when <see cref="Succeeded"/> is true.</summary>
    public int FailedSystemIndex { get; }

    /// <summary>Name of the first system to fail, or <c>null</c> when <see cref="Succeeded"/> is true.</summary>
    public string FailedSystemName { get; }

    /// <summary>The exception that ended the tick, or <c>null</c> when <see cref="Succeeded"/> is true.</summary>
    public Exception FailedSystemException { get; }

    /// <summary>True when the tick completed as its policy promises.</summary>
    public bool Succeeded => Reason == TickOutcomeReason.Success;

    /// <summary>Creates an outcome. Use <see cref="ForSuccess"/> for the success case.</summary>
    /// <param name="tickNumber">The tick this outcome describes.</param>
    /// <param name="reason">Why the tick ended the way it did.</param>
    /// <param name="failedSystemIndex">Index of the first failing system, or <c>-1</c>.</param>
    /// <param name="failedSystemName">Name of the first failing system, or <c>null</c>.</param>
    /// <param name="failedSystemException">The exception that ended the tick, or <c>null</c>.</param>
    public TickOutcome(long tickNumber, TickOutcomeReason reason, int failedSystemIndex, string failedSystemName, Exception failedSystemException)
    {
        TickNumber = tickNumber;
        Reason = reason;
        FailedSystemIndex = failedSystemIndex;
        FailedSystemName = failedSystemName;
        FailedSystemException = failedSystemException;
    }

    /// <summary>Creates the outcome for a tick that completed as its policy promises.</summary>
    /// <param name="tickNumber">The tick this outcome describes.</param>
    public static TickOutcome ForSuccess(long tickNumber) => new(tickNumber, TickOutcomeReason.Success, -1, null, null);
}
