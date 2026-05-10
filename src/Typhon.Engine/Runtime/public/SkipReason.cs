using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Reason a system was skipped during tick execution.
/// </summary>
[PublicAPI]
public enum SkipReason : byte
{
    /// <summary>System was not skipped — it executed normally.</summary>
    NotSkipped = 0,

    /// <summary>System's <c>runIf</c> predicate returned false.</summary>
    RunIfFalse = 1,

    /// <summary>System's input source was empty (no entities to process).</summary>
    EmptyInput = 2,

    /// <summary>System's event queue was empty (no events to consume).</summary>
    EmptyEvents = 3,

    /// <summary>System was throttled by overload management (#201).</summary>
    Throttled = 4,

    /// <summary>System was shed by overload management (#201).</summary>
    Shed = 5,

    /// <summary>System threw an exception during execution.</summary>
    Exception = 6,

    /// <summary>System was skipped because a predecessor system failed with an exception.</summary>
    DependencyFailed = 7
}
