using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Thrown by <see cref="ChaosWalFileIO"/> when a configured simulated crash point is reached. The crash test harness catches this to stop the workload and begin
/// the recovery phase against the post-crash on-disk state. Distinct from a real engine exception so tests never mistake injected faults for genuine failures.
/// </summary>
internal sealed class ChaosSimulatedCrashException : Exception
{
    /// <summary>The global write/flush sequence number at which the crash was injected.</summary>
    public int CrashSequence { get; }

    /// <summary>The subsystem in which the crash occurred.</summary>
    public IoSubsystem Subsystem { get; }

    public ChaosSimulatedCrashException(int sequence, IoSubsystem subsystem)
        : base($"Simulated crash at sequence {sequence} in {subsystem}")
    {
        CrashSequence = sequence;
        Subsystem = subsystem;
    }
}
